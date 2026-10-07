//! Typed models and names. Wire shapes follow the server's `/api/v1` JSON (camelCase).

use serde::{Deserialize, Deserializer, Serialize};

/// Treats JSON `null` like a missing value (empty list / empty string).
fn null_default<'de, D, T>(d: D) -> std::result::Result<T, D::Error>
where
    D: Deserializer<'de>,
    T: Default + Deserialize<'de>,
{
    Ok(Option::<T>::deserialize(d)?.unwrap_or_default())
}

/// Id of the protected manager bot.
pub const BOSS_MAN: &str = "boss-man";

/// `host_ref` values besides a registered host id.
pub struct HostRef;

impl HostRef {
    /// Run the bot's tools on the server itself.
    pub const LOCAL: &'static str = "local-default";
    /// Let placement choose a computer per thread.
    pub const AUTO: &'static str = "auto";
}

/// Runs the bot's shell commands in a throwaway Docker container with these quotas.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContainerProfile {
    pub image: String,
    pub cpus: f64,
    pub memory_mb: u32,
    pub network: bool,
}

impl ContainerProfile {
    /// `image` with 1 CPU, 1024 MB and network access.
    pub fn new(image: impl Into<String>) -> Self {
        Self {
            image: image.into(),
            cpus: 1.0,
            memory_mb: 1024,
            network: true,
        }
    }
}

/// The learning evaluation's conclusion about a skill.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SkillVerdict {
    CollectingEvidence,
    Healthy,
    Underperforming,
    RollbackRecommended,
    ReadyToPromote,
    DiscardRecommended,
}

/// Built-in tool packs a bot can enable.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum KernelPack {
    Files,
    Search,
    Shell,
    Web,
    Memory,
    Todo,
    Agents,
    /// Boss Man only.
    Management,
    /// Computer use: screenshots, mouse and keyboard on the bot's computer (Windows hosts).
    Desktop,
    /// `spawn_subagents`: parallel temporary copies of the bot.
    Subagents,
}

/// What a bot may do without asking.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PermissionProfile {
    ReadOnly,
    WorkspaceWrite,
    DeveloperSafe,
    Autonomous,
    Manager,
}

/// A bot's lifecycle state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BotStatus {
    Ready,
    Running,
    Paused,
    Archived,
    Degraded,
}

/// A task's lifecycle state.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TaskState {
    Queued,
    Preparing,
    Running,
    WaitingForTool,
    WaitingForAgent,
    WaitingForHuman,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}

impl TaskState {
    /// True for Completed, Failed, Cancelled and TimedOut.
    pub fn is_terminal(self) -> bool {
        matches!(
            self,
            Self::Completed | Self::Failed | Self::Cancelled | Self::TimedOut
        )
    }
}

/// Optional learning after tasks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AutoLearnMode {
    Off,
    MemoryOnly,
    SuggestSkills,
}

/// How far an approval reaches.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ApprovalScope {
    Once,
    Session,
}

/// State of an approval request.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ApprovalState {
    Pending,
    Approved,
    Rejected,
    Expired,
}

/// Kind of long-term memory.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MemoryKind {
    Semantic,
    Episodic,
    Procedural,
    Relational,
    Artifact,
}

/// Types of events on the live stream. Types newer than this SDK deserialize as [`EventType::Unknown`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum EventType {
    BotCreated,
    BotUpdated,
    BotDeleted,
    BotStateChanged,
    MessageAdded,
    TaskCreated,
    TaskStateChanged,
    TaskDelegated,
    TaskProgressed,
    AgentThinkingStarted,
    AgentThinkingCompleted,
    ToolCallStarted,
    ToolCallCompleted,
    ApprovalRequested,
    ApprovalResolved,
    MemoryWritten,
    SkillLoaded,
    ContextCompacted,
    AutoLearnCandidateCreated,
    ScheduleTriggered,
    HostConnected,
    TodoUpdated,
    SettingsChanged,
    #[serde(other)]
    Unknown,
}

/// Builds a bot's model setting.
pub struct ModelRef;

impl ModelRef {
    /// Follow the workspace default model.
    pub const DEFAULT: &'static str = "default";

    /// A direct provider/model pair, e.g. `ModelRef::of("azure", "gpt-5.6-luna")`.
    ///
    /// # Panics
    /// When `provider` or `model` is empty or `provider` contains `/`.
    pub fn of(provider: &str, model: &str) -> String {
        assert!(
            !provider.is_empty() && !model.is_empty() && !provider.contains('/'),
            "provider and model are required; provider cannot contain '/'"
        );
        format!("{provider}/{model}")
    }

    /// A named model profile configured in Settings.
    pub fn profile(name: &str) -> String {
        name.to_string()
    }
}

/// A durable AI teammate.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Bot {
    pub id: String,
    pub name: String,
    #[serde(default, deserialize_with = "null_default")]
    pub role: String,
    #[serde(default, deserialize_with = "null_default")]
    pub description: String,
    #[serde(default, deserialize_with = "null_default")]
    pub persona: String,
    #[serde(default, deserialize_with = "null_default")]
    pub color: String,
    /// `"default"` (workspace default), a profile name, or `"provider/model"`.
    #[serde(rename = "modelProfile", default, deserialize_with = "null_default")]
    pub model: String,
    #[serde(default, deserialize_with = "null_default")]
    pub kernel_functions: Vec<KernelPack>,
    #[serde(default, deserialize_with = "null_default")]
    pub skills: Vec<String>,
    #[serde(default, deserialize_with = "null_default")]
    pub mcp_servers: Vec<String>,
    pub permission_profile: PermissionProfile,
    pub auto_learn: AutoLearnMode,
    #[serde(default)]
    pub short_term_memory: bool,
    #[serde(default)]
    pub long_term_memory: bool,
    #[serde(default)]
    pub max_steps: u32,
    pub status: BotStatus,
    #[serde(default)]
    pub is_system: bool,
    pub template_id: Option<String>,
    /// Where the bot's files/shell/desktop tools run: [`HostRef::LOCAL`], a host id or [`HostRef::AUTO`].
    #[serde(default, deserialize_with = "null_default")]
    pub host_ref: String,
    pub container: Option<ContainerProfile>,
}

impl Bot {
    /// True when the bot follows the workspace default model.
    pub fn uses_default_model(&self) -> bool {
        self.model.is_empty() || self.model == ModelRef::DEFAULT
    }
}

/// Options for creating or updating a bot.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BotSpec {
    #[serde(skip_serializing_if = "String::is_empty")]
    id: String,
    name: String,
    role: String,
    description: String,
    persona: String,
    color: String,
    #[serde(rename = "modelProfile")]
    model: String,
    kernel_functions: Vec<KernelPack>,
    skills: Vec<String>,
    mcp_servers: Vec<String>,
    permission_profile: PermissionProfile,
    auto_learn: AutoLearnMode,
    short_term_memory: bool,
    long_term_memory: bool,
    max_steps: u32,
    host_ref: String,
    container: Option<ContainerProfile>,
}

impl BotSpec {
    /// A spec for a bot named `name` with the server defaults.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            id: String::new(),
            name: name.into(),
            role: String::new(),
            description: String::new(),
            persona: String::new(),
            color: "#2C3BA3".into(),
            model: ModelRef::DEFAULT.into(),
            kernel_functions: vec![
                KernelPack::Files,
                KernelPack::Search,
                KernelPack::Web,
                KernelPack::Memory,
                KernelPack::Todo,
            ],
            skills: Vec::new(),
            mcp_servers: Vec::new(),
            permission_profile: PermissionProfile::DeveloperSafe,
            auto_learn: AutoLearnMode::Off,
            short_term_memory: true,
            long_term_memory: true,
            max_steps: 24,
            host_ref: HostRef::LOCAL.into(),
            container: None,
        }
    }
    /// [`HostRef::LOCAL`] (default), a host id, or [`HostRef::AUTO`].
    pub fn host_ref(mut self, v: impl Into<String>) -> Self {
        self.host_ref = v.into();
        self
    }
    /// Run the bot's shell commands in a Docker container.
    pub fn container(mut self, v: ContainerProfile) -> Self {
        self.container = Some(v);
        self
    }
    pub fn role(mut self, v: impl Into<String>) -> Self {
        self.role = v.into();
        self
    }
    pub fn description(mut self, v: impl Into<String>) -> Self {
        self.description = v.into();
        self
    }
    pub fn persona(mut self, v: impl Into<String>) -> Self {
        self.persona = v.into();
        self
    }
    pub fn color(mut self, v: impl Into<String>) -> Self {
        self.color = v.into();
        self
    }
    /// [`ModelRef::DEFAULT`], [`ModelRef::of`] or a profile name.
    pub fn model(mut self, v: impl Into<String>) -> Self {
        self.model = v.into();
        self
    }
    pub fn kernel_functions(mut self, v: impl IntoIterator<Item = KernelPack>) -> Self {
        self.kernel_functions = v.into_iter().collect();
        self
    }
    pub fn skills(mut self, v: impl IntoIterator<Item = impl Into<String>>) -> Self {
        self.skills = v.into_iter().map(Into::into).collect();
        self
    }
    pub fn mcp_servers(mut self, v: impl IntoIterator<Item = impl Into<String>>) -> Self {
        self.mcp_servers = v.into_iter().map(Into::into).collect();
        self
    }
    pub fn permission_profile(mut self, v: PermissionProfile) -> Self {
        self.permission_profile = v;
        self
    }
    pub fn auto_learn(mut self, v: AutoLearnMode) -> Self {
        self.auto_learn = v;
        self
    }
    pub fn memory(mut self, short_term: bool, long_term: bool) -> Self {
        self.short_term_memory = short_term;
        self.long_term_memory = long_term;
        self
    }
    pub fn max_steps(mut self, v: u32) -> Self {
        self.max_steps = v;
        self
    }
    pub(crate) fn with_id(mut self, id: &str) -> Self {
        self.id = id.to_string();
        self
    }
}

/// A ready-made bot role from the template gallery.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BotTemplate {
    pub id: String,
    pub name: String,
    pub category: String,
    #[serde(default, deserialize_with = "null_default")]
    pub role: String,
    #[serde(default, deserialize_with = "null_default")]
    pub description: String,
    #[serde(rename = "modelProfile", default, deserialize_with = "null_default")]
    pub model: String,
    #[serde(default, deserialize_with = "null_default")]
    pub skills: Vec<String>,
    #[serde(default, deserialize_with = "null_default")]
    pub kernel_functions: Vec<KernelPack>,
    #[serde(default, deserialize_with = "null_default")]
    pub tags: Vec<String>,
    pub permission_profile: PermissionProfile,
    #[serde(default)]
    pub is_built_in: bool,
}

/// A bot's model setting and the model it actually runs on.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BotModelInfo {
    pub bot_id: String,
    pub setting: String,
    /// The provider/model the bot runs on.
    pub effective: String,
    pub uses_default: bool,
    pub warning: Option<String>,
}

/// A named model profile.
#[derive(Debug, Clone, Deserialize)]
pub struct ModelProfileInfo {
    pub name: String,
    pub provider: String,
    pub model: String,
    #[serde(default, deserialize_with = "null_default")]
    pub fallbacks: Vec<String>,
}

/// The workspace default model, the choices and the profiles.
#[derive(Debug, Clone, Deserialize)]
pub struct ModelCatalog {
    /// The workspace default provider/model.
    pub default: String,
    pub choices: Vec<String>,
    pub profiles: Vec<ModelProfileInfo>,
}

/// A conversation with one bot.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChatThread {
    pub id: String,
    pub title: String,
    pub bot_id: String,
    #[serde(default)]
    pub pinned: bool,
    #[serde(default)]
    pub archived: bool,
}

/// A tool invocation requested by a model.
#[derive(Debug, Clone, Deserialize)]
pub struct ToolCall {
    pub id: String,
    pub name: String,
    pub arguments: String,
}

/// One chat message.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChatMessage {
    pub id: String,
    pub thread_id: String,
    pub seq: i64,
    pub role: String,
    pub author: String,
    pub content: String,
    #[serde(default, deserialize_with = "null_default")]
    pub tool_calls: Vec<ToolCall>,
    pub tool_name: Option<String>,
    pub task_id: Option<String>,
}

/// A durable unit of work.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskRecord {
    pub id: String,
    pub parent_task_id: Option<String>,
    pub thread_id: String,
    pub bot_id: String,
    #[serde(default)]
    pub depth: u32,
    pub objective: String,
    pub state: TaskState,
    pub result: Option<String>,
    pub error: Option<String>,
    pub current_activity: Option<String>,
    /// The provider/model that served the latest step.
    pub model: Option<String>,
    #[serde(default)]
    pub steps: u32,
    #[serde(default)]
    pub input_tokens: u64,
    #[serde(default)]
    pub output_tokens: u64,
    #[serde(default)]
    pub cost_usd: f64,
}

/// Returned by `threads().send(...)`; `reply` is set when the call waited for the bot.
#[derive(Debug, Clone, Deserialize)]
pub struct SendResult {
    pub task: TaskRecord,
    pub reply: Option<ChatMessage>,
}

impl SendResult {
    /// The reply text, or the task result/error.
    pub fn text(&self) -> &str {
        self.reply
            .as_ref()
            .map(|r| r.content.as_str())
            .or(self.task.result.as_deref())
            .or(self.task.error.as_deref())
            .unwrap_or("")
    }
}

/// A file in a thread's project workspace.
#[derive(Debug, Clone, Deserialize)]
pub struct WorkspaceFile {
    pub path: String,
    pub size: u64,
    /// Set when the file lives on a remote agent host (download with `?host=<id>`).
    pub host: Option<String>,
    pub host_name: Option<String>,
}

/// A risky action waiting for (or resolved by) a human.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApprovalRequest {
    pub id: String,
    pub task_id: String,
    pub thread_id: String,
    pub bot_id: String,
    pub tool_name: String,
    pub arguments: String,
    pub reason: String,
    pub state: ApprovalState,
    pub resolved_by: Option<String>,
}

/// One item on the live event stream.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AgentEvent {
    pub id: i64,
    #[serde(rename = "type")]
    pub event_type: EventType,
    pub timestamp: String,
    pub thread_id: Option<String>,
    pub task_id: Option<String>,
    pub bot_id: Option<String>,
    pub message: Option<String>,
    pub data: Option<String>,
}

impl AgentEvent {
    /// True when this event says a task finished.
    pub fn is_task_finished(&self) -> bool {
        self.event_type == EventType::TaskStateChanged
            && self
                .data
                .as_deref()
                .and_then(|d| {
                    serde_json::from_value::<TaskState>(serde_json::Value::String(d.to_string()))
                        .ok()
                })
                .is_some_and(TaskState::is_terminal)
    }
}

/// A long-term memory.
#[derive(Debug, Clone, Deserialize)]
pub struct MemoryRecord {
    pub id: String,
    pub owner: String,
    pub kind: MemoryKind,
    pub content: String,
    pub source: String,
    pub confidence: f64,
}

/// An installed SKILL.md package.
#[derive(Debug, Clone, Deserialize)]
pub struct SkillInfo {
    pub name: String,
    pub version: String,
    pub description: String,
    pub trust: String,
    pub source: String,
    #[serde(default)]
    pub pending: bool,
}

/// An MCP server from the gallery.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpServer {
    pub id: String,
    pub name: String,
    pub description: String,
    pub transport: String,
    pub command: Option<String>,
    #[serde(default, deserialize_with = "null_default")]
    pub args: Vec<String>,
    pub url: Option<String>,
    pub trust: String,
    #[serde(default)]
    pub is_catalog_entry: bool,
}

/// A recurring (`cron`) or one-off (`run_at`, ISO-8601) job.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleSpec {
    pub name: String,
    pub bot_id: String,
    pub prompt: String,
    pub cron: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_at: Option<String>,
    pub time_zone: String,
}

impl ScheduleSpec {
    /// A recurring job with a five-field cron expression, in UTC.
    pub fn cron(
        name: impl Into<String>,
        bot_id: impl Into<String>,
        prompt: impl Into<String>,
        cron: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            bot_id: bot_id.into(),
            prompt: prompt.into(),
            cron: cron.into(),
            run_at: None,
            time_zone: "UTC".into(),
        }
    }
}

/// A saved schedule.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleJob {
    pub id: String,
    pub name: String,
    pub bot_id: String,
    pub prompt: String,
    pub cron: String,
    pub time_zone: String,
    pub enabled: bool,
    pub next_run_at: Option<String>,
    pub last_run_at: Option<String>,
}

/// A machine that runs bots.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostInfo {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub os: String,
    pub status: String,
    pub processor_count: u32,
    #[serde(default, deserialize_with = "null_default")]
    pub architecture: String,
    #[serde(default, deserialize_with = "null_default")]
    pub agent_version: String,
    /// shell, files, desktop, docker, dotnet, node, python, `pkg:winget` …
    #[serde(default, deserialize_with = "null_default")]
    pub capabilities: Vec<String>,
    pub metrics: Option<HostMetrics>,
    pub installed_via: Option<String>,
}

/// A host's latest load report.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostMetrics {
    pub cpu_percent: f64,
    pub free_memory_mb: i64,
    pub running_calls: u32,
    pub free_disk_mb: i64,
}

/// One-time token for `marbots-host enroll`.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EnrollmentToken {
    pub token: String,
    pub expires_at: String,
    pub enroll_command: String,
}

/// SSH bootstrap of an agent host. The password/key is used for that call only.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapOptions {
    host: String,
    port: u16,
    user: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    password: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    private_key: Option<String>,
    name: String,
    server_url: String,
    update_only: bool,
}

impl BootstrapOptions {
    /// `server_url` is the address the new host uses to reach this server (LAN address, not localhost).
    pub fn new(
        host: impl Into<String>,
        user: impl Into<String>,
        server_url: impl Into<String>,
    ) -> Self {
        let host = host.into();
        Self {
            name: host.clone(),
            host,
            port: 22,
            user: user.into(),
            password: None,
            private_key: None,
            server_url: server_url.into(),
            update_only: false,
        }
    }
    pub fn password(mut self, v: impl Into<String>) -> Self {
        self.password = Some(v.into());
        self
    }
    pub fn private_key(mut self, v: impl Into<String>) -> Self {
        self.private_key = Some(v.into());
        self
    }
    pub fn name(mut self, v: impl Into<String>) -> Self {
        self.name = v.into();
        self
    }
    pub fn port(mut self, v: u16) -> Self {
        self.port = v;
        self
    }
    /// Replace the binary and restart, keeping the enrollment.
    pub fn update_only(mut self, v: bool) -> Self {
        self.update_only = v;
        self
    }
}

/// What an SSH bootstrap did, step by step.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapResult {
    pub success: bool,
    pub host_id: Option<String>,
    #[serde(default, deserialize_with = "null_default")]
    pub log: Vec<String>,
    pub error: Option<String>,
}

/// Outcomes of the tasks that loaded one skill version.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillStats {
    #[serde(default, deserialize_with = "null_default")]
    pub name: String,
    #[serde(default, deserialize_with = "null_default")]
    pub version: String,
    pub loads: u64,
    pub successes: u64,
    pub failures: u64,
}

impl SkillStats {
    pub fn runs(&self) -> u64 {
        self.successes + self.failures
    }
    pub fn success_rate(&self) -> f64 {
        if self.runs() == 0 {
            0.0
        } else {
            self.successes as f64 / self.runs() as f64
        }
    }
}

/// Learning evaluation of one skill: outcomes of its current version and a verdict.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SkillEvaluation {
    pub name: String,
    pub version: String,
    pub pending: bool,
    pub current: SkillStats,
    pub previous_version: Option<String>,
    pub previous: Option<SkillStats>,
    pub verdict: SkillVerdict,
    pub reason: String,
    pub can_rollback: bool,
}

/// Server information.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemInfo {
    pub product: String,
    pub version: String,
    pub credits: String,
    pub credits_en: String,
    pub model_configured: bool,
}
