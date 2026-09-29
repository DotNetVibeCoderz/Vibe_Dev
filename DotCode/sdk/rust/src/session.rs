use crate::client::Inner;
use crate::config::SessionConfig;
use crate::event::{SessionEvent, SessionEventData};
use crate::tool::ToolInvocation;
use crate::types::{
    Attachment, Error, ExitPlanModeResult, Invocation, MessageOptions, PermissionDecision,
    PermissionMode, PermissionRequest, ReasoningEffort, Result, SendResult, SessionInfo, ToolInfo,
    UserQuestion,
};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

type Callback = Arc<dyn Fn(&SessionEvent) + Send + Sync>;

pub(crate) enum StreamItem {
    Event(Box<SessionEvent>),
    Done(Result<SendResult>),
}

/// Per-session state shared with the reader thread (callbacks and subscribers).
pub(crate) struct SessionShared {
    config: SessionConfig,
    session_id: Mutex<String>,
    model: Mutex<String>,
    next_sub: AtomicU64,
    handlers: Mutex<HashMap<u64, Callback>>,
    watchers: Mutex<Vec<Sender<StreamItem>>>,
}

impl SessionShared {
    pub(crate) fn new(config: SessionConfig, info: &SessionInfo) -> Self {
        SessionShared {
            config,
            session_id: Mutex::new(info.session_id.clone()),
            model: Mutex::new(info.model.clone()),
            next_sub: AtomicU64::new(0),
            handlers: Mutex::new(HashMap::new()),
            watchers: Mutex::new(Vec::new()),
        }
    }

    pub(crate) fn dispatch(&self, event: SessionEvent) {
        if let SessionEventData::ModelChanged { model } = &event.data {
            *self.model.lock().unwrap() = model.clone();
        }
        if let Some(f) = &self.config.on_event {
            f(&event);
        }
        let handlers: Vec<Callback> = self.handlers.lock().unwrap().values().cloned().collect();
        for h in handlers {
            h(&event);
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
        let invocation = Invocation {
            session_id: self.session_id.lock().unwrap().clone(),
        };
        match method {
            "permission.request" => {
                let request: PermissionRequest =
                    serde_json::from_value(params["request"].clone()).map_err(|e| e.to_string())?;
                let decision = match &self.config.permission_handler {
                    Some(h) => h.handle(&request, &invocation),
                    None => PermissionDecision::reject(Some(
                        "No permission handler registered in the SDK host (deny by default)."
                            .into(),
                    )),
                };
                Ok(decision.wire())
            }
            "user.question" => {
                let questions: Vec<UserQuestion> =
                    serde_json::from_value(params["questions"].clone()).unwrap_or_default();
                let answers = self
                    .config
                    .user_input_handler
                    .as_ref()
                    .map(|h| h.handle(&questions, &invocation))
                    .unwrap_or_default();
                Ok(json!({"answers": answers}))
            }
            "plan.review" => {
                let plan = params["plan"].as_str().unwrap_or("");
                Ok(
                    match self
                        .config
                        .exit_plan_mode_handler
                        .as_ref()
                        .map(|h| h.handle(plan, &invocation))
                    {
                        None | Some(ExitPlanModeResult::Approve) => json!({"approval": "approve"}),
                        Some(ExitPlanModeResult::ApproveAndAcceptEdits) => {
                            json!({"approval": "approve_accept_edits"})
                        }
                        Some(ExitPlanModeResult::Reject { feedback }) => {
                            json!({"approval": "reject", "feedback": feedback})
                        }
                    },
                )
            }
            "tool.call" => {
                let name = params["name"].as_str().unwrap_or("");
                let tool = self
                    .config
                    .tools
                    .iter()
                    .find(|t| t.name == name)
                    .ok_or_else(|| format!("unknown host tool {name}"))?;
                let handler = tool
                    .handler
                    .as_ref()
                    .ok_or_else(|| format!("tool {name} has no handler"))?;
                let call = ToolInvocation {
                    session_id: invocation.session_id,
                    tool_call_id: params["toolUseId"].as_str().unwrap_or("").to_string(),
                    tool_name: name.to_string(),
                    arguments: params["input"].clone(),
                };
                let handler = Arc::clone(handler);
                let result =
                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| handler.call(call)));
                Ok(match result {
                    Ok(Ok(r)) => r.wire(),
                    Ok(Err(e)) => json!({"content": format!("Error: {e}"), "isError": true}),
                    Err(_) => json!({"content": "Error: tool handler panicked", "isError": true}),
                })
            }
            other => Err(format!("unsupported callback {other}")),
        }
    }
}

/// One conversation with the agent. The API is synchronous (thread-based); from async code, call it inside
/// `spawn_blocking`.
pub struct Session {
    inner: Arc<Inner>,
    shared: Arc<SessionShared>,
    /// Session id (use it with `Client::resume_session`).
    pub session_id: String,
    pub info: SessionInfo,
}

impl Session {
    pub(crate) fn new(inner: Arc<Inner>, shared: Arc<SessionShared>, info: SessionInfo) -> Session {
        Session {
            inner,
            shared,
            session_id: info.session_id.clone(),
            info,
        }
    }

    /// Current model (updated by `set_model` and `model.changed` events).
    pub fn model(&self) -> String {
        self.shared.model.lock().unwrap().clone()
    }

    /// Calls `handler` for every event; returns a [`Subscription`] that unsubscribes when dropped (or
    /// [`Subscription::detach`]ed to keep it for the session's lifetime).
    pub fn on<F>(&self, handler: F) -> Subscription
    where
        F: Fn(&SessionEvent) + Send + Sync + 'static,
    {
        let id = self.shared.next_sub.fetch_add(1, Ordering::SeqCst);
        self.shared
            .handlers
            .lock()
            .unwrap()
            .insert(id, Arc::new(handler));
        Subscription {
            shared: Arc::downgrade(&self.shared),
            id: Some(id),
        }
    }

    /// A channel receiving every event from now on.
    pub fn subscribe(&self) -> Receiver<SessionEvent> {
        let (tx, rx) = mpsc::channel();
        self.on(move |e| {
            let _ = tx.send(e.clone());
        })
        .detach();
        rx
    }

    fn send_params(&self, message: MessageOptions) -> Value {
        let attachments: Vec<Value> = message
            .attachments
            .iter()
            .map(|a| match a {
                Attachment::File { path } => json!({"type": "file", "path": path}),
                Attachment::Image { data, media_type } => {
                    json!({"type": "image", "data": data, "mediaType": media_type})
                }
            })
            .collect();
        let mut params = json!({"sessionId": self.session_id, "prompt": message.prompt});
        if !attachments.is_empty() {
            params["attachments"] = Value::Array(attachments);
        }
        params
    }

    /// Starts a turn and returns once it is dispatched; follow it with [`Session::on`] (a `TurnCompleted` event
    /// ends it). Failures are delivered as an `Error` event.
    pub fn send(&self, message: impl Into<MessageOptions>) -> Result<()> {
        let params = self.send_params(message.into());
        let inner = Arc::clone(&self.inner);
        let shared = Arc::clone(&self.shared);
        let session_id = self.session_id.clone();
        thread::Builder::new()
            .name("dotcode-sdk-send".into())
            .spawn(move || {
                if let Err(e) = inner.call("session.send", params, None) {
                    shared.dispatch(SessionEvent {
                        session_id: Some(session_id),
                        parent_tool_use_id: None,
                        data: SessionEventData::Error {
                            code: "send_failed".into(),
                            message: e.to_string(),
                            retryable: false,
                        },
                    });
                }
            })?;
        Ok(())
    }

    /// Runs a turn to completion and returns its result.
    pub fn send_and_wait(&self, message: impl Into<MessageOptions>) -> Result<SendResult> {
        let v = self
            .inner
            .call("session.send", self.send_params(message.into()), None)?;
        Ok(serde_json::from_value(v)?)
    }

    /// Like [`Session::send_and_wait`]; when the turn takes longer than `timeout` it is aborted and
    /// [`Error::Timeout`] is returned.
    pub fn send_and_wait_timeout(
        &self,
        message: impl Into<MessageOptions>,
        timeout: Duration,
    ) -> Result<SendResult> {
        match self.inner.call(
            "session.send",
            self.send_params(message.into()),
            Some(timeout),
        ) {
            Ok(v) => Ok(serde_json::from_value(v)?),
            Err(Error::Timeout) => {
                let _ = self.abort();
                Err(Error::Timeout)
            }
            Err(e) => Err(e),
        }
    }

    /// Runs a turn and yields its events as they happen; the iterator ends after the turn completes, then
    /// [`EventStream::result`] returns the outcome.
    pub fn stream(&self, message: impl Into<MessageOptions>) -> EventStream {
        let (tx, rx) = mpsc::channel();
        self.shared.watchers.lock().unwrap().push(tx.clone());
        let inner = Arc::clone(&self.inner);
        let params = self.send_params(message.into());
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

    fn call(&self, method: &str, extra: Value) -> Result<Value> {
        let mut params = json!({"sessionId": self.session_id});
        if let (Value::Object(p), Value::Object(e)) = (&mut params, extra) {
            p.extend(e);
        }
        self.inner.call(method, params, None)
    }

    /// Cancels the running turn.
    pub fn abort(&self) -> Result<()> {
        self.call("session.abort", json!({})).map(|_| ())
    }

    /// Switches the model (any configured provider).
    pub fn set_model(&self, model: &str) -> Result<()> {
        self.call("session.setModel", json!({"model": model}))?;
        *self.shared.model.lock().unwrap() = model.to_string();
        Ok(())
    }

    pub fn set_permission_mode(&self, mode: PermissionMode) -> Result<()> {
        self.call("session.setMode", json!({"mode": mode}))
            .map(|_| ())
    }

    pub fn set_reasoning_effort(&self, effort: ReasoningEffort) -> Result<()> {
        self.call("session.setEffort", json!({"effort": effort}))
            .map(|_| ())
    }

    /// Summarizes the conversation to free context.
    pub fn compact(&self, instructions: Option<&str>) -> Result<()> {
        self.call("session.compact", json!({"instructions": instructions}))
            .map(|_| ())
    }

    /// Clears the conversation.
    pub fn clear(&self) -> Result<()> {
        self.call("session.clear", json!({})).map(|_| ())
    }

    /// The conversation so far (provider-neutral messages).
    pub fn get_messages(&self) -> Result<Vec<Value>> {
        Ok(self.call("session.messages", json!({}))?["messages"]
            .as_array()
            .cloned()
            .unwrap_or_default())
    }

    /// Tools available to the model (built-in, MCP and yours).
    pub fn list_tools(&self) -> Result<Vec<ToolInfo>> {
        Ok(
            serde_json::from_value(self.call("tools.list", json!({}))?["tools"].clone())
                .unwrap_or_default(),
        )
    }

    /// Closes the session; the transcript stays on disk for `Client::resume_session` (an unchanged worktree is
    /// removed).
    pub fn disconnect(self) -> Result<()> {
        let r = self.call("session.close", json!({})).map(|_| ());
        self.inner.sessions.lock().unwrap().remove(&self.session_id);
        r
    }
}

/// Unsubscribes its handler when dropped.
pub struct Subscription {
    shared: std::sync::Weak<SessionShared>,
    id: Option<u64>,
}

impl Subscription {
    /// Keeps the handler for the session's lifetime.
    pub fn detach(mut self) {
        self.id = None;
    }
}

impl Drop for Subscription {
    fn drop(&mut self) {
        if let (Some(id), Some(shared)) = (self.id, self.shared.upgrade()) {
            shared.handlers.lock().unwrap().remove(&id);
        }
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
    type Item = SessionEvent;

    fn next(&mut self) -> Option<SessionEvent> {
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
