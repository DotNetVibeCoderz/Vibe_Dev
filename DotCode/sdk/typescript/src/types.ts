// Protocol types for the DotCode JSON-RPC server (see schema/protocol.schema.json).

export type PermissionMode = "default" | "acceptEdits" | "plan" | "bypassPermissions";
export type ReasoningEffort = "off" | "low" | "medium" | "high" | "xhigh";

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

interface BaseEvent {
  sessionId?: string;
  /** Set for events produced inside a subagent. */
  parentToolUseId?: string;
}

/** Events streamed by the engine. The TUI consumes the exact same stream. */
export type AgentEvent =
  | (BaseEvent & { type: "session.started"; model: string; cwd: string; tools: string[]; permissionMode: string })
  | (BaseEvent & { type: "user.message"; text: string })
  | (BaseEvent & { type: "assistant.text.delta"; text: string })
  | (BaseEvent & { type: "assistant.thinking.delta"; text: string })
  | (BaseEvent & { type: "assistant.message"; messageId: string; text: string; thinking?: string; toolCalls: { id: string; name: string; input: unknown }[]; model: string })
  | (BaseEvent & { type: "tool.started"; toolUseId: string; name: string; displayName: string; input: unknown })
  | (BaseEvent & { type: "tool.progress"; toolUseId: string; text: string })
  | (BaseEvent & { type: "tool.completed"; toolUseId: string; name: string; isError: boolean; summary: string; output: string; diff?: string; durationMs: number; rejected: boolean })
  | (BaseEvent & { type: "todo.updated"; todos: TodoItem[] })
  | (BaseEvent & { type: "subagent.started"; toolUseId: string; agentType: string; description: string; model: string })
  | (BaseEvent & { type: "subagent.completed"; toolUseId: string; agentType: string; usage: Usage; durationMs: number; toolUses: number })
  | (BaseEvent & { type: "context.compacted"; tokensBefore: number; tokensAfter: number; automatic: boolean })
  | (BaseEvent & { type: "usage.updated"; turnUsage: Usage; sessionUsage: Usage; sessionCostUsd: number; contextTokens: number; contextWindow: number })
  | (BaseEvent & { type: "model.changed"; model: string })
  | (BaseEvent & { type: "model.fallback"; from: string; to: string; reason: string })
  | (BaseEvent & { type: "retry"; attempt: number; maxAttempts: number; delaySeconds: number; reason: string })
  | (BaseEvent & { type: "notice"; level: "Info" | "Warning" | "Error"; text: string })
  | (BaseEvent & { type: "error"; code: string; message: string; retryable: boolean })
  | (BaseEvent & { type: "mode.changed"; mode: string })
  | (BaseEvent & { type: "turn.completed"; stopReason: string; resultText: string; usage: Usage; costUsd: number; durationMs: number; numModelCalls: number; isError: boolean });

export interface PermissionRequest {
  toolUseId: string;
  toolName: string;
  displayName: string;
  input: Record<string, unknown>;
  title: string;
  detail?: string;
  suggestedRule?: string;
  diff?: string;
  parentToolUseId?: string;
}

export interface PermissionDecision {
  /** allow = once, allow_always = persist rule, allow_session = for this session, deny = reject */
  decision: "allow" | "allow_always" | "allow_session" | "deny";
  /** Explanation returned to the model on deny. */
  feedback?: string;
  rule?: string;
  updatedInput?: Record<string, unknown>;
}

export interface UserQuestion {
  question: string;
  header: string;
  options: { label: string; description?: string }[];
  multiSelect?: boolean;
}

export interface UserQuestionAnswer {
  question: string;
  answer: string;
}

export type ToolContent = string | Array<{ type: "text"; text: string } | { type: "image"; data: string; mediaType: string }>;

/** A tool implemented by your application. */
export interface DotCodeTool<TInput = any> {
  name: string;
  description: string;
  /** JSON Schema of the input. */
  inputSchema?: Record<string, unknown>;
  readOnly?: boolean;
  handler: (input: TInput, context: { sessionId: string; toolUseId: string }) => Promise<ToolContent> | ToolContent;
}

export interface McpServerSpec {
  type?: "stdio" | "http" | "sse";
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  url?: string;
  headers?: Record<string, string>;
}

export interface SessionOptions {
  /** provider:model, alias or role — e.g. "anthropic:claude-sonnet-4-5", "openai:gpt-5", "ollama:qwen3-coder". */
  model?: string;
  fallbackModel?: string;
  cwd?: string;
  permissionMode?: PermissionMode;
  systemPrompt?: string;
  appendSystemPrompt?: string;
  allowedTools?: string[];
  disallowedTools?: string[];
  /** Restrict the built-in tools. */
  builtinTools?: string[];
  tools?: DotCodeTool[];
  mcpServers?: Record<string, McpServerSpec>;
  /** Inline settings merged over settings files (e.g. BYOK provider configuration). */
  settings?: Record<string, unknown>;
  maxTurns?: number;
  effort?: ReasoningEffort;
  persistSession?: boolean;
  noMcp?: boolean;
  /** Run in a fresh git worktree (`true`) or a named one (`"name"`); removed on close when unchanged. */
  worktree?: boolean | string;
  /** Called when a tool needs approval. Without it the session is deny-by-default. */
  onPermissionRequest?: (request: PermissionRequest) => Promise<PermissionDecision> | PermissionDecision;
  onQuestion?: (questions: UserQuestion[]) => Promise<UserQuestionAnswer[]> | UserQuestionAnswer[];
  onPlanReview?: (plan: string) => Promise<boolean> | boolean;
  onEvent?: (event: AgentEvent) => void;
}

export interface SendResult {
  sessionId: string;
  stopReason: string;
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
  permissionMode: string;
  messageCount: number;
  totalCostUsd: number;
  transcriptPath?: string;
  tools: string[];
}
