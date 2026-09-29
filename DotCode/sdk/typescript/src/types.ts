// Public types of the DotCode SDK. Wire shapes follow schema/protocol.schema.json.
import type { SchemaLike } from "./schema.js";

// ---------------------------------------------------------------- enums & names

export type PermissionMode = "default" | "acceptEdits" | "auto" | "plan" | "bypassPermissions";
export type ReasoningEffort = "off" | "low" | "medium" | "high" | "xhigh";

/** Built-in tool names (typed so `availableTools`/`excludedTools` typos are compile errors). */
export const BuiltinTool = {
  Read: "Read",
  Write: "Write",
  Edit: "Edit",
  NotebookEdit: "NotebookEdit",
  Glob: "Glob",
  Grep: "Grep",
  Bash: "Bash",
  PowerShell: "PowerShell",
  BashOutput: "BashOutput",
  KillShell: "KillShell",
  WebFetch: "WebFetch",
  WebSearch: "WebSearch",
  TodoWrite: "TodoWrite",
  Agent: "Agent",
  Skill: "Skill",
  AskUserQuestion: "AskUserQuestion",
  ExitPlanMode: "ExitPlanMode",
  LSP: "LSP",
} as const;
export type BuiltinToolName = (typeof BuiltinTool)[keyof typeof BuiltinTool];

/**
 * A permission rule in Claude Code syntax: a tool name, `Tool(specifier)` (e.g. `Bash(npm test:*)`,
 * `Edit(src/**)`), or an MCP tool (`mcp__server__tool`). Custom tool names are accepted as plain strings.
 */
export type PermissionRule = BuiltinToolName | `${BuiltinToolName}(${string})` | `mcp__${string}` | (string & {});

// ---------------------------------------------------------------- events

export interface Usage {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens: number;
  reasoningTokens: number;
}

export interface TodoItem {
  content: string;
  status: "Pending" | "InProgress" | "Completed";
  activeForm?: string;
}

export interface ToolCallInfo {
  id: string;
  name: string;
  input: unknown;
}

export type StopReason = "EndTurn" | "ToolUse" | "MaxTokens" | "StopSequence" | "Refusal" | "Aborted" | "Error";

/** Payload of every event type, keyed by `type`. */
export interface SessionEventMap {
  "session.started": { model: string; cwd: string; tools: string[]; permissionMode: PermissionMode };
  "user.message": { text: string };
  "assistant.text.delta": { text: string };
  "assistant.thinking.delta": { text: string };
  "assistant.message": { messageId: string; text: string; thinking?: string; toolCalls: ToolCallInfo[]; model: string };
  "tool.started": { toolUseId: string; name: string; displayName: string; input: unknown };
  "tool.progress": { toolUseId: string; text: string };
  "tool.completed": {
    toolUseId: string; name: string; isError: boolean; summary: string; output: string;
    diff?: string; durationMs: number; rejected: boolean;
  };
  "todo.updated": { todos: TodoItem[] };
  "subagent.started": { toolUseId: string; agentType: string; description: string; model: string };
  "subagent.completed": { toolUseId: string; agentType: string; usage: Usage; durationMs: number; toolUses: number };
  "context.compacted": { tokensBefore: number; tokensAfter: number; automatic: boolean };
  "usage.updated": { turnUsage: Usage; sessionUsage: Usage; sessionCostUsd: number; contextTokens: number; contextWindow: number };
  "model.changed": { model: string };
  "model.fallback": { from: string; to: string; reason: string };
  "retry": { attempt: number; maxAttempts: number; delaySeconds: number; reason: string };
  "notice": { level: "Info" | "Warning" | "Error"; text: string };
  "error": { code: string; message: string; retryable: boolean };
  "mode.changed": { mode: PermissionMode };
  "turn.completed": {
    stopReason: StopReason; resultText: string; usage: Usage; costUsd: number;
    durationMs: number; numModelCalls: number; isError: boolean;
  };
}

export type SessionEventType = keyof SessionEventMap;

interface EventBase {
  sessionId?: string;
  /** Set for events produced inside a subagent. */
  parentToolUseId?: string;
}

/** A specific event, e.g. `SessionEventOf<"tool.completed">`. */
export type SessionEventOf<K extends SessionEventType> = EventBase & { type: K } & SessionEventMap[K];

/** Any event streamed by the engine (the terminal UI consumes the exact same stream). Narrow with `event.type`. */
export type SessionEvent = { [K in SessionEventType]: SessionEventOf<K> }[SessionEventType];

export type SessionEventHandler = (event: SessionEvent) => void;
export type TypedSessionEventHandler<K extends SessionEventType> = (event: SessionEventOf<K>) => void;

// ---------------------------------------------------------------- permissions

export interface PermissionRequest {
  toolUseId: string;
  toolName: string;
  displayName: string;
  input: Record<string, unknown>;
  title: string;
  detail?: string;
  /** Rule the user could persist, e.g. `Bash(npm test:*)`. */
  suggestedRule?: string;
  /** Unified diff preview for file edits. */
  diff?: string;
  parentToolUseId?: string;
}

/** Context passed to handlers. */
export interface Invocation {
  sessionId: string;
}

/** The answer to a {@link PermissionRequest}. Build it with the {@link PermissionDecision} factories. */
export type PermissionDecision =
  | { kind: "approve-once"; updatedInput?: Record<string, unknown> }
  | { kind: "approve-for-session"; updatedInput?: Record<string, unknown> }
  | { kind: "approve-always"; rule?: PermissionRule; updatedInput?: Record<string, unknown> }
  | { kind: "reject"; feedback?: string };

export const PermissionDecision = {
  /** Allow this single call. */
  approveOnce: (updatedInput?: Record<string, unknown>): PermissionDecision => ({ kind: "approve-once", updatedInput }),
  /** Allow matching calls for the rest of the session. */
  approveForSession: (updatedInput?: Record<string, unknown>): PermissionDecision => ({ kind: "approve-for-session", updatedInput }),
  /** Allow and persist a rule (defaults to the request's suggested rule). */
  approveAlways: (rule?: PermissionRule): PermissionDecision => ({ kind: "approve-always", rule }),
  /** Deny; `feedback` is returned to the model. */
  reject: (feedback?: string): PermissionDecision => ({ kind: "reject", feedback }),
} as const;

export type PermissionHandler = (request: PermissionRequest, invocation: Invocation) => PermissionDecision | Promise<PermissionDecision>;

/** Approves every permission request. */
export const approveAll: PermissionHandler = () => PermissionDecision.approveOnce();
/** Rejects every permission request (same as omitting the handler). */
export const rejectAll: PermissionHandler = () => PermissionDecision.reject("Rejected by the SDK host.");

// ---------------------------------------------------------------- user input & plan mode

export interface UserQuestion {
  question: string;
  header: string;
  options: { label: string; description?: string }[];
  multiSelect?: boolean;
}

export interface UserInputRequest {
  questions: UserQuestion[];
}

export interface UserQuestionAnswer {
  question: string;
  /** The chosen option label(s) (comma-separated for multi-select) or free text. */
  answer: string;
}

export type UserInputHandler = (request: UserInputRequest, invocation: Invocation) => UserQuestionAnswer[] | Promise<UserQuestionAnswer[]>;

export interface ExitPlanModeRequest {
  /** The plan (markdown) the agent wants to execute. */
  plan: string;
}

export type ExitPlanModeResult =
  | { approved: true; /** Continue in `acceptEdits` mode. */ acceptEdits?: boolean }
  | { approved: false; feedback?: string };

export type ExitPlanModeHandler = (request: ExitPlanModeRequest, invocation: Invocation) => ExitPlanModeResult | Promise<ExitPlanModeResult>;

// ---------------------------------------------------------------- tools

export interface ToolInvocation {
  sessionId: string;
  toolCallId: string;
  toolName: string;
  /** Raw arguments as sent by the model. */
  arguments: unknown;
}

/** Full control over a tool result. */
export interface ToolResultObject {
  textResultForLlm: string;
  resultType?: "success" | "failure";
  /** Images returned to the model (vision-capable models). */
  binaryResultsForLlm?: { data: string; mimeType: string }[];
}

/** A handler may return a string, a {@link ToolResultObject}, or any JSON-serializable value. */
export type ToolHandler<TArgs> = (args: TArgs, invocation: ToolInvocation) => unknown | Promise<unknown>;

/** A tool implemented by your application. Create it with {@link defineTool}. */
export interface Tool<TArgs = any> {
  name: string;
  description: string;
  parameters?: SchemaLike<TArgs>;
  handler: ToolHandler<TArgs>;
  /** Read-only tools may run in plan mode. Host tools never prompt for permission. */
  readOnly?: boolean;
}

// ---------------------------------------------------------------- session configuration

export type ProviderType =
  | "anthropic" | "openai" | "azure" | "gemini" | "deepseek" | "ollama" | "openai-compatible"
  | "bedrock" | "vertex" | "vertex-gemini" | "mock";

/** A named model provider (BYOK). Values may reference environment variables: `"${env:MY_KEY}"`. */
export interface ProviderConfig {
  type: ProviderType;
  baseUrl?: string;
  apiKey?: string;
  /** OpenAI family: wire API. */
  api?: "responses" | "chat";
  headers?: Record<string, string>;
  /** Quirk profile for OpenAI-compatible servers. */
  profile?: "deepseek" | "openrouter" | "lmstudio" | "vllm" | "litellm" | "groq" | "together" | "azure" | (string & {});
  /** Models to advertise when the API cannot list them. */
  models?: string[];
  timeoutSeconds?: number;
  /** Ollama context window. */
  numCtx?: number;
  /** AWS region (bedrock) or Google Cloud location (vertex). */
  region?: string;
  awsProfile?: string;
  /** Google Cloud project (vertex). */
  project?: string;
  credentialsFile?: string;
  /** Azure: `entra` for Microsoft Entra ID tokens instead of an API key. */
  auth?: "key" | "entra";
  tenantId?: string;
  clientId?: string;
  clientSecret?: string;
  /** Scripted provider (tests): path to a script file. */
  script?: string;
}

export type McpServerConfig =
  | { type?: "stdio"; command: string; args?: string[]; env?: Record<string, string> }
  | { type: "http" | "sse"; url: string; headers?: Record<string, string> };

export interface SystemMessageConfig {
  /** `append` (default) adds to DotCode's system prompt; `replace` swaps it entirely. */
  mode?: "append" | "replace";
  content: string;
}

export interface SessionConfig {
  /** `provider:model`, alias or role — e.g. `"anthropic:claude-sonnet-4-5"`, `"openai:gpt-5"`, `"ollama:qwen3-coder"`. */
  model?: string;
  fallbackModel?: string;
  /** Working directory of the session (default: the client's `cwd`). */
  workingDirectory?: string;
  permissionMode?: PermissionMode;
  reasoningEffort?: ReasoningEffort;
  systemMessage?: SystemMessageConfig;
  /** Custom tools implemented by your application ({@link defineTool}). */
  tools?: Tool[];
  /** Restrict the built-in tools to this list. */
  availableTools?: BuiltinToolName[];
  /** Remove tools / deny matching calls. */
  excludedTools?: PermissionRule[];
  /** Pre-approve matching calls (no permission prompt). */
  allowedTools?: PermissionRule[];
  mcpServers?: Record<string, McpServerConfig>;
  /** Skip MCP servers from settings files. */
  disableMcp?: boolean;
  /** Named providers (BYOK); reference them as `"<name>:<model>"` in `model`. */
  providers?: Record<string, ProviderConfig>;
  /** Advanced: raw settings merged over the settings files (prefer the typed options above). */
  settings?: Record<string, unknown>;
  maxTurns?: number;
  /** Save the transcript so the session can be resumed (default true). */
  persistSession?: boolean;
  /** Run in a fresh git worktree (`true`) or a named one; removed on disconnect when unchanged. */
  worktree?: boolean | string;
  /** Called before a tool runs when approval is needed. Without it the session is deny-by-default. */
  onPermissionRequest?: PermissionHandler;
  /** Answers the model's AskUserQuestion tool. */
  onUserInputRequest?: UserInputHandler;
  /** Reviews the plan when the agent leaves plan mode (default: approve). */
  onExitPlanMode?: ExitPlanModeHandler;
  /** Receives every event, including those emitted while the session is created. */
  onEvent?: SessionEventHandler;
}

export interface ResumeSessionConfig extends SessionConfig {
  /** Continue in a new session id, leaving the original transcript untouched. */
  fork?: boolean;
}

export type Attachment =
  | { type: "file"; path: string }
  | { type: "image"; data: string; mediaType: "image/png" | "image/jpeg" | "image/gif" | "image/webp" };

export interface MessageOptions {
  prompt: string;
  attachments?: Attachment[];
}

// ---------------------------------------------------------------- results

export interface SendResult {
  sessionId: string;
  stopReason: StopReason;
  result: string;
  isError: boolean;
  error?: string;
  durationMs: number;
  numModelCalls: number;
  costUsd: number;
  totalCostUsd: number;
  usage: Usage;
}

export interface SessionInfo {
  sessionId: string;
  model: string;
  cwd: string;
  permissionMode: PermissionMode;
  messageCount: number;
  totalCostUsd: number;
  transcriptPath?: string;
  worktree?: { name: string; path: string; branch: string };
  tools: string[];
}

export interface SessionMetadata {
  id: string;
  title?: string;
  firstPrompt: string;
  modified: string;
  messageCount: number;
}

export interface ModelList {
  default: string;
  providers: { name: string; type: string }[];
  models: { provider: string; id: string; qualifiedId: string }[];
}

export interface ToolInfo {
  name: string;
  description: string;
  inputSchema: unknown;
}
