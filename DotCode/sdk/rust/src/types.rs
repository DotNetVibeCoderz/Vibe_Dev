use serde::{Deserialize, Serialize};
use serde_json::Value;
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

/// An engine event: `session.started`, `user.message`, `assistant.text.delta`, `assistant.thinking.delta`,
/// `assistant.message`, `tool.started`, `tool.progress`, `tool.completed`, `todo.updated`, `subagent.started`,
/// `subagent.completed`, `context.compacted`, `usage.updated`, `model.changed`, `model.fallback`, `retry`,
/// `notice`, `error`, `mode.changed`, `turn.completed`. Common fields are decoded; `raw` holds the full JSON.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Event {
    #[serde(rename = "type")]
    pub kind: String,
    pub session_id: Option<String>,
    pub parent_tool_use_id: Option<String>,
    pub text: Option<String>,
    pub tool_use_id: Option<String>,
    pub name: Option<String>,
    pub display_name: Option<String>,
    pub summary: Option<String>,
    pub output: Option<String>,
    pub diff: Option<String>,
    pub is_error: bool,
    pub model: Option<String>,
    pub result_text: Option<String>,
    pub stop_reason: Option<String>,
    pub cost_usd: f64,
    pub duration_ms: i64,
    pub num_model_calls: i64,
    pub message: Option<String>,
    #[serde(skip)]
    pub raw: Value,
}

impl Event {
    /// True for the `turn.completed` of the top-level agent (not a subagent).
    pub fn is_turn_completed(&self) -> bool {
        self.kind == "turn.completed" && self.parent_tool_use_id.as_deref().unwrap_or("").is_empty()
    }
}

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
    pub suggested_rule: Option<String>,
    pub diff: Option<String>,
}

/// Answer to a [`PermissionRequest`]. `decision`: `allow` | `allow_always` | `allow_session` | `deny`.
#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PermissionDecision {
    pub decision: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub feedback: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rule: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_input: Option<Value>,
}

impl PermissionDecision {
    pub fn allow() -> Self {
        Self {
            decision: "allow".into(),
            ..Default::default()
        }
    }
    /// Allow and remember the rule (e.g. `Bash(npm test:*)`) in the project's local settings.
    pub fn allow_always(rule: impl Into<String>) -> Self {
        Self {
            decision: "allow_always".into(),
            rule: Some(rule.into()),
            ..Default::default()
        }
    }
    /// Allow for the rest of the session (edits: switches to accept-edits; others: a session rule).
    pub fn allow_session() -> Self {
        Self {
            decision: "allow_session".into(),
            ..Default::default()
        }
    }
    pub fn deny() -> Self {
        Self {
            decision: "deny".into(),
            ..Default::default()
        }
    }
    /// Deny and tell the model what to do instead.
    pub fn deny_with(feedback: impl Into<String>) -> Self {
        Self {
            decision: "deny".into(),
            feedback: Some(feedback.into()),
            ..Default::default()
        }
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

/// Answer to one [`UserQuestion`].
#[derive(Debug, Clone, Serialize)]
pub struct UserQuestionAnswer {
    pub question: String,
    pub answer: String,
}

/// Outcome of one turn.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SendResult {
    pub session_id: String,
    pub stop_reason: String,
    pub result: String,
    pub is_error: bool,
    pub error: Option<String>,
    pub duration_ms: i64,
    pub num_model_calls: i64,
    pub cost_usd: f64,
    pub total_cost_usd: f64,
    pub usage: Usage,
}

/// Git worktree a session runs in (when created with `worktree`).
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

/// An MCP server for a session (stdio: `command`/`args`; remote: `type` = `http` | `sse` and `url`).
#[derive(Debug, Clone, Default, Serialize)]
pub struct McpServer {
    #[serde(rename = "type", skip_serializing_if = "Option::is_none")]
    pub kind: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub command: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub args: Vec<String>,
    #[serde(skip_serializing_if = "std::collections::HashMap::is_empty")]
    pub env: std::collections::HashMap<String, String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
    #[serde(skip_serializing_if = "std::collections::HashMap::is_empty")]
    pub headers: std::collections::HashMap<String, String>,
}
