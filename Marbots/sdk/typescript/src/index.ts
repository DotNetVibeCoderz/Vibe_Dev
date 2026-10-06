/**
 * Marbots SDK for TypeScript / JavaScript — typed client for the Marbots multi-agent collaboration platform.
 * Node 18+ and modern browsers (fetch + streams). Built by Gravicode Studios, led by Kang Fadhil.
 *
 * @example
 * ```ts
 * import { MarbotsClient, KernelPack, PermissionProfile, ModelRef, EventType } from "@gravicode/marbots";
 * const mb = new MarbotsClient();
 * const sari = await mb.bots.create({
 *   name: "Sari", role: "UX designer",
 *   kernelFunctions: [KernelPack.Files, KernelPack.Web],
 *   permissionProfile: PermissionProfile.WorkspaceWrite,
 *   model: ModelRef.of("azure", "gpt-5.6-luna"),
 * });
 * const thread = await mb.threads.create(sari.id);
 * const r = await mb.threads.send(thread.id, "Sketch a wireframe", { wait: true });
 * console.log(r.task.model, r.reply?.content);
 * ```
 */
import type {
  AgentEvent, ApprovalRequest, ApprovalScope, Bot, BotModelInfo, BotSpec, BotTemplate, ChatMessage, ChatThread,
  ClientOptions, HostInfo, McpServer, MemoryKind, MemoryRecord, ModelCatalog, ModelSetting, ScheduleJob, ScheduleSpec,
  SendOptions, SendResult, SkillInfo, SystemInfo, TaskRecord, WorkspaceFile,
} from "./types.js";
import { TERMINAL_STATES } from "./types.js";

export * from "./types.js";

/** An API error: HTTP status plus the server's Problem Details message. */
export class MarbotsError extends Error {
  constructor(public readonly status: number, message: string) {
    super(`HTTP ${status}: ${message}`);
    this.name = "MarbotsError";
  }
}

const q = encodeURIComponent;

/** True when the event says a task has finished (completed, failed, cancelled or timed out). */
export function isTaskFinished(e: AgentEvent): boolean {
  return e.type === "TaskStateChanged" && (TERMINAL_STATES as readonly string[]).includes(e.data ?? "");
}

function botBody(spec: BotSpec, id = ""): Record<string, unknown> {
  return {
    id, name: spec.name, role: spec.role ?? "", description: spec.description ?? "", persona: spec.persona ?? "",
    color: spec.color ?? "#2C3BA3", modelProfile: spec.model ?? "default",
    kernelFunctions: spec.kernelFunctions ?? ["files", "search", "web", "memory", "todo"],
    skills: spec.skills ?? [], mcpServers: spec.mcpServers ?? [], permissionProfile: spec.permissionProfile ?? "developer-safe",
    autoLearn: spec.autoLearn ?? "Off", shortTermMemory: spec.shortTermMemory ?? true, longTermMemory: spec.longTermMemory ?? true,
    maxSteps: spec.maxSteps ?? 24,
  };
}

export class MarbotsClient {
  readonly baseUrl: string;
  private readonly apiKey?: string;
  private readonly f: typeof fetch;

  constructor(options: ClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? "http://localhost:5170").replace(/\/$/, "");
    this.apiKey = options.apiKey;
    this.f = options.fetch ?? globalThis.fetch.bind(globalThis);
  }

  private headers(extra?: Record<string, string>): Record<string, string> {
    return { ...(this.apiKey ? { "X-Api-Key": this.apiKey } : {}), ...extra };
  }

  private async raw(method: string, path: string, body?: BodyInit, contentType = "application/json"): Promise<Response> {
    const res = await this.f(this.baseUrl + path, {
      method, body, headers: this.headers(body !== undefined ? { "Content-Type": contentType } : undefined),
    });
    if (!res.ok) {
      const text = await res.text();
      let message = text;
      try {
        const j = JSON.parse(text) as { detail?: string; title?: string };
        message = j.detail ?? j.title ?? text;
      } catch { /* not JSON */ }
      throw new MarbotsError(res.status, message);
    }
    return res;
  }

  private async json<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await this.raw(method, path, body === undefined ? undefined : JSON.stringify(body));
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  system(): Promise<SystemInfo> { return this.json("GET", "/api/v1/system"); }
  hosts(): Promise<HostInfo[]> { return this.json("GET", "/api/v1/hosts"); }

  /** Create a thread with `bot`, send `text`, wait for the answer and return its text. */
  async chat(bot: string, text: string, timeoutSeconds = 600): Promise<string> {
    const thread = await this.threads.create(bot);
    const r = await this.threads.send(thread.id, text, { wait: true, timeoutSeconds });
    return r.reply?.content ?? r.task.result ?? r.task.error ?? "";
  }

  readonly bots = {
    list: (): Promise<Bot[]> => this.json("GET", "/api/v1/bots"),
    get: (idOrName: string): Promise<Bot> => this.json("GET", `/api/v1/bots/${q(idOrName)}`),
    create: (spec: BotSpec): Promise<Bot> => this.json("POST", "/api/v1/bots", botBody(spec)),
    update: (id: string, spec: BotSpec): Promise<Bot> => this.json("PUT", `/api/v1/bots/${q(id)}`, botBody(spec, id)),
    delete: (id: string): Promise<void> => this.json("DELETE", `/api/v1/bots/${q(id)}`),
    /** Create a bot from a gallery template. */
    hire: (templateId: string, name?: string): Promise<Bot> => this.json("POST", `/api/v1/bots/from-template/${q(templateId)}`, { name }),
    getModel: (id: string): Promise<BotModelInfo> => this.json("GET", `/api/v1/bots/${q(id)}/model`),
    /** `model`: `ModelRef.Default`, `ModelRef.of(provider, model)` or a profile name. */
    setModel: (id: string, model: ModelSetting): Promise<BotModelInfo> => this.json("PUT", `/api/v1/bots/${q(id)}/model`, { model }),
    pause: (id: string): Promise<void> => this.json("POST", `/api/v1/bots/${q(id)}/pause`),
    resume: (id: string): Promise<void> => this.json("POST", `/api/v1/bots/${q(id)}/resume`),
    /** Download a .marbot package (secrets are never included). */
    export: async (id: string, includeMemory = false): Promise<Uint8Array> =>
      new Uint8Array(await (await this.raw("GET", `/api/v1/bots/${q(id)}/export?includeMemory=${includeMemory}`)).arrayBuffer()),
    importPackage: async (pkg: Uint8Array): Promise<Bot> =>
      (await (await this.raw("POST", "/api/v1/bots/import", pkg as unknown as BodyInit, "application/zip")).json()) as Bot,
  };

  readonly templates = {
    list: (query = "", category = ""): Promise<BotTemplate[]> => this.json("GET", `/api/v1/templates?q=${q(query)}&category=${q(category)}`),
    get: (id: string): Promise<BotTemplate> => this.json("GET", `/api/v1/templates/${q(id)}`),
  };

  readonly models = {
    list: (): Promise<ModelCatalog> => this.json("GET", "/api/v1/models"),
    /** Change the workspace default model (used by every bot whose model is `ModelRef.Default`). */
    setDefault: async (model: ModelSetting): Promise<string> => (await this.json<{ default: string }>("PUT", "/api/v1/models/default", { model })).default,
  };

  readonly threads = {
    list: (botId?: string): Promise<ChatThread[]> => this.json("GET", `/api/v1/threads${botId ? `?botId=${q(botId)}` : ""}`),
    create: (botId: string, title?: string): Promise<ChatThread> => this.json("POST", "/api/v1/threads", { botId, title }),
    send: (threadId: string, text: string, options: SendOptions = {}): Promise<SendResult> =>
      this.json("POST", `/api/v1/threads/${q(threadId)}/messages`, { text, wait: options.wait ?? false, timeoutSeconds: options.timeoutSeconds ?? 600 }),
    messages: (threadId: string, afterSeq = 0): Promise<ChatMessage[]> => this.json("GET", `/api/v1/threads/${q(threadId)}/messages?after=${afterSeq}`),
    files: (threadId: string): Promise<WorkspaceFile[]> => this.json("GET", `/api/v1/threads/${q(threadId)}/files`),
    fileUrl: (threadId: string, path: string): string => `${this.baseUrl}/api/v1/threads/${q(threadId)}/files/${path}`,
    exportTranscript: async (threadId: string): Promise<string> => (await this.raw("GET", `/api/v1/threads/${q(threadId)}/export`)).text(),
    delete: (threadId: string): Promise<void> => this.json("DELETE", `/api/v1/threads/${q(threadId)}`),
  };

  readonly tasks = {
    list: (threadId?: string): Promise<TaskRecord[]> => this.json("GET", `/api/v1/tasks${threadId ? `?threadId=${q(threadId)}` : ""}`),
    get: (id: string): Promise<TaskRecord> => this.json("GET", `/api/v1/tasks/${q(id)}`),
    cancel: (id: string): Promise<void> => this.json("POST", `/api/v1/tasks/${q(id)}/cancel`),
  };

  readonly approvals = {
    /** True when approvals are skipped (dangerous mode). */
    getSkipApprovals: async (): Promise<boolean> =>
      (await this.json<{ dangerouslySkipApprovals: boolean }>("GET", "/api/v1/system/approvals")).dangerouslySkipApprovals,
    /**
     * Dangerous, like `--dangerously-skip-permissions`: allow every "ask" action without a human. Turning it on also
     * approves everything pending. Actions a bot's profile denies stay denied.
     */
    setSkipApprovals: async (skip: boolean): Promise<boolean> =>
      (await this.json<{ dangerouslySkipApprovals: boolean }>("PUT", "/api/v1/system/approvals", { dangerouslySkipApprovals: skip })).dangerouslySkipApprovals,
    pending: (): Promise<ApprovalRequest[]> => this.json("GET", "/api/v1/approvals?state=pending"),
    approve: (id: string, scope: ApprovalScope = "Once"): Promise<ApprovalRequest> => this.json("POST", `/api/v1/approvals/${q(id)}/approve`, { scope }),
    reject: (id: string): Promise<ApprovalRequest> => this.json("POST", `/api/v1/approvals/${q(id)}/reject`),
  };

  readonly skills = {
    list: (): Promise<SkillInfo[]> => this.json("GET", "/api/v1/skills"),
    install: (source: string): Promise<SkillInfo[]> => this.json("POST", "/api/v1/skills/install", { source }),
  };

  readonly mcp = {
    list: (): Promise<McpServer[]> => this.json("GET", "/api/v1/mcp"),
    install: (id: string): Promise<McpServer> => this.json("POST", `/api/v1/mcp/${q(id)}/install`),
  };

  readonly schedules = {
    list: (): Promise<ScheduleJob[]> => this.json("GET", "/api/v1/schedules"),
    create: (spec: ScheduleSpec): Promise<ScheduleJob> => this.json("POST", "/api/v1/schedules", spec),
    delete: (id: string): Promise<void> => this.json("DELETE", `/api/v1/schedules/${q(id)}`),
  };

  readonly memory = {
    list: (owner: string): Promise<MemoryRecord[]> => this.json("GET", `/api/v1/memory/${q(owner)}`),
    remember: (owner: string, content: string, kind: MemoryKind = "Semantic"): Promise<MemoryRecord> =>
      this.json("POST", "/api/v1/memory", { owner, content, kind, source: "sdk:typescript", confidence: 1 }),
  };

  /** Streams live events via Server-Sent Events. `break` out of the loop or pass an AbortSignal to stop. */
  async *events(threadId?: string, signal?: AbortSignal): AsyncGenerator<AgentEvent> {
    const path = threadId ? `/api/v1/threads/${q(threadId)}/events` : "/api/v1/events";
    const res = await this.f(this.baseUrl + path, { headers: this.headers({ Accept: "text/event-stream" }), signal });
    if (!res.ok || !res.body) throw new MarbotsError(res.status, "event stream unavailable");
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) return;
        buffer += decoder.decode(value, { stream: true });
        let idx: number;
        while ((idx = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, idx).trimEnd();
          buffer = buffer.slice(idx + 1);
          if (line.startsWith("data: ")) yield JSON.parse(line.slice(6)) as AgentEvent;
        }
      }
    } finally {
      await reader.cancel().catch(() => undefined);
    }
  }
}

export default MarbotsClient;
