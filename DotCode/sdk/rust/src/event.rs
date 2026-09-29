use crate::types::{PermissionMode, StopReason, Usage};
use serde::Deserialize;
use serde_json::Value;

/// An event streamed by the engine (the terminal UI consumes the exact same stream). Match on [`SessionEvent::data`]:
///
/// ```no_run
/// # fn f(event: dotcode_sdk::SessionEvent) {
/// use dotcode_sdk::SessionEventData;
/// match &event.data {
///     SessionEventData::AssistantTextDelta { text } => print!("{text}"),
///     SessionEventData::ToolCompleted { name, output, .. } => println!("{name}: {output}"),
///     SessionEventData::TurnCompleted { cost_usd, .. } => println!("${cost_usd:.4}"),
///     _ => {}
/// }
/// # }
/// ```
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionEvent {
    #[serde(default)]
    pub session_id: Option<String>,
    /// Set for events produced inside a subagent.
    #[serde(default)]
    pub parent_tool_use_id: Option<String>,
    #[serde(flatten)]
    pub data: SessionEventData,
}

impl SessionEvent {
    /// True for the `turn.completed` of the top-level agent (not a subagent).
    pub fn is_turn_completed(&self) -> bool {
        matches!(self.data, SessionEventData::TurnCompleted { .. })
            && self.parent_tool_use_id.as_deref().unwrap_or("").is_empty()
    }

    pub(crate) fn parse(raw: &Value) -> SessionEvent {
        serde_json::from_value(raw.clone()).unwrap_or(SessionEvent {
            session_id: raw["sessionId"].as_str().map(str::to_string),
            parent_tool_use_id: raw["parentToolUseId"].as_str().map(str::to_string),
            data: SessionEventData::Unknown,
        })
    }
}

/// A tool call requested by the model.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct ToolCallInfo {
    pub id: String,
    pub name: String,
    pub input: Value,
}

/// One entry of the agent's todo list; `status` is `Pending`, `InProgress` or `Completed`.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TodoItem {
    pub content: String,
    pub status: String,
    pub active_form: Option<String>,
}

/// The payload of a [`SessionEvent`], tagged by the protocol `type`.
#[derive(Debug, Clone, Deserialize)]
#[serde(tag = "type")]
pub enum SessionEventData {
    #[serde(rename = "user.message")]
    UserMessage { text: String },
    #[serde(rename = "assistant.text.delta")]
    AssistantTextDelta { text: String },
    #[serde(rename = "assistant.thinking.delta")]
    AssistantThinkingDelta { text: String },
    #[serde(rename = "assistant.message", rename_all = "camelCase")]
    AssistantMessage {
        message_id: String,
        text: String,
        #[serde(default)]
        thinking: Option<String>,
        #[serde(default)]
        tool_calls: Vec<ToolCallInfo>,
        model: String,
    },
    #[serde(rename = "tool.started", rename_all = "camelCase")]
    ToolStarted {
        tool_use_id: String,
        name: String,
        display_name: String,
        #[serde(default)]
        input: Value,
    },
    #[serde(rename = "tool.progress", rename_all = "camelCase")]
    ToolProgress { tool_use_id: String, text: String },
    #[serde(rename = "tool.completed", rename_all = "camelCase")]
    ToolCompleted {
        tool_use_id: String,
        name: String,
        is_error: bool,
        summary: String,
        output: String,
        #[serde(default)]
        diff: Option<String>,
        #[serde(default)]
        duration_ms: i64,
        #[serde(default)]
        rejected: bool,
    },
    #[serde(rename = "todo.updated")]
    TodoUpdated { todos: Vec<TodoItem> },
    #[serde(rename = "subagent.started", rename_all = "camelCase")]
    SubagentStarted {
        tool_use_id: String,
        agent_type: String,
        description: String,
        model: String,
    },
    #[serde(rename = "subagent.completed", rename_all = "camelCase")]
    SubagentCompleted {
        tool_use_id: String,
        agent_type: String,
        usage: Usage,
        duration_ms: i64,
        tool_uses: i64,
    },
    #[serde(rename = "context.compacted", rename_all = "camelCase")]
    ContextCompacted {
        tokens_before: i64,
        tokens_after: i64,
        automatic: bool,
    },
    #[serde(rename = "usage.updated", rename_all = "camelCase")]
    UsageUpdated {
        turn_usage: Usage,
        session_usage: Usage,
        session_cost_usd: f64,
        context_tokens: i64,
        context_window: i64,
    },
    #[serde(rename = "model.changed")]
    ModelChanged { model: String },
    #[serde(rename = "model.fallback")]
    ModelFallback {
        from: String,
        to: String,
        reason: String,
    },
    #[serde(rename = "retry", rename_all = "camelCase")]
    Retry {
        attempt: i64,
        max_attempts: i64,
        delay_seconds: f64,
        reason: String,
    },
    /// `level` is `Info`, `Warning` or `Error`.
    #[serde(rename = "notice")]
    Notice { level: String, text: String },
    #[serde(rename = "error")]
    Error {
        code: String,
        message: String,
        retryable: bool,
    },
    #[serde(rename = "mode.changed")]
    ModeChanged { mode: PermissionMode },
    #[serde(rename = "turn.completed", rename_all = "camelCase")]
    TurnCompleted {
        stop_reason: StopReason,
        result_text: String,
        usage: Usage,
        cost_usd: f64,
        duration_ms: i64,
        num_model_calls: i64,
        is_error: bool,
    },
    /// An event type this SDK version does not know.
    #[serde(other)]
    Unknown,
}
