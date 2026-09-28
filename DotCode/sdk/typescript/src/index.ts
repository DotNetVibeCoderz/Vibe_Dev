/**
 * DotCode SDK for TypeScript/JavaScript.
 * Built by Gravicode Studios, led by Kang Fadhil.
 *
 * ```ts
 * import { DotCodeClient } from "dotcode-sdk";
 * const client = new DotCodeClient();
 * const session = await client.createSession({ model: "openai:gpt-5" });
 * const result = await session.send("Summarize README.md");
 * console.log(result.result);
 * await client.close();
 * ```
 */
import { spawn, type ChildProcess } from "node:child_process";
import { JsonRpcPeer, JsonRpcError } from "./jsonrpc.js";
import type {
  AgentEvent, PermissionDecision, SendResult, SessionInfo, SessionOptions, ToolContent,
} from "./types.js";

export * from "./types.js";
export { JsonRpcError } from "./jsonrpc.js";

export const PROTOCOL_VERSION = "1.0";

export interface ClientOptions {
  /** Path to the dotcode executable (or dotcode.dll). Defaults to $DOTCODE_CLI_PATH or "dotcode" on PATH. */
  cliPath?: string;
  /** Extra arguments passed to `dotcode serve`. */
  serverArgs?: string[];
  /** Default working directory for sessions. */
  cwd?: string;
  env?: Record<string, string>;
}

export class DotCodeClient {
  private proc?: ChildProcess;
  private peer?: JsonRpcPeer;
  private readonly sessions = new Map<string, Session>();
  private starting?: Promise<void>;
  serverVersion?: string;

  constructor(private readonly options: ClientOptions = {}) {}

  /** Starts `dotcode serve` and performs the protocol handshake (called lazily by other methods). */
  start(): Promise<void> {
    this.starting ??= this.doStart();
    return this.starting;
  }

  private async doStart() {
    const cli = this.options.cliPath ?? process.env.DOTCODE_CLI_PATH ?? "dotcode";
    const [cmd, args] = cli.endsWith(".dll") ? ["dotnet", [cli, "serve"]] : [cli, ["serve"]];
    this.proc = spawn(cmd, [...args, ...(this.options.serverArgs ?? [])], {
      cwd: this.options.cwd,
      env: { ...process.env, ...this.options.env },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    const spawned = new Promise<void>((resolve, reject) => {
      this.proc!.once("spawn", () => resolve());
      this.proc!.once("error", (e) =>
        reject(new Error(`Could not start '${cli}'. Install the DotCode CLI or set DOTCODE_CLI_PATH. (${e.message})`)),
      );
    });
    await spawned;
    this.proc.stderr!.on("data", () => {});
    this.peer = new JsonRpcPeer(this.proc.stdout!, this.proc.stdin!);
    this.peer.on("notification", (method: string, params: any) => {
      if (method === "session.event") this.sessions.get(params.sessionId)?._dispatch(params.event as AgentEvent);
    });
    this.peer.onRequest = async (method, params) => {
      const session = this.sessions.get(params?.sessionId);
      if (!session) throw new JsonRpcError(-32001, "Unknown session");
      return session._handleServerRequest(method, params);
    };
    const init = await this.peer.request("initialize", {
      protocolVersion: PROTOCOL_VERSION,
      clientInfo: { name: "dotcode-sdk-typescript", version: "0.1.0" },
      capabilities: { permissions: true, questions: true },
    });
    this.serverVersion = init?.serverInfo?.version;
  }

  async createSession(options: SessionOptions = {}): Promise<Session> {
    await this.start();
    const info = await this.peer!.request<SessionInfo>("session.create", toWire(options, this.options.cwd));
    const session = new Session(this.peer!, info, options);
    this.sessions.set(info.sessionId, session);
    return session;
  }

  async resumeSession(sessionId: string, options: SessionOptions = {}, fork = false): Promise<Session> {
    await this.start();
    const info = await this.peer!.request<SessionInfo>("session.resume", { ...toWire(options, this.options.cwd), sessionId, fork });
    const session = new Session(this.peer!, info, options);
    this.sessions.set(info.sessionId, session);
    return session;
  }

  async listModels(): Promise<{ default: string; providers: { name: string; type: string }[]; models: { provider: string; id: string; qualifiedId: string }[] }> {
    await this.start();
    return this.peer!.request("models.list", { cwd: this.options.cwd });
  }

  async listSessions(): Promise<{ sessions: { id: string; title?: string; firstPrompt: string; modified: string; messageCount: number }[] }> {
    await this.start();
    return this.peer!.request("session.list", { cwd: this.options.cwd });
  }

  async close(): Promise<void> {
    if (!this.proc) return;
    try {
      await Promise.race([this.peer!.request("shutdown"), new Promise((r) => setTimeout(r, 1500))]);
    } catch { /* ignore */ }
    this.proc.stdin?.end();
    const exited = new Promise<void>((r) => this.proc!.once("exit", () => r()));
    await Promise.race([exited, new Promise((r) => setTimeout(r, 2000))]);
    if (this.proc.exitCode === null) this.proc.kill();
    this.proc = undefined;
  }
}

function toWire(o: SessionOptions, defaultCwd?: string) {
  return {
    cwd: o.cwd ?? defaultCwd,
    model: o.model,
    fallbackModel: o.fallbackModel,
    permissionMode: o.permissionMode,
    systemPrompt: o.systemPrompt,
    appendSystemPrompt: o.appendSystemPrompt,
    allowedTools: o.allowedTools,
    disallowedTools: o.disallowedTools,
    tools: o.builtinTools,
    hostTools: o.tools?.map((t) => ({ name: t.name, description: t.description, inputSchema: t.inputSchema ?? { type: "object", properties: {} }, readOnly: t.readOnly ?? false })),
    mcpServers: o.mcpServers,
    settings: o.settings,
    maxTurns: o.maxTurns,
    effort: o.effort,
    persistSession: o.persistSession ?? true,
    noMcp: o.noMcp,
  };
}

export class Session {
  readonly id: string;
  model: string;
  private listeners = new Set<(e: AgentEvent) => void>();

  constructor(private readonly peer: JsonRpcPeer, public readonly info: SessionInfo, private readonly options: SessionOptions) {
    this.id = info.sessionId;
    this.model = info.model;
  }

  /** Subscribe to engine events; returns an unsubscribe function. */
  on(listener: (e: AgentEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** @internal */
  _dispatch(e: AgentEvent) {
    if (e.type === "model.changed") this.model = e.model;
    this.options.onEvent?.(e);
    for (const l of this.listeners) l(e);
  }

  /** @internal */
  async _handleServerRequest(method: string, params: any): Promise<unknown> {
    switch (method) {
      case "permission.request": {
        const handler = this.options.onPermissionRequest;
        const decision: PermissionDecision = handler
          ? await handler(params.request)
          : { decision: "deny", feedback: "No permission handler registered in the SDK host (deny by default)." };
        return decision;
      }
      case "user.question":
        return { answers: this.options.onQuestion ? await this.options.onQuestion(params.questions) : [] };
      case "plan.review":
        return { approval: this.options.onPlanReview && !(await this.options.onPlanReview(params.plan)) ? "reject" : "approve" };
      case "tool.call": {
        const tool = this.options.tools?.find((t) => t.name === params.name);
        if (!tool) throw new JsonRpcError(-32601, `Unknown host tool ${params.name}`);
        try {
          const content: ToolContent = await tool.handler(params.input, { sessionId: this.id, toolUseId: params.toolUseId });
          return { content: typeof content === "string" ? content : content };
        } catch (e: any) {
          return { content: `Error: ${e?.message ?? e}`, isError: true };
        }
      }
    }
    throw new JsonRpcError(-32601, method);
  }

  /** Runs a prompt to completion. */
  send(prompt: string, options: { attachments?: { type: "image"; data: string; mediaType: string }[] } = {}): Promise<SendResult> {
    return this.peer.request<SendResult>("session.send", { sessionId: this.id, prompt, attachments: options.attachments });
  }

  /** Runs a prompt and yields events as they arrive; the last event is `turn.completed`. */
  async *stream(prompt: string): AsyncGenerator<AgentEvent, SendResult> {
    const queue: AgentEvent[] = [];
    let wake: (() => void) | undefined;
    let done = false;
    const off = this.on((e) => {
      queue.push(e);
      wake?.();
    });
    const result = this.send(prompt).finally(() => {
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

  abort() { return this.peer.request("session.abort", { sessionId: this.id }); }
  setModel(model: string) { return this.peer.request("session.setModel", { sessionId: this.id, model }); }
  setPermissionMode(mode: string) { return this.peer.request("session.setMode", { sessionId: this.id, mode }); }
  setEffort(effort: string) { return this.peer.request("session.setEffort", { sessionId: this.id, effort }); }
  compact(instructions?: string) { return this.peer.request("session.compact", { sessionId: this.id, instructions }); }
  clear() { return this.peer.request("session.clear", { sessionId: this.id }); }
  messages(): Promise<{ messages: unknown[] }> { return this.peer.request("session.messages", { sessionId: this.id }); }
  tools(): Promise<{ tools: { name: string; description: string; inputSchema: unknown }[] }> { return this.peer.request("tools.list", { sessionId: this.id }); }
  close() { return this.peer.request("session.close", { sessionId: this.id }); }
}

/** Helper to define a typed tool. */
export function tool<TInput = any>(definition: import("./types.js").DotCodeTool<TInput>) {
  return definition;
}
