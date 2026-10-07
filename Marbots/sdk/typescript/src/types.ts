// Public types of the Marbots SDK. Wire shapes follow the server's /api/v1 JSON (camelCase).

// ---------------------------------------------------------------- names (typed so typos are compile errors)

/** Id of the protected manager bot. */
export const BOSS_MAN = "boss-man";

/** `hostRef` values besides a registered host id: the server itself, or automatic placement. */
export const HostRef = { Local: "local-default", Auto: "auto" } as const;

export type SkillVerdict =
  | "CollectingEvidence" | "Healthy" | "Underperforming" | "RollbackRecommended" | "ReadyToPromote" | "DiscardRecommended";

/** Run the bot's shell commands in a throwaway Docker container with these quotas. */
export interface ContainerProfile {
  image: string;
  cpus?: number;
  memoryMb?: number;
  network?: boolean;
}

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
  /** Computer use: screenshots, mouse and keyboard on the bot's computer (Windows hosts). */
  Desktop: "desktop",
  /** spawn_subagents: parallel temporary copies of the bot. */
  Subagents: "subagents",
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
  /** "local-default", a host id, or "auto". */
  hostRef: string;
  container?: ContainerProfile;
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
  /** Where the bot's files/shell/desktop tools run (default `HostRef.Local`). */
  hostRef?: string;
  container?: ContainerProfile;
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
  /** Set when the file lives on a remote agent host (download with `?host=<id>`). */
  host?: string;
  hostName?: string;
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

export interface HostMetrics {
  cpuPercent: number;
  freeMemoryMb: number;
  runningCalls: number;
  freeDiskMb: number;
}

export interface HostInfo {
  id: string;
  name: string;
  kind: string;
  os: string;
  architecture: string;
  processorCount: number;
  status: string;
  agentVersion: string;
  /** shell, files, desktop, docker, dotnet, node, python, "pkg:winget" … */
  capabilities: string[];
  metrics?: HostMetrics;
  installedVia?: string;
}

/** One-time token for `marbots-host enroll`. */
export interface EnrollmentToken {
  token: string;
  expiresAt: string;
  enrollCommand: string;
}

export interface BootstrapOptions {
  host: string;
  user: string;
  /** Address the new host uses to reach this server (LAN address, not localhost). */
  serverUrl: string;
  name?: string;
  /** Used for this call only; never stored. */
  password?: string;
  privateKey?: string;
  port?: number;
  /** Replace the binary and restart, keeping the enrollment. */
  updateOnly?: boolean;
}

export interface BootstrapResult {
  success: boolean;
  hostId?: string;
  log: string[];
  error?: string;
}

export interface SkillStats {
  name: string;
  version: string;
  loads: number;
  successes: number;
  failures: number;
  runs: number;
  successRate: number;
}

/** Learning evaluation of one skill: outcomes of its current version and a verdict. */
export interface SkillEvaluation {
  name: string;
  version: string;
  pending: boolean;
  current: SkillStats;
  previousVersion?: string;
  previous?: SkillStats;
  verdict: SkillVerdict;
  reason: string;
  canRollback: boolean;
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
  /** A tenant key (mbk_…), the platform key (Marbots:ApiKey) or an OIDC access token. */
  apiKey?: string;
  /** Tenant to act in with the platform key or a token (or end baseUrl in /t/<tenant>). */
  tenant?: string;
  /** Custom fetch (tests, proxies). */
  fetch?: typeof fetch;
}

/** Roles inside a tenant, weakest first. */
export type TenantRole = "Viewer" | "Operator" | "Admin" | "Owner";

export interface TenantInfo {
  id: string;
  name: string;
  disabled: boolean;
  createdAt: string;
}

/** A tenant API key as listed; the key itself is only returned when created. */
export interface ApiKeyInfo {
  id: string;
  tenant: string;
  name: string;
  role: TenantRole;
  prefix: string;
  createdAt: string;
  lastUsedAt?: string;
}

/** A freshly created key — `key` is shown only this once. */
export interface NewApiKey {
  id: string;
  key: string;
  tenant: string;
  role: TenantRole;
}

/** An OIDC user's role in a tenant (matched by e-mail or subject). */
export interface TenantMember {
  tenant: string;
  subject: string;
  role: TenantRole;
}

export interface WhoAmI {
  tenant: string;
  role: TenantRole;
  user?: string;
  platformAdmin: boolean;
  multiTenant: boolean;
  tenants: string[];
}
