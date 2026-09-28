use crate::client::Inner;
use crate::types::{
    Error, Event, McpServer, PermissionDecision, PermissionRequest, Result, SendResult,
    SessionInfo, UserQuestion, UserQuestionAnswer,
};
use serde_json::{json, Map, Value};
use std::collections::HashMap;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

type ToolHandler = dyn Fn(&Value) -> std::result::Result<String, String> + Send + Sync;

/// A tool implemented by your application and offered to the model.
#[derive(Clone)]
pub struct Tool {
    pub name: String,
    pub description: String,
    /// JSON Schema of the input object.
    pub input_schema: Value,
    /// Read-only tools can run in parallel and need no approval.
    pub read_only: bool,
    handler: Arc<ToolHandler>,
}

impl Tool {
    /// `handler` receives the input JSON and returns the tool output (or an error message shown to the model).
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        input_schema: Value,
        handler: impl Fn(&Value) -> std::result::Result<String, String> + Send + Sync + 'static,
    ) -> Tool {
        Tool {
            name: name.into(),
            description: description.into(),
            input_schema,
            read_only: false,
            handler: Arc::new(handler),
        }
    }

    pub fn read_only(mut self) -> Tool {
        self.read_only = true;
        self
    }
}

impl std::fmt::Debug for Tool {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Tool")
            .field("name", &self.name)
            .field("read_only", &self.read_only)
            .finish()
    }
}

pub type PermissionHandler = Arc<dyn Fn(&PermissionRequest) -> PermissionDecision + Send + Sync>;
pub type QuestionHandler = Arc<dyn Fn(&[UserQuestion]) -> Vec<UserQuestionAnswer> + Send + Sync>;
pub type PlanHandler = Arc<dyn Fn(&str) -> bool + Send + Sync>;
pub type EventHandler = Arc<dyn Fn(&Event) + Send + Sync>;

/// Session configuration; unset fields fall back to the user's DotCode settings.
#[derive(Clone, Default)]
pub struct SessionOptions {
    /// `provider:model`, an alias or a role, e.g. `anthropic:claude-sonnet-4-5`, `openai:gpt-5`, `ollama:qwen3-coder`.
    pub model: Option<String>,
    pub fallback_model: Option<String>,
    pub cwd: Option<String>,
    /// `default` | `acceptEdits` | `auto` | `plan` | `bypassPermissions`
    pub permission_mode: Option<String>,
    pub system_prompt: Option<String>,
    pub append_system_prompt: Option<String>,
    /// Permission rules, e.g. `Bash(npm test:*)`.
    pub allowed_tools: Vec<String>,
    pub disallowed_tools: Vec<String>,
    /// Restrict the built-in tools (empty = all).
    pub builtin_tools: Vec<String>,
    pub tools: Vec<Tool>,
    pub mcp_servers: HashMap<String, McpServer>,
    /// Inline settings merged over settings files, e.g. `{"providers": {...}}` for bring-your-own-key.
    pub settings: Option<Value>,
    pub max_turns: Option<u32>,
    /// `off` | `low` | `medium` | `high` | `xhigh`
    pub effort: Option<String>,
    pub persist_session: Option<bool>,
    pub no_mcp: bool,
    /// Run in a fresh git worktree (`Some("")`) or a named one (`Some("name")`).
    pub worktree: Option<String>,
    /// Approves tool calls. Without it the session is deny-by-default.
    pub on_permission_request: Option<PermissionHandler>,
    pub on_question: Option<QuestionHandler>,
    pub on_plan_review: Option<PlanHandler>,
    pub on_event: Option<EventHandler>,
}

impl SessionOptions {
    pub fn model(mut self, model: impl Into<String>) -> Self {
        self.model = Some(model.into());
        self
    }
    pub fn permission_mode(mut self, mode: impl Into<String>) -> Self {
        self.permission_mode = Some(mode.into());
        self
    }
    pub fn settings(mut self, settings: Value) -> Self {
        self.settings = Some(settings);
        self
    }
    pub fn tool(mut self, tool: Tool) -> Self {
        self.tools.push(tool);
        self
    }
    pub fn allow_tool(mut self, rule: impl Into<String>) -> Self {
        self.allowed_tools.push(rule.into());
        self
    }
    pub fn on_permission_request(
        mut self,
        f: impl Fn(&PermissionRequest) -> PermissionDecision + Send + Sync + 'static,
    ) -> Self {
        self.on_permission_request = Some(Arc::new(f));
        self
    }
    pub fn on_question(
        mut self,
        f: impl Fn(&[UserQuestion]) -> Vec<UserQuestionAnswer> + Send + Sync + 'static,
    ) -> Self {
        self.on_question = Some(Arc::new(f));
        self
    }
    pub fn on_plan_review(mut self, f: impl Fn(&str) -> bool + Send + Sync + 'static) -> Self {
        self.on_plan_review = Some(Arc::new(f));
        self
    }
    pub fn on_event(mut self, f: impl Fn(&Event) + Send + Sync + 'static) -> Self {
        self.on_event = Some(Arc::new(f));
        self
    }

    pub(crate) fn wire(&self, default_cwd: Option<&str>) -> Value {
        let mut m = Map::new();
        let mut set = |k: &str, v: &Option<String>| {
            if let Some(v) = v.as_ref().filter(|v| !v.is_empty()) {
                m.insert(k.into(), Value::String(v.clone()));
            }
        };
        set(
            "cwd",
            &self.cwd.clone().or_else(|| default_cwd.map(str::to_string)),
        );
        set("model", &self.model);
        set("fallbackModel", &self.fallback_model);
        set("permissionMode", &self.permission_mode);
        set("systemPrompt", &self.system_prompt);
        set("appendSystemPrompt", &self.append_system_prompt);
        set("effort", &self.effort);
        if !self.allowed_tools.is_empty() {
            m.insert("allowedTools".into(), json!(self.allowed_tools));
        }
        if !self.disallowed_tools.is_empty() {
            m.insert("disallowedTools".into(), json!(self.disallowed_tools));
        }
        if !self.builtin_tools.is_empty() {
            m.insert("tools".into(), json!(self.builtin_tools));
        }
        if !self.mcp_servers.is_empty() {
            m.insert(
                "mcpServers".into(),
                serde_json::to_value(&self.mcp_servers).unwrap_or(Value::Null),
            );
        }
        if let Some(s) = &self.settings {
            m.insert("settings".into(), s.clone());
        }
        if let Some(t) = self.max_turns {
            m.insert("maxTurns".into(), json!(t));
        }
        if let Some(p) = self.persist_session {
            m.insert("persistSession".into(), json!(p));
        }
        if self.no_mcp {
            m.insert("noMcp".into(), json!(true));
        }
        match self.worktree.as_deref() {
            Some("") => {
                m.insert("worktree".into(), json!(true));
            }
            Some(name) => {
                m.insert("worktree".into(), json!(name));
            }
            None => {}
        }
        if !self.tools.is_empty() {
            let tools: Vec<Value> = self
                .tools
                .iter()
                .map(|t| {
                    let schema = if t.input_schema.is_null() { json!({"type": "object", "properties": {}}) } else { t.input_schema.clone() };
                    json!({"name": t.name, "description": t.description, "inputSchema": schema, "readOnly": t.read_only})
                })
                .collect();
            m.insert("hostTools".into(), Value::Array(tools));
        }
        Value::Object(m)
    }
}

pub(crate) enum StreamItem {
    Event(Box<Event>),
    Done(Result<SendResult>),
}

/// Per-session state shared with the reader thread (callbacks and stream watchers).
pub(crate) struct SessionShared {
    options: SessionOptions,
    model: Mutex<String>,
    watchers: Mutex<Vec<Sender<StreamItem>>>,
}

impl SessionShared {
    pub(crate) fn new(options: SessionOptions, model: String) -> Self {
        SessionShared {
            options,
            model: Mutex::new(model),
            watchers: Mutex::new(Vec::new()),
        }
    }

    pub(crate) fn dispatch(&self, raw: &Value) {
        let mut event: Event = serde_json::from_value(raw.clone()).unwrap_or_default();
        event.raw = raw.clone();
        if event.kind == "model.changed" {
            if let Some(m) = &event.model {
                *self.model.lock().unwrap() = m.clone();
            }
        }
        if let Some(f) = &self.options.on_event {
            f(&event);
        }
        self.watchers
            .lock()
            .unwrap()
            .retain(|w| w.send(StreamItem::Event(Box::new(event.clone()))).is_ok());
    }

    pub(crate) fn handle_server_request(
        &self,
        method: &str,
        params: &Value,
    ) -> std::result::Result<Value, String> {
        match method {
            "permission.request" => {
                let request: PermissionRequest =
                    serde_json::from_value(params["request"].clone()).map_err(|e| e.to_string())?;
                let decision = match &self.options.on_permission_request {
                    Some(f) => f(&request),
                    None => PermissionDecision::deny_with(
                        "No permission handler registered in the SDK host (deny by default).",
                    ),
                };
                serde_json::to_value(decision).map_err(|e| e.to_string())
            }
            "user.question" => {
                let questions: Vec<UserQuestion> =
                    serde_json::from_value(params["questions"].clone()).unwrap_or_default();
                let answers = self
                    .options
                    .on_question
                    .as_ref()
                    .map(|f| f(&questions))
                    .unwrap_or_default();
                Ok(json!({"answers": answers}))
            }
            "plan.review" => {
                let approve = self
                    .options
                    .on_plan_review
                    .as_ref()
                    .map(|f| f(params["plan"].as_str().unwrap_or("")))
                    .unwrap_or(true);
                Ok(json!({"approval": if approve { "approve" } else { "reject" }}))
            }
            "tool.call" => {
                let name = params["name"].as_str().unwrap_or("");
                let tool = self
                    .options
                    .tools
                    .iter()
                    .find(|t| t.name == name)
                    .ok_or_else(|| format!("unknown host tool {name}"))?;
                Ok(match (tool.handler)(&params["input"]) {
                    Ok(out) => json!({"content": out}),
                    Err(e) => json!({"content": format!("Error: {e}"), "isError": true}),
                })
            }
            other => Err(format!("unsupported callback {other}")),
        }
    }
}

/// One conversation with the agent.
pub struct Session {
    inner: Arc<Inner>,
    shared: Arc<SessionShared>,
    /// Session id (use it with `Client::resume_session`).
    pub id: String,
    pub info: SessionInfo,
}

impl Session {
    pub(crate) fn new(inner: Arc<Inner>, shared: Arc<SessionShared>, info: SessionInfo) -> Session {
        Session {
            inner,
            shared,
            id: info.session_id.clone(),
            info,
        }
    }

    /// Current model (updated by `set_model` and `model.changed` events).
    pub fn model(&self) -> String {
        self.shared.model.lock().unwrap().clone()
    }

    /// Runs a prompt to completion.
    pub fn send(&self, prompt: &str) -> Result<SendResult> {
        let v = self.inner.call(
            "session.send",
            json!({"sessionId": self.id, "prompt": prompt}),
            None,
        )?;
        Ok(serde_json::from_value(v)?)
    }

    /// Runs a prompt and yields its events as they happen; the iterator ends after the turn completes, then
    /// [`EventStream::result`] returns the outcome.
    pub fn stream(&self, prompt: &str) -> EventStream {
        let (tx, rx) = mpsc::channel();
        self.shared.watchers.lock().unwrap().push(tx.clone());
        let inner = Arc::clone(&self.inner);
        let params = json!({"sessionId": self.id, "prompt": prompt});
        let worker = thread::spawn(move || {
            let result = inner
                .call("session.send", params, None)
                .and_then(|v| Ok(serde_json::from_value(v)?));
            // Sent after every event of the turn: the server emits turn.completed before it answers.
            let _ = tx.send(StreamItem::Done(result));
        });
        EventStream {
            rx,
            worker: Some(worker),
            result: None,
            done: false,
        }
    }

    fn simple(&self, method: &str, extra: Value) -> Result<()> {
        let mut params = json!({"sessionId": self.id});
        if let (Value::Object(p), Value::Object(e)) = (&mut params, extra) {
            p.extend(e);
        }
        self.inner.call(method, params, None).map(|_| ())
    }

    /// Interrupts the running turn.
    pub fn abort(&self) -> Result<()> {
        self.simple("session.abort", json!({}))
    }

    /// Switches the model (any configured provider).
    pub fn set_model(&self, model: &str) -> Result<()> {
        self.simple("session.setModel", json!({"model": model}))?;
        *self.shared.model.lock().unwrap() = model.to_string();
        Ok(())
    }

    /// `default` | `acceptEdits` | `auto` | `plan` | `bypassPermissions`
    pub fn set_permission_mode(&self, mode: &str) -> Result<()> {
        self.simple("session.setMode", json!({"mode": mode}))
    }

    /// Summarizes the conversation to free context.
    pub fn compact(&self, instructions: Option<&str>) -> Result<()> {
        self.simple("session.compact", json!({"instructions": instructions}))
    }

    /// The raw transcript messages.
    pub fn messages(&self) -> Result<Vec<Value>> {
        let v = self
            .inner
            .call("session.messages", json!({"sessionId": self.id}), None)?;
        Ok(v["messages"].as_array().cloned().unwrap_or_default())
    }

    /// Ends the session on the server (an unchanged worktree is removed).
    pub fn close(self) -> Result<()> {
        let r = self.simple("session.close", json!({}));
        self.inner.sessions.lock().unwrap().remove(&self.id);
        r
    }
}

/// Events of one streamed turn (see [`Session::stream`]).
pub struct EventStream {
    rx: Receiver<StreamItem>,
    worker: Option<JoinHandle<()>>,
    result: Option<Result<SendResult>>,
    done: bool,
}

impl Iterator for EventStream {
    type Item = Event;

    fn next(&mut self) -> Option<Event> {
        if self.done {
            return None;
        }
        match self.rx.recv() {
            Ok(StreamItem::Event(e)) => Some(*e),
            Ok(StreamItem::Done(r)) => {
                self.result = Some(r);
                self.done = true;
                None
            }
            Err(_) => {
                self.done = true;
                self.result.get_or_insert(Err(Error::Closed));
                None
            }
        }
    }
}

impl EventStream {
    /// Consumes the remaining events and returns the turn's outcome.
    pub fn result(mut self) -> Result<SendResult> {
        while self.next().is_some() {}
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
        self.result.take().unwrap_or(Err(Error::Closed))
    }
}
