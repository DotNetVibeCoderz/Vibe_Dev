/**
 * DotCode SDK for TypeScript/JavaScript.
 * Built by Gravicode Studios, led by Kang Fadhil.
 *
 * ```ts
 * import { DotCodeClient, approveAll, defineTool, s } from "dotcode-sdk";
 *
 * await using client = new DotCodeClient();
 * await client.start();
 * await using session = await client.createSession({
 *   model: "openai:gpt-5",
 *   onPermissionRequest: approveAll,
 *   tools: [defineTool("get_weather", {
 *     description: "Weather for a city",
 *     parameters: s.object({ city: s.string().describe("City name") }),
 *     handler: ({ city }) => `${city}: sunny`,
 *   })],
 * });
 * session.on("assistant.text.delta", (e) => process.stdout.write(e.text));
 * const result = await session.sendAndWait({ prompt: "Weather in Bogor?" });
 * ```
 */
import { spawn, type ChildProcess } from "node:child_process";
import { JsonRpcPeer, JsonRpcError } from "./jsonrpc.js";
import { toJsonSchema, type SchemaLike } from "./schema.js";
import type {
  ExitPlanModeResult, MessageOptions, ModelList, PermissionDecision, PermissionMode, ReasoningEffort,
  ResumeSessionConfig, SendResult, SessionConfig, SessionEvent, SessionEventHandler, SessionEventType,
  SessionInfo, SessionMetadata, Tool, ToolHandler, ToolInfo, ToolInvocation, ToolResultObject,
  TypedSessionEventHandler,
} from "./types.js";

export * from "./types.js";
export * from "./schema.js";
export { JsonRpcError } from "./jsonrpc.js";

export const PROTOCOL_VERSION = "1.0";
const SDK_VERSION = "0.2.0";

export interface ClientOptions {
  /** Path to the dotcode executable (or dotcode.dll). Defaults to $DOTCODE_CLI_PATH or "dotcode" on PATH. */
  cliPath?: string;
  /** Extra arguments passed to `dotcode serve`. */
  cliArgs?: string[];
  /** Default working directory for sessions and of the server process. */
  cwd?: string;
  env?: Record<string, string>;
}

export type ConnectionState = "disconnected" | "connecting" | "connected";

/**
 * Defines a custom tool. The handler's argument type is inferred from `parameters` (a {@link s} builder schema or
 * a Zod 4 schema), so a misspelled property is a compile error; arguments are validated before the handler runs.
 */
export function defineTool<S extends SchemaLike<any> | undefined = undefined>(
  name: string,
  config: {
    description: string;
    parameters?: S;
    handler: ToolHandler<S extends SchemaLike<infer T> ? T : Record<string, never>>;
    readOnly?: boolean;
  },
): Tool<S extends SchemaLike<infer T> ? T : Record<string, never>> {
  return { name, ...config } as Tool<any>;
}

export class DotCodeClient {
  private proc?: ChildProcess;
  private peer?: JsonRpcPeer;
  private readonly sessions = new Map<string, DotCodeSession>();
  private starting?: Promise<void>;
  /** Events of sessions still being created (the server emits `session.started` before replying). */
  private readonly early = new Map<string, SessionEvent[]>();
  private opening = 0;
  private _state: ConnectionState = "disconnected";
  serverVersion?: string;

  constructor(private readonly options: ClientOptions = {}) {}

  get state(): ConnectionState {
    return this._state;
  }

  /** Starts `dotcode serve` and performs the protocol handshake (other methods call it lazily). */
  start(): Promise<void> {
    this.starting ??= this.doStart().catch((e) => {
      this.starting = undefined;
      this._state = "disconnected";
      throw e;
    });
    return this.starting;
  }

  private async doStart() {
    this._state = "connecting";
    const cli = this.options.cliPath ?? process.env.DOTCODE_CLI_PATH ?? "dotcode";
    const [cmd, args] = cli.endsWith(".dll") ? ["dotnet", [cli, "serve"]] : [cli, ["serve"]];
    const proc = spawn(cmd, [...args, ...(this.options.cliArgs ?? [])], {
      cwd: this.options.cwd,
      env: { ...process.env, ...this.options.env },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.proc = proc;
    await new Promise<void>((resolve, reject) => {
      proc.once("spawn", () => resolve());
      proc.once("error", (e) =>
        reject(new Error(`Could not start '${cli}'. Install the DotCode CLI or set DOTCODE_CLI_PATH. (${e.message})`)),
      );
    });
    proc.stderr!.on("data", () => {});
    const peer = new JsonRpcPeer(proc.stdout!, proc.stdin!);
    this.peer = peer;
    peer.on("notification", (method: string, params: any) => {
      if (method !== "session.event") return;
      const session = this.sessions.get(params.sessionId);
      if (session) session._dispatch(params.event as SessionEvent);
      else if (this.opening > 0) {
        const list = this.early.get(params.sessionId) ?? [];
        list.push(params.event as SessionEvent);
        this.early.set(params.sessionId, list);
      }
    });
    peer.on("close", () => (this._state = "disconnected"));
    peer.onRequest = async (method, params) => {
      const session = this.sessions.get(params?.sessionId);
      if (!session) throw new JsonRpcError(-32001, "Unknown session");
      return session._handleServerRequest(method, params);
    };
    const init = await peer.request<{ serverInfo?: { version?: string } }>("initialize", {
      protocolVersion: PROTOCOL_VERSION,
      clientInfo: { name: "dotcode-sdk-typescript", version: SDK_VERSION },
      capabilities: { permissions: true, questions: true },
    });
    this.serverVersion = init?.serverInfo?.version;
    this._state = "connected";
  }

  /** Creates a session. Without `onPermissionRequest` it is deny-by-default. */
  async createSession(config: SessionConfig = {}): Promise<DotCodeSession> {
    return this.open("session.create", config, {});
  }

  /** Resumes a saved session (or forks it with `fork: true`). */
  async resumeSession(sessionId: string, config: ResumeSessionConfig = {}): Promise<DotCodeSession> {
    return this.open("session.resume", config, { sessionId, fork: config.fork ?? false });
  }

  private async open(method: string, config: SessionConfig, extra: Record<string, unknown>) {
    await this.start();
    this.opening++;
    try {
      const info = await this.peer!.request<SessionInfo>(method, { ...toWire(config, this.options.cwd), ...extra });
      const session = new DotCodeSession(this.peer!, info, config, () => this.sessions.delete(info.sessionId));
      this.sessions.set(info.sessionId, session);
      for (const e of this.early.get(info.sessionId) ?? []) session._dispatch(e);
      return session;
    } finally {
      if (--this.opening === 0) this.early.clear();
    }
  }

  async listModels(): Promise<ModelList> {
    await this.start();
    return this.peer!.request("models.list", { cwd: this.options.cwd });
  }

  async listSessions(): Promise<SessionMetadata[]> {
    await this.start();
    const r = await this.peer!.request<{ sessions: SessionMetadata[] }>("session.list", { cwd: this.options.cwd });
    return r.sessions;
  }

  /** Round-trips a message to the server. */
  async ping(): Promise<void> {
    await this.start();
    await this.peer!.request("ping", {});
  }

  /** Disconnects all sessions and stops the server process. */
  async stop(): Promise<void> {
    const proc = this.proc;
    if (!proc) return;
    try {
      await Promise.race([this.peer!.request("shutdown"), new Promise((r) => setTimeout(r, 1500))]);
    } catch { /* ignore */ }
    proc.stdin?.end();
    const exited = new Promise<void>((r) => proc.once("exit", () => r()));
    await Promise.race([exited, new Promise((r) => setTimeout(r, 2000))]);
    if (proc.exitCode === null) proc.kill();
    this.proc = undefined;
    this.peer = undefined;
    this.starting = undefined;
    this.sessions.clear();
    this._state = "disconnected";
  }

  /** Kills the server process without a graceful shutdown. */
  forceStop(): void {
    this.proc?.kill();
    this.proc = undefined;
    this.peer = undefined;
    this.starting = undefined;
    this.sessions.clear();
    this._state = "disconnected";
  }

  async [Symbol.asyncDispose](): Promise<void> {
    await this.stop();
  }
}

function toWire(c: SessionConfig, defaultCwd?: string) {
  const settings: Record<string, unknown> = { ...(c.settings ?? {}) };
  if (c.providers) settings.providers = { ...((settings.providers as object) ?? {}), ...c.providers };
  return {
    cwd: c.workingDirectory ?? defaultCwd,
    model: c.model,
    fallbackModel: c.fallbackModel,
    permissionMode: c.permissionMode,
    systemPrompt: c.systemMessage?.mode === "replace" ? c.systemMessage.content : undefined,
    appendSystemPrompt: c.systemMessage && c.systemMessage.mode !== "replace" ? c.systemMessage.content : undefined,
    allowedTools: c.allowedTools,
    disallowedTools: c.excludedTools,
    tools: c.availableTools,
    hostTools: c.tools?.map((t) => ({
      name: t.name,
      description: t.description,
      inputSchema: toJsonSchema(t.parameters),
      readOnly: t.readOnly ?? false,
    })),
    mcpServers: c.mcpServers,
    settings: Object.keys(settings).length > 0 ? settings : undefined,
    maxTurns: c.maxTurns,
    effort: c.reasoningEffort,
    persistSession: c.persistSession ?? true,
    noMcp: c.disableMcp,
    worktree: c.worktree,
  };
}

function decisionToWire(d: PermissionDecision) {
  switch (d.kind) {
    case "approve-once": return { decision: "allow", updatedInput: d.updatedInput };
    case "approve-for-session": return { decision: "allow_session", updatedInput: d.updatedInput };
    case "approve-always": return { decision: "allow_always", rule: d.rule, updatedInput: d.updatedInput };
    case "reject": return { decision: "deny", feedback: d.feedback };
  }
}

function planToWire(r: ExitPlanModeResult) {
  if (r.approved) return { approval: r.acceptEdits ? "approve_accept_edits" : "approve" };
  return { approval: "reject", feedback: r.feedback };
}

function isToolResultObject(v: unknown): v is ToolResultObject {
  return typeof v === "object" && v !== null && typeof (v as ToolResultObject).textResultForLlm === "string";
}

function toolResultToWire(value: unknown) {
  if (typeof value === "string") return { content: value };
  if (isToolResultObject(value)) {
    const images = value.binaryResultsForLlm ?? [];
    const content = images.length === 0
      ? value.textResultForLlm
      : [{ type: "text", text: value.textResultForLlm }, ...images.map((b) => ({ type: "image", data: b.data, mediaType: b.mimeType }))];
    return { content, isError: value.resultType === "failure" };
  }
  return { content: value === undefined ? "" : JSON.stringify(value) };
}

export class DotCodeSession {
  readonly sessionId: string;
  model: string;
  private readonly handlers = new Set<SessionEventHandler>();
  private readonly tools: Map<string, Tool>;

  /** @internal */
  constructor(
    private readonly peer: JsonRpcPeer,
    public readonly info: SessionInfo,
    private readonly config: SessionConfig,
    private readonly onDisconnect: () => void,
  ) {
    this.sessionId = info.sessionId;
    this.model = info.model;
    this.tools = new Map((config.tools ?? []).map((t) => [t.name, t]));
  }

  /** Subscribes to one event type with a typed handler. Returns an unsubscribe function. */
  on<K extends SessionEventType>(type: K, handler: TypedSessionEventHandler<K>): () => void;
  /** Subscribes to all events. Returns an unsubscribe function. */
  on(handler: SessionEventHandler): () => void;
  on(a: SessionEventType | SessionEventHandler, b?: (e: any) => void): () => void {
    const handler: SessionEventHandler = typeof a === "function" ? a : (e) => { if (e.type === a) b!(e); };
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  /** @internal */
  _dispatch(e: SessionEvent) {
    if (e.type === "model.changed") this.model = e.model;
    this.config.onEvent?.(e);
    for (const h of this.handlers) {
      try { h(e); } catch { /* a failing listener must not break the session */ }
    }
  }

  /** @internal */
  async _handleServerRequest(method: string, params: any): Promise<unknown> {
    const invocation = { sessionId: this.sessionId };
    switch (method) {
      case "permission.request": {
        const handler = this.config.onPermissionRequest;
        if (!handler) return { decision: "deny", feedback: "No permission handler registered in the SDK host (deny by default)." };
        return decisionToWire(await handler(params.request, invocation));
      }
      case "user.question": {
        const handler = this.config.onUserInputRequest;
        return { answers: handler ? await handler({ questions: params.questions }, invocation) : [] };
      }
      case "plan.review": {
        const handler = this.config.onExitPlanMode;
        return handler ? planToWire(await handler({ plan: params.plan }, invocation)) : { approval: "approve" };
      }
      case "tool.call": {
        const tool = this.tools.get(params.name);
        if (!tool) throw new JsonRpcError(-32601, `Unknown host tool ${params.name}`);
        const toolInvocation: ToolInvocation = {
          sessionId: this.sessionId, toolCallId: params.toolUseId, toolName: params.name, arguments: params.input,
        };
        try {
          const args = tool.parameters?.parse ? tool.parameters.parse(params.input ?? {}) : (params.input ?? {});
          return toolResultToWire(await tool.handler(args, toolInvocation));
        } catch (e: any) {
          return { content: `Error: ${e?.message ?? e}`, isError: true };
        }
      }
    }
    throw new JsonRpcError(-32601, method);
  }

  /**
   * Starts a turn and returns once it is dispatched; follow progress with {@link on} (`turn.completed` marks the end).
   * Failures are reported as an `error` event.
   */
  async send(message: string | MessageOptions): Promise<void> {
    this.request(message).catch((e: any) =>
      this._dispatch({ type: "error", sessionId: this.sessionId, code: "send_failed", message: e?.message ?? String(e), retryable: false }),
    );
  }

  /** Runs a turn to completion and returns its result. With `timeoutMs` the turn is aborted when it takes longer. */
  async sendAndWait(message: string | MessageOptions, timeoutMs?: number): Promise<SendResult> {
    const run = this.request(message);
    if (timeoutMs === undefined) return run;
    let timer: NodeJS.Timeout | undefined;
    const timeout = new Promise<never>((_, reject) => {
      timer = setTimeout(() => {
        this.abort().catch(() => {});
        reject(new Error(`Turn did not complete within ${timeoutMs} ms`));
      }, timeoutMs);
    });
    try {
      return await Promise.race([run, timeout]);
    } finally {
      clearTimeout(timer);
    }
  }

  private request(message: string | MessageOptions): Promise<SendResult> {
    const m = typeof message === "string" ? { prompt: message } : message;
    return this.peer.request<SendResult>("session.send", { sessionId: this.sessionId, prompt: m.prompt, attachments: m.attachments });
  }

  /** Runs a turn and yields its events as they arrive; the last one is `turn.completed`. Returns the result. */
  async *stream(message: string | MessageOptions): AsyncGenerator<SessionEvent, SendResult> {
    const queue: SessionEvent[] = [];
    let wake: (() => void) | undefined;
    let done = false;
    const off = this.on((e) => {
      queue.push(e);
      wake?.();
    });
    const result = this.request(message).finally(() => {
      done = true;
      wake?.();
    });
    try {
      while (true) {
        if (queue.length === 0) {
          if (done) break;
          await new Promise<void>((r) => (wake = r));
          wake = undefined;
          continue;
        }
        const e = queue.shift()!;
        yield e;
        if (e.type === "turn.completed" && !e.parentToolUseId) break;
      }
    } finally {
      off();
    }
    return result;
  }

  /** Cancels the running turn. */
  async abort(): Promise<void> { await this.call("session.abort"); }
  async setModel(model: string): Promise<void> { await this.call("session.setModel", { model }); this.model = model; }
  async setPermissionMode(mode: PermissionMode): Promise<void> { await this.call("session.setMode", { mode }); }
  async setReasoningEffort(effort: ReasoningEffort): Promise<void> { await this.call("session.setEffort", { effort }); }
  /** Summarizes the conversation to free context. */
  async compact(instructions?: string): Promise<void> { await this.call("session.compact", { instructions }); }
  /** Clears the conversation. */
  async clear(): Promise<void> { await this.call("session.clear"); }
  /** The conversation so far (provider-neutral messages). */
  async getMessages(): Promise<unknown[]> {
    return (await this.call<{ messages: unknown[] }>("session.messages")).messages;
  }
  /** Tools available to the model (built-in, MCP and yours). */
  async listTools(): Promise<ToolInfo[]> {
    return (await this.call<{ tools: ToolInfo[] }>("tools.list")).tools;
  }

  /** Closes the session on the server; the transcript stays on disk for {@link DotCodeClient.resumeSession}. */
  async disconnect(): Promise<void> {
    try { await this.call("session.close"); } finally { this.onDisconnect(); this.handlers.clear(); }
  }

  async [Symbol.asyncDispose](): Promise<void> {
    await this.disconnect();
  }

  private call<T = unknown>(method: string, extra: Record<string, unknown> = {}): Promise<T> {
    return this.peer.request<T>(method, { sessionId: this.sessionId, ...extra });
  }
}
