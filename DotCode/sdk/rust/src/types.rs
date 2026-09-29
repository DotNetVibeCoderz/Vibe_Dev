use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::fmt;

/// Errors returned by the SDK.
#[derive(Debug)]
pub enum Error {
    /// A JSON-RPC error returned by the server.
    Rpc {
        code: i64,
        message: String,
    },
    /// The `dotcode` executable could not be started.
    Spawn(String),
    /// The server connection is closed.
    Closed,
    /// No response within the allowed time.
    Timeout,
    Io(std::io::Error),
    Json(serde_json::Error),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Rpc { code, message } => write!(f, "dotcode: {message} (code {code})"),
            Error::Spawn(m) => write!(f, "dotcode: {m}"),
            Error::Closed => write!(f, "dotcode: server connection closed"),
            Error::Timeout => write!(f, "dotcode: request timed out"),
            Error::Io(e) => write!(f, "dotcode: {e}"),
            Error::Json(e) => write!(f, "dotcode: invalid JSON: {e}"),
        }
    }
}

impl std::error::Error for Error {}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Error::Io(e)
    }
}

impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::Json(e)
    }
}

pub type Result<T> = std::result::Result<T, Error>;

/// How tool calls are approved.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PermissionMode {
    #[serde(rename = "default")]
    Default,
    #[serde(rename = "acceptEdits")]
    AcceptEdits,
    #[serde(rename = "auto")]
    Auto,
    #[serde(rename = "plan")]
    Plan,
    #[serde(rename = "bypassPermissions")]
    BypassPermissions,
}

/// Thinking budget for models that support it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ReasoningEffort {
    Off,
    Low,
    Medium,
    High,
    Xhigh,
}

/// Built-in tool names (for [`SessionConfig::with_available_tools`](crate::SessionConfig::with_available_tools)
/// and permission rules).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BuiltinTool {
    Read,
    Write,
    Edit,
    NotebookEdit,
    Glob,
    Grep,
    Bash,
    PowerShell,
    BashOutput,
    KillShell,
    WebFetch,
    WebSearch,
    TodoWrite,
    Agent,
    Skill,
    AskUserQuestion,
    ExitPlanMode,
    #[serde(rename = "LSP")]
    Lsp,
}

impl BuiltinTool {
    /// The tool name as the model sees it.
    pub fn name(self) -> &'static str {
        match self {
            BuiltinTool::Read => "Read",
            BuiltinTool::Write => "Write",
            BuiltinTool::Edit => "Edit",
            BuiltinTool::NotebookEdit => "NotebookEdit",
            BuiltinTool::Glob => "Glob",
            BuiltinTool::Grep => "Grep",
            BuiltinTool::Bash => "Bash",
            BuiltinTool::PowerShell => "PowerShell",
            BuiltinTool::BashOutput => "BashOutput",
            BuiltinTool::KillShell => "KillShell",
            BuiltinTool::WebFetch => "WebFetch",
            BuiltinTool::WebSearch => "WebSearch",
            BuiltinTool::TodoWrite => "TodoWrite",
            BuiltinTool::Agent => "Agent",
            BuiltinTool::Skill => "Skill",
            BuiltinTool::AskUserQuestion => "AskUserQuestion",
            BuiltinTool::ExitPlanMode => "ExitPlanMode",
            BuiltinTool::Lsp => "LSP",
        }
    }

    /// A permission rule, e.g. `BuiltinTool::Bash.rule("npm test:*")` → `Bash(npm test:*)`.
    pub fn rule(self, specifier: &str) -> String {
        format!("{}({specifier})", self.name())
    }
}

impl fmt::Display for BuiltinTool {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.name())
    }
}

/// Why a turn ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum StopReason {
    #[default]
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    Refusal,
    Aborted,
    Error,
}

/// Token consumption.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Usage {
    pub input_tokens: i64,
    pub output_tokens: i64,
    pub cache_read_tokens: i64,
    pub cache_write_tokens: i64,
    pub reasoning_tokens: i64,
}

// ---------------------------------------------------------------- permissions & interaction

/// Sent when a tool call needs approval.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PermissionRequest {
    pub tool_use_id: String,
    pub tool_name: String,
    pub display_name: String,
    pub input: Value,
    pub title: String,
    pub detail: Option<String>,
    /// Rule the user could persist, e.g. `Bash(npm test:*)`.
    pub suggested_rule: Option<String>,
    /// Unified diff preview for file edits.
    pub diff: Option<String>,
    pub parent_tool_use_id: Option<String>,
}

/// Context passed to handlers.
#[derive(Debug, Clone)]
pub struct Invocation {
    pub session_id: String,
}

/// The answer to a [`PermissionRequest`].
#[derive(Debug, Clone, PartialEq)]
pub enum PermissionDecision {
    /// Allow this single call (optionally with modified input).
    ApproveOnce { updated_input: Option<Value> },
    /// Allow matching calls for the rest of the session.
    ApproveForSession { updated_input: Option<Value> },
    /// Allow and persist a rule (`None`: the request's suggested rule).
    ApproveAlways { rule: Option<String> },
    /// Deny; `feedback` is returned to the model.
    Reject { feedback: Option<String> },
}

impl PermissionDecision {
    pub fn approve_once() -> Self {
        PermissionDecision::ApproveOnce {
            updated_input: None,
        }
    }
    pub fn approve_for_session() -> Self {
        PermissionDecision::ApproveForSession {
            updated_input: None,
        }
    }
    pub fn approve_always(rule: Option<String>) -> Self {
        PermissionDecision::ApproveAlways { rule }
    }
    pub fn reject(feedback: Option<String>) -> Self {
        PermissionDecision::Reject { feedback }
    }

    pub(crate) fn wire(&self) -> Value {
        let mut m = serde_json::Map::new();
        let (decision, input, rule, feedback) = match self {
            PermissionDecision::ApproveOnce { updated_input } => {
                ("allow", updated_input, None, None)
            }
            PermissionDecision::ApproveForSession { updated_input } => {
                ("allow_session", updated_input, None, None)
            }
            PermissionDecision::ApproveAlways { rule } => {
                ("allow_always", &None, rule.as_ref(), None)
            }
            PermissionDecision::Reject { feedback } => ("deny", &None, None, feedback.as_ref()),
        };
        m.insert("decision".into(), decision.into());
        if let Some(i) = input {
            m.insert("updatedInput".into(), i.clone());
        }
        if let Some(r) = rule {
            m.insert("rule".into(), r.clone().into());
        }
        if let Some(f) = feedback {
            m.insert("feedback".into(), f.clone().into());
        }
        Value::Object(m)
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct QuestionOption {
    pub label: String,
    pub description: Option<String>,
}

/// Asked by the model through the AskUserQuestion tool.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct UserQuestion {
    pub question: String,
    pub header: String,
    pub multi_select: bool,
    pub options: Vec<QuestionOption>,
}

/// Answer to one [`UserQuestion`]: option label(s) (comma-separated for multi-select) or free text.
#[derive(Debug, Clone, Serialize)]
pub struct UserQuestionAnswer {
    pub question: String,
    pub answer: String,
}

/// The decision when the agent leaves plan mode.
#[derive(Debug, Clone, PartialEq)]
pub enum ExitPlanModeResult {
    Approve,
    /// Approve and continue in `acceptEdits` mode.
    ApproveAndAcceptEdits,
    /// Keep planning; `feedback` is returned to the model.
    Reject {
        feedback: Option<String>,
    },
}

// ---------------------------------------------------------------- configuration

/// Wire protocol of a model provider.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProviderType {
    Anthropic,
    #[serde(rename = "openai")]
    OpenAi,
    Azure,
    Gemini,
    #[serde(rename = "deepseek")]
    DeepSeek,
    Ollama,
    #[serde(rename = "openai-compatible")]
    OpenAiCompatible,
    Bedrock,
    Vertex,
    VertexGemini,
    Mock,
}

/// A named model provider (bring your own key). Values may reference environment variables: `"${env:MY_KEY}"`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderConfig {
    #[serde(rename = "type")]
    pub provider_type: ProviderType,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub base_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub api_key: Option<String>,
    /// OpenAI family: `responses` or `chat`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub api: Option<String>,
    #[serde(skip_serializing_if = "BTreeMap::is_empty")]
    pub headers: BTreeMap<String, String>,
    /// Quirk profile for OpenAI-compatible servers (deepseek, openrouter, lmstudio, vllm, litellm, groq, …).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub profile: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub models: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub timeout_seconds: Option<u32>,
    /// Ollama context window.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub num_ctx: Option<u32>,
    /// AWS region (bedrock) or Google Cloud location (vertex).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub region: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub aws_profile: Option<String>,
    /// Google Cloud project (vertex).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub project: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub credentials_file: Option<String>,
    /// Azure: `entra` for Microsoft Entra ID tokens instead of an API key.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub auth: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tenant_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_secret: Option<String>,
    /// Scripted provider (tests): path to a script file.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub script: Option<String>,
}

impl ProviderConfig {
    pub fn new(provider_type: ProviderType) -> Self {
        ProviderConfig {
            provider_type,
            base_url: None,
            api_key: None,
            api: None,
            headers: BTreeMap::new(),
            profile: None,
            models: Vec::new(),
            timeout_seconds: None,
            num_ctx: None,
            region: None,
            aws_profile: None,
            project: None,
            credentials_file: None,
            auth: None,
            tenant_id: None,
            client_id: None,
            client_secret: None,
            script: None,
        }
    }
    pub fn with_base_url(mut self, url: impl Into<String>) -> Self {
        self.base_url = Some(url.into());
        self
    }
    pub fn with_api_key(mut self, key: impl Into<String>) -> Self {
        self.api_key = Some(key.into());
        self
    }
    pub fn with_models(mut self, models: impl IntoIterator<Item = impl Into<String>>) -> Self {
        self.models = models.into_iter().map(Into::into).collect();
        self
    }
    pub fn with_script(mut self, path: impl Into<String>) -> Self {
        self.script = Some(path.into());
        self
    }
}

/// An MCP server for a session.
#[derive(Debug, Clone)]
pub enum McpServerConfig {
    /// Started as a child process.
    Stdio {
        command: String,
        args: Vec<String>,
        env: BTreeMap<String, String>,
    },
    /// Remote server (streamable HTTP).
    Http {
        url: String,
        headers: BTreeMap<String, String>,
    },
    /// Remote server (legacy SSE).
    Sse {
        url: String,
        headers: BTreeMap<String, String>,
    },
}

impl McpServerConfig {
    pub fn stdio(
        command: impl Into<String>,
        args: impl IntoIterator<Item = impl Into<String>>,
    ) -> Self {
        McpServerConfig::Stdio {
            command: command.into(),
            args: args.into_iter().map(Into::into).collect(),
            env: BTreeMap::new(),
        }
    }
    pub fn http(url: impl Into<String>) -> Self {
        McpServerConfig::Http {
            url: url.into(),
            headers: BTreeMap::new(),
        }
    }

    pub(crate) fn wire(&self) -> Value {
        match self {
            McpServerConfig::Stdio { command, args, env } => {
                serde_json::json!({"type": "stdio", "command": command, "args": args, "env": env})
            }
            McpServerConfig::Http { url, headers } => {
                serde_json::json!({"type": "http", "url": url, "headers": headers})
            }
            McpServerConfig::Sse { url, headers } => {
                serde_json::json!({"type": "sse", "url": url, "headers": headers})
            }
        }
    }
}

/// Customizes the system prompt.
#[derive(Debug, Clone)]
pub enum SystemMessageConfig {
    /// Added to DotCode's system prompt.
    Append(String),
    /// Replaces it entirely.
    Replace(String),
}

/// Sent with a message.
#[derive(Debug, Clone)]
pub enum Attachment {
    /// A file the agent should read.
    File { path: String },
    /// Base64 image data (`image/png`, `image/jpeg`, `image/gif`, `image/webp`).
    Image { data: String, media_type: String },
}

/// One user message. `&str` and `String` convert into it.
#[derive(Debug, Clone, Default)]
pub struct MessageOptions {
    pub prompt: String,
    pub attachments: Vec<Attachment>,
}

impl MessageOptions {
    pub fn new(prompt: impl Into<String>) -> Self {
        MessageOptions {
            prompt: prompt.into(),
            attachments: Vec::new(),
        }
    }
    pub fn with_attachments(mut self, attachments: Vec<Attachment>) -> Self {
        self.attachments = attachments;
        self
    }
}

impl From<&str> for MessageOptions {
    fn from(prompt: &str) -> Self {
        MessageOptions::new(prompt)
    }
}

impl From<String> for MessageOptions {
    fn from(prompt: String) -> Self {
        MessageOptions::new(prompt)
    }
}

// ---------------------------------------------------------------- results

/// Outcome of one turn.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SendResult {
    pub session_id: String,
    pub stop_reason: StopReason,
    pub result: String,
    pub is_error: bool,
    pub error: Option<String>,
    pub duration_ms: i64,
    pub num_model_calls: i64,
    pub cost_usd: f64,
    pub total_cost_usd: f64,
    pub usage: Usage,
}

/// Git worktree a session runs in (when created with a worktree).
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct WorktreeInfo {
    pub name: String,
    pub path: String,
    pub branch: String,
}

/// Describes a created session.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SessionInfo {
    pub session_id: String,
    pub model: String,
    pub cwd: String,
    pub permission_mode: String,
    pub message_count: i64,
    pub total_cost_usd: f64,
    pub transcript_path: Option<String>,
    pub worktree: Option<WorktreeInfo>,
    pub tools: Vec<String>,
}

/// A saved session.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SessionMetadata {
    pub id: String,
    pub title: Option<String>,
    pub first_prompt: String,
    pub modified: String,
    pub message_count: i64,
}

/// A model offered by a configured provider.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ModelInfo {
    pub provider: String,
    pub id: String,
    pub qualified_id: String,
}

/// A tool available to the model.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ToolInfo {
    pub name: String,
    pub description: String,
    pub input_schema: Value,
}
