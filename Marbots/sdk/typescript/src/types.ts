// Public types of the Marbots SDK. Wire shapes follow the server's /api/v1 JSON (camelCase).

// ---------------------------------------------------------------- names (typed so typos are compile errors)

/** Id of the protected manager bot. */
export const BOSS_MAN = "boss-man";

export type BotStatus = "Ready" | "Running" | "Paused" | "Archived" | "Degraded";
export type TaskState =
  | "Queued" | "Preparing" | "Running" | "WaitingForTool" | "WaitingForAgent" | "WaitingForHuman"
  | "Completed" | "Failed" | "Cancelled" | "TimedOut";
export type TerminalTaskState = "Completed" | "Failed" | "Cancelled" | "TimedOut";
export type AutoLearnMode = "Off" | "MemoryOnly" | "SuggestSkills";
export type ApprovalScope = "Once" | "Session";
export type ApprovalState = "Pending" | "Approved" | "Rejected" | "Expired";
export type MemoryKind = "Semantic" | "Episodic" | "Procedural" | "Relational" | "Artifact";
export type MessageRole = "user" | "assistant" | "tool" | "system";

export const TERMINAL_STATES: readonly TerminalTaskState[] = ["Completed", "Failed", "Cancelled", "TimedOut"];

/** Built-in tool packs a bot can enable. */
export const KernelPack = {
  Files: "files",
  Search: "search",
  Shell: "shell",
  Web: "web",
  Memory: "memory",
  Todo: "todo",
  Agents: "agents",
} as const;
export type KernelPackName = (typeof KernelPack)[keyof typeof KernelPack];

/** What a bot may do without asking (see docs/en/security.md). */
export const PermissionProfile = {
  ReadOnly: "read-only",
  WorkspaceWrite: "workspace-write",
  DeveloperSafe: "developer-safe",
  Autonomous: "autonomous",
  Manager: "manager",
} as const;
export type PermissionProfileName = (typeof PermissionProfile)[keyof typeof PermissionProfile];

/** Types of events on the live stream. */
export const EventType = {
  BotCreated: "BotCreated",
  BotUpdated: "BotUpdated",
  BotDeleted: "BotDeleted",
  BotStateChanged: "BotStateChanged",
  MessageAdded: "MessageAdded",
  TaskCreated: "TaskCreated",
  TaskStateChanged: "TaskStateChanged",
  TaskDelegated: "TaskDelegated",
  TaskProgressed: "TaskProgressed",
  AgentThinkingStarted: "AgentThinkingStarted",
  AgentThinkingCompleted: "AgentThinkingCompleted",
  ToolCallStarted: "ToolCallStarted",
  ToolCallCompleted: "ToolCallCompleted",
  ApprovalRequested: "ApprovalRequested",
  ApprovalResolved: "ApprovalResolved",
  MemoryWritten: "MemoryWritten",
  SkillLoaded: "SkillLoaded",
  ContextCompacted: "ContextCompacted",
  AutoLearnCandidateCreated: "AutoLearnCandidateCreated",
  ScheduleTriggered: "ScheduleTriggered",
  HostConnected: "HostConnected",
  TodoUpdated: "TodoUpdated",
} as const;
export type EventTypeName = (typeof EventType)[keyof typeof EventType];

/**
 * A bot's model setting: `"default"` (workspace default model), a profile name, or `"provider/model"`.
 * Build it with {@link ModelRef}.
 */
export type ModelSetting = "default" | `${string}/${string}` | (string & {});

export const ModelRef = {
  /** Follow the workspace default model. */
  Default: "default" as const,
  /** A direct provider/model pair, e.g. `ModelRef.of("azure", "gpt-5.6-luna")`. */
  of<P extends string, M extends string>(provider: P, model: M): `${P}/${M}` {
    if (!provider || !model || provider.includes("/")) throw new Error("provider and model are required; provider cannot contain '/'");
    return `${provider}/${model}`;
  },
  /** A named model profile configured in Settings. */
  profile(name: string): ModelSetting {
    return name;
  },
};

// ---------------------------------------------------------------- bots & templates

export interface Bot {
  id: string;
  name: string;
  role: string;
  description: string;
  persona: string;
  color: string;
  /** "default" (workspace default), a profile name, or "provider/model". */
  modelProfile: ModelSetting;
  shortTermMemory: boolean;
  longTermMemory: boolean;
  autoLearn: AutoLearnMode;
  skills: string[];
  mcpServers: string[];
  kernelFunctions: string[];
  permissionProfile: PermissionProfileName;
  hostRef: string;
  maxSteps: number;
  isSystem: boolean;
  status: BotStatus;
  templateId?: string;
  createdAt: string;
  updatedAt: string;
}

/** Options for creating or updating a bot. */
export interface BotSpec {
  name: string;
  role?: string;
  description?: string;
  persona?: string;
  color?: string;
  /** Defaults to `ModelRef.Default`. */
  model?: ModelSetting;
  kernelFunctions?: KernelPackName[];
  skills?: string[];
  mcpServers?: string[];
  permissionProfile?: Exclude<PermissionProfileName, "manager">;
  autoLearn?: AutoLearnMode;
  shortTermMemory?: boolean;
  longTermMemory?: boolean;
  maxSteps?: number;
}

export interface BotTemplate {
  id: string;
  name: string;
  category: string;
  role: string;
  description: string;
  persona: string;
  color: string;
  modelProfile: ModelSetting;
  skills: string[];
  mcpServers: string[];
  kernelFunctions: string[];
  tags: string[];
  permissionProfile: PermissionProfileName;
  isBuiltIn: boolean;
}

export interface BotModelInfo {
  botId: string;
  /** The bot's own setting. */
  setting: ModelSetting;
  /** The provider/model the bot actually runs on. */
  effective: string;
  usesDefault: boolean;
  warning?: string | null;
}

export interface ModelProfileInfo {
  name: string;
  provider: string;
  model: string;
  fallbacks: string[];
}

export interface ModelCatalog {
  /** The workspace default provider/model. */
  default: string;
  /** provider/model pairs offered in model pickers. */
  choices: string[];
  profiles: ModelProfileInfo[];
}

// ---------------------------------------------------------------- threads, messages, tasks

export interface ChatThread {
  id: string;
  title: string;
  botId: string;
  pinned: boolean;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface ToolCall {
  id: string;
  name: string;
  arguments: string;
}

export interface ChatMessage {
  id: string;
  threadId: string;
  seq: number;
  role: MessageRole;
  author: string;
  content: string;
  toolCalls?: ToolCall[];
  toolCallId?: string;
  toolName?: string;
  taskId?: string;
  createdAt: string;
}

export interface TaskRecord {
  id: string;
  parentTaskId?: string;
  rootTaskId: string;
  threadId: string;
  botId: string;
  assignedBy: string;
  depth: number;
  objective: string;
  state: TaskState;
  result?: string;
  error?: string;
  currentActivity?: string;
  /** provider/model that served the latest step. */
  model?: string;
  steps: number;
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
}

export interface SendResult {
  task: TaskRecord;
  /** The bot's final reply (only with `wait: true`). */
  reply?: ChatMessage;
}

export interface SendOptions {
  /** Wait for the bot to finish (default false). */
  wait?: boolean;
  timeoutSeconds?: number;
}

export interface WorkspaceFile {
  path: string;
  size: number;
  modified: string;
}

// ---------------------------------------------------------------- approvals, events, memory, skills, mcp, schedules

export interface ApprovalRequest {
  id: string;
  taskId: string;
  threadId: string;
  botId: string;
  toolName: string;
  arguments: string;
  category: string;
  risk: "Low" | "Medium" | "High" | "Critical";
  reason: string;
  state: ApprovalState;
  createdAt: string;
}

export interface AgentEvent {
  id: number;
  type: EventTypeName;
  timestamp: string;
  threadId?: string;
  taskId?: string;
  botId?: string;
  message?: string;
  data?: string;
}

export interface MemoryRecord {
  id: string;
  owner: string;
  kind: MemoryKind;
  content: string;
  source: string;
  confidence: number;
  tags: string[];
  createdAt: string;
}

export interface SkillInfo {
  name: string;
  version: string;
  description: string;
  trust: string;
  source: string;
  pending: boolean;
}

export interface McpServer {
  id: string;
  name: string;
  description: string;
  transport: "stdio" | "http";
  command?: string;
  args: string[];
  url?: string;
  trust: string;
  isCatalogEntry: boolean;
}

/** A recurring (`cron`) or one-off (`runAt`, ISO-8601) job. */
export interface ScheduleSpec {
  name: string;
  botId: string;
  prompt: string;
  cron?: string;
  runAt?: string;
  timeZone?: string;
  enabled?: boolean;
}

export interface ScheduleJob extends Required<Pick<ScheduleSpec, "name" | "botId" | "prompt">> {
  id: string;
  cron: string;
  timeZone: string;
  enabled: boolean;
  nextRunAt?: string;
  lastRunAt?: string;
  runCount: number;
}

export interface HostInfo {
  id: string;
  name: string;
  kind: string;
  os: string;
  architecture: string;
  processorCount: number;
  status: string;
}

export interface SystemInfo {
  product: string;
  version: string;
  credits: string;
  creditsEn: string;
  modelConfigured: boolean;
  profiles: string[];
}

export interface ClientOptions {
  /** Server URL (default http://localhost:5170). */
  baseUrl?: string;
  /** Required when the server sets Marbots:ApiKey. */
  apiKey?: string;
  /** Custom fetch (tests, proxies). */
  fetch?: typeof fetch;
}
