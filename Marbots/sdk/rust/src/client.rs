use std::io::{BufRead, BufReader};
use std::time::Duration;

use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::{json, Value};
use ureq::Agent;

use crate::types::*;

/// Errors returned by the SDK.
#[derive(Debug)]
pub enum Error {
    /// The server answered with an error status (message from Problem Details).
    Api { status: u16, message: String },
    /// Transport or decoding failure.
    Transport(String),
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Error::Api { status, message } => write!(f, "HTTP {status}: {message}"),
            Error::Transport(m) => write!(f, "transport error: {m}"),
        }
    }
}

impl std::error::Error for Error {}

impl Error {
    /// The HTTP status for API errors.
    pub fn status(&self) -> Option<u16> {
        match self {
            Error::Api { status, .. } => Some(*status),
            Error::Transport(_) => None,
        }
    }
}

impl From<ureq::Error> for Error {
    fn from(e: ureq::Error) -> Self {
        Error::Transport(e.to_string())
    }
}

impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::Transport(e.to_string())
    }
}

/// SDK result type.
pub type Result<T> = std::result::Result<T, Error>;

fn esc(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

/// A synchronous client for a Marbots server. Cheap to clone; safe to share between threads.
#[derive(Clone)]
pub struct Client {
    base: String,
    api_key: Option<String>,
    agent: Agent,
}

impl Client {
    /// A client for `base_url` (e.g. `http://localhost:5170`).
    pub fn new(base_url: impl Into<String>) -> Self {
        let agent: Agent = Agent::config_builder()
            .http_status_as_error(false)
            .timeout_global(Some(Duration::from_secs(30 * 60)))
            .build()
            .into();
        Self {
            base: base_url.into().trim_end_matches('/').to_string(),
            api_key: None,
            agent,
        }
    }

    /// Sends `X-Api-Key` (needed when the server sets Marbots:ApiKey).
    pub fn with_api_key(mut self, key: impl Into<String>) -> Self {
        self.api_key = Some(key.into());
        self
    }

    pub fn base_url(&self) -> &str {
        &self.base
    }

    fn finish(resp: ureq::http::Response<ureq::Body>) -> Result<Vec<u8>> {
        let status = resp.status().as_u16();
        let bytes = resp.into_body().read_to_vec()?;
        if status >= 300 {
            let text = String::from_utf8_lossy(&bytes).to_string();
            let message = serde_json::from_str::<Value>(&text)
                .ok()
                .and_then(|v| {
                    v.get("detail")
                        .or_else(|| v.get("title"))
                        .and_then(Value::as_str)
                        .map(str::to_string)
                })
                .unwrap_or(text);
            return Err(Error::Api { status, message });
        }
        Ok(bytes)
    }

    fn decode<T: DeserializeOwned>(bytes: &[u8]) -> Result<T> {
        Ok(serde_json::from_slice(if bytes.is_empty() {
            b"null"
        } else {
            bytes
        })?)
    }

    pub(crate) fn get<T: DeserializeOwned>(&self, path: &str) -> Result<T> {
        Self::decode(&self.get_raw(path)?)
    }

    pub(crate) fn get_raw(&self, path: &str) -> Result<Vec<u8>> {
        let mut req = self.agent.get(format!("{}{}", self.base, path));
        if let Some(k) = &self.api_key {
            req = req.header("X-Api-Key", k);
        }
        Self::finish(req.call()?)
    }

    pub(crate) fn send<B: Serialize, T: DeserializeOwned>(
        &self,
        method: &str,
        path: &str,
        body: Option<&B>,
    ) -> Result<T> {
        let url = format!("{}{}", self.base, path);
        let resp = match method {
            "DELETE" => {
                let mut req = self.agent.delete(url);
                if let Some(k) = &self.api_key {
                    req = req.header("X-Api-Key", k);
                }
                req.call()?
            }
            _ => {
                let mut req = match method {
                    "PUT" => self.agent.put(url),
                    _ => self.agent.post(url),
                };
                if let Some(k) = &self.api_key {
                    req = req.header("X-Api-Key", k);
                }
                match body {
                    Some(b) => req.send_json(b)?,
                    None => req
                        .header("Content-Type", "application/json")
                        .send(&b""[..])?,
                }
            }
        };
        Self::decode(&Self::finish(resp)?)
    }

    pub(crate) fn post_bytes(
        &self,
        path: &str,
        body: &[u8],
        content_type: &str,
    ) -> Result<Vec<u8>> {
        let mut req = self
            .agent
            .post(format!("{}{}", self.base, path))
            .header("Content-Type", content_type);
        if let Some(k) = &self.api_key {
            req = req.header("X-Api-Key", k);
        }
        Self::finish(req.send(body)?)
    }

    /// Server information.
    pub fn system(&self) -> Result<SystemInfo> {
        self.get("/api/v1/system")
    }

    /// Machines that run bots.
    pub fn hosts(&self) -> Result<Vec<HostInfo>> {
        self.get("/api/v1/hosts")
    }

    /// Creates a thread with `bot`, sends `text`, waits, and returns the reply text.
    pub fn chat(&self, bot: &str, text: &str) -> Result<String> {
        let thread = self.threads().create(bot, None)?;
        Ok(self
            .threads()
            .send(&thread.id, text, true)?
            .text()
            .to_string())
    }

    pub fn bots(&self) -> Bots<'_> {
        Bots(self)
    }
    pub fn templates(&self) -> Templates<'_> {
        Templates(self)
    }
    pub fn models(&self) -> Models<'_> {
        Models(self)
    }
    pub fn threads(&self) -> Threads<'_> {
        Threads(self)
    }
    pub fn tasks(&self) -> Tasks<'_> {
        Tasks(self)
    }
    pub fn approvals(&self) -> Approvals<'_> {
        Approvals(self)
    }
    pub fn skills(&self) -> Skills<'_> {
        Skills(self)
    }
    pub fn mcp(&self) -> Mcp<'_> {
        Mcp(self)
    }
    pub fn schedules(&self) -> Schedules<'_> {
        Schedules(self)
    }
    pub fn memory(&self) -> Memory<'_> {
        Memory(self)
    }
    /// Computers that run bots' tools.
    pub fn agent_hosts(&self) -> AgentHosts<'_> {
        AgentHosts(self)
    }
    pub fn events(&self) -> Events<'_> {
        Events(self)
    }
}

const NONE: Option<&Value> = None;

/// Bot management.
pub struct Bots<'a>(&'a Client);

impl Bots<'_> {
    pub fn list(&self) -> Result<Vec<Bot>> {
        self.0.get("/api/v1/bots")
    }
    pub fn get(&self, id_or_name: &str) -> Result<Bot> {
        self.0.get(&format!("/api/v1/bots/{}", esc(id_or_name)))
    }
    pub fn create(&self, spec: BotSpec) -> Result<Bot> {
        self.0.send("POST", "/api/v1/bots", Some(&spec))
    }
    pub fn update(&self, id: &str, spec: BotSpec) -> Result<Bot> {
        self.0.send(
            "PUT",
            &format!("/api/v1/bots/{}", esc(id)),
            Some(&spec.with_id(id)),
        )
    }
    pub fn delete(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("DELETE", &format!("/api/v1/bots/{}", esc(id)), NONE)
            .map(|_| ())
    }
    /// Creates a bot from a gallery template.
    pub fn hire(&self, template_id: &str, name: Option<&str>) -> Result<Bot> {
        self.0.send(
            "POST",
            &format!("/api/v1/bots/from-template/{}", esc(template_id)),
            Some(&json!({ "name": name })),
        )
    }
    pub fn get_model(&self, id: &str) -> Result<BotModelInfo> {
        self.0.get(&format!("/api/v1/bots/{}/model", esc(id)))
    }
    /// Sets the bot's model: [`ModelRef::DEFAULT`], [`ModelRef::of`] or a profile name.
    pub fn set_model(&self, id: &str, model: &str) -> Result<BotModelInfo> {
        self.0.send(
            "PUT",
            &format!("/api/v1/bots/{}/model", esc(id)),
            Some(&json!({ "model": model })),
        )
    }
    pub fn pause(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("POST", &format!("/api/v1/bots/{}/pause", esc(id)), NONE)
            .map(|_| ())
    }
    pub fn resume(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("POST", &format!("/api/v1/bots/{}/resume", esc(id)), NONE)
            .map(|_| ())
    }
    /// Downloads a .marbot package (secrets are never included).
    pub fn export(&self, id: &str, include_memory: bool) -> Result<Vec<u8>> {
        self.0.get_raw(&format!(
            "/api/v1/bots/{}/export?includeMemory={include_memory}",
            esc(id)
        ))
    }
    pub fn import_package(&self, package: &[u8]) -> Result<Bot> {
        Client::decode(
            &self
                .0
                .post_bytes("/api/v1/bots/import", package, "application/zip")?,
        )
    }
}

/// Template gallery.
pub struct Templates<'a>(&'a Client);

impl Templates<'_> {
    pub fn list(&self, query: &str, category: &str) -> Result<Vec<BotTemplate>> {
        self.0.get(&format!(
            "/api/v1/templates?q={}&category={}",
            esc(query),
            esc(category)
        ))
    }
    pub fn get(&self, id: &str) -> Result<BotTemplate> {
        self.0.get(&format!("/api/v1/templates/{}", esc(id)))
    }
}

/// Workspace default model and model choices.
pub struct Models<'a>(&'a Client);

impl Models<'_> {
    pub fn list(&self) -> Result<ModelCatalog> {
        self.0.get("/api/v1/models")
    }
    /// Changes the model used by every bot whose model is [`ModelRef::DEFAULT`]; returns the new default.
    pub fn set_default(&self, model: &str) -> Result<String> {
        let v: Value = self.0.send(
            "PUT",
            "/api/v1/models/default",
            Some(&json!({ "model": model })),
        )?;
        Ok(v.get("default")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string())
    }
}

/// Conversations.
pub struct Threads<'a>(&'a Client);

impl Threads<'_> {
    pub fn list(&self, bot_id: Option<&str>) -> Result<Vec<ChatThread>> {
        match bot_id {
            Some(b) => self.0.get(&format!("/api/v1/threads?botId={}", esc(b))),
            None => self.0.get("/api/v1/threads"),
        }
    }
    pub fn create(&self, bot_id: &str, title: Option<&str>) -> Result<ChatThread> {
        self.0.send(
            "POST",
            "/api/v1/threads",
            Some(&json!({ "botId": bot_id, "title": title })),
        )
    }
    /// Sends a message; with `wait` the call returns after the bot finished (10 minute limit).
    pub fn send(&self, thread_id: &str, text: &str, wait: bool) -> Result<SendResult> {
        self.send_with_timeout(thread_id, text, wait, 600)
    }
    pub fn send_with_timeout(
        &self,
        thread_id: &str,
        text: &str,
        wait: bool,
        timeout_seconds: u32,
    ) -> Result<SendResult> {
        let body = json!({ "text": text, "wait": wait, "timeoutSeconds": timeout_seconds });
        self.0.send(
            "POST",
            &format!("/api/v1/threads/{}/messages", esc(thread_id)),
            Some(&body),
        )
    }
    pub fn messages(&self, thread_id: &str) -> Result<Vec<ChatMessage>> {
        self.0
            .get(&format!("/api/v1/threads/{}/messages", esc(thread_id)))
    }
    pub fn files(&self, thread_id: &str) -> Result<Vec<WorkspaceFile>> {
        self.0
            .get(&format!("/api/v1/threads/{}/files", esc(thread_id)))
    }
    pub fn download(&self, thread_id: &str, path: &str) -> Result<Vec<u8>> {
        self.0.get_raw(&format!(
            "/api/v1/threads/{}/files/{}",
            esc(thread_id),
            path
        ))
    }
    pub fn delete(&self, thread_id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>(
                "DELETE",
                &format!("/api/v1/threads/{}", esc(thread_id)),
                NONE,
            )
            .map(|_| ())
    }
}

/// Tasks.
pub struct Tasks<'a>(&'a Client);

impl Tasks<'_> {
    pub fn list(&self, thread_id: Option<&str>) -> Result<Vec<TaskRecord>> {
        match thread_id {
            Some(t) => self.0.get(&format!("/api/v1/tasks?threadId={}", esc(t))),
            None => self.0.get("/api/v1/tasks"),
        }
    }
    pub fn get(&self, id: &str) -> Result<TaskRecord> {
        self.0.get(&format!("/api/v1/tasks/{}", esc(id)))
    }
    pub fn cancel(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("POST", &format!("/api/v1/tasks/{}/cancel", esc(id)), NONE)
            .map(|_| ())
    }
}

/// Human-in-the-loop approvals.
pub struct Approvals<'a>(&'a Client);

impl Approvals<'_> {
    pub fn pending(&self) -> Result<Vec<ApprovalRequest>> {
        self.0.get("/api/v1/approvals?state=pending")
    }
    pub fn approve(&self, id: &str, scope: ApprovalScope) -> Result<ApprovalRequest> {
        self.0.send(
            "POST",
            &format!("/api/v1/approvals/{}/approve", esc(id)),
            Some(&json!({ "scope": scope })),
        )
    }
    pub fn reject(&self, id: &str) -> Result<ApprovalRequest> {
        self.0.send::<Value, _>(
            "POST",
            &format!("/api/v1/approvals/{}/reject", esc(id)),
            NONE,
        )
    }
    /// True when approvals are skipped (dangerous mode).
    pub fn skip_approvals(&self) -> Result<bool> {
        let v: Value = self.0.get("/api/v1/system/approvals")?;
        Ok(v.get("dangerouslySkipApprovals")
            .and_then(Value::as_bool)
            .unwrap_or(false))
    }
    /// Dangerous, like `--dangerously-skip-permissions`: every action that would ask runs without a human.
    /// Turning it on also approves everything pending. Actions a bot's profile denies stay denied.
    pub fn set_skip_approvals(&self, skip: bool) -> Result<bool> {
        let v: Value = self.0.send(
            "PUT",
            "/api/v1/system/approvals",
            Some(&json!({ "dangerouslySkipApprovals": skip })),
        )?;
        Ok(v.get("dangerouslySkipApprovals")
            .and_then(Value::as_bool)
            .unwrap_or(false))
    }
}

/// SKILL.md packages.
pub struct Skills<'a>(&'a Client);

impl Skills<'_> {
    pub fn list(&self) -> Result<Vec<SkillInfo>> {
        self.0.get("/api/v1/skills")
    }
    pub fn install(&self, source: &str) -> Result<Vec<SkillInfo>> {
        self.0.send(
            "POST",
            "/api/v1/skills/install",
            Some(&json!({ "source": source })),
        )
    }
    /// Learning evaluation: outcomes per skill version and a verdict.
    pub fn evaluations(&self) -> Result<Vec<SkillEvaluation>> {
        self.0.get("/api/v1/skills/evaluations")
    }
    /// Restores the previous version of an installed skill; returns the restored version.
    pub fn rollback(&self, name: &str) -> Result<String> {
        let v: Value = self.0.send(
            "POST",
            &format!("/api/v1/skills/{}/rollback", esc(name)),
            NONE,
        )?;
        Ok(v.get("version")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string())
    }
    /// Publishes a skill drafted by auto-learn.
    pub fn promote(&self, name: &str) -> Result<()> {
        self.0
            .send::<Value, Value>(
                "POST",
                &format!("/api/v1/skills/{}/approve", esc(name)),
                NONE,
            )
            .map(|_| ())
    }
    pub fn discard(&self, name: &str) -> Result<()> {
        self.0
            .send::<Value, Value>(
                "POST",
                &format!("/api/v1/skills/{}/reject", esc(name)),
                NONE,
            )
            .map(|_| ())
    }
    pub fn auto_rollback(&self) -> Result<bool> {
        let v: Value = self.0.get("/api/v1/system/learning")?;
        Ok(v.get("autoRollbackSkills")
            .and_then(Value::as_bool)
            .unwrap_or(false))
    }
    pub fn set_auto_rollback(&self, on: bool) -> Result<bool> {
        let v: Value = self.0.send(
            "PUT",
            "/api/v1/system/learning",
            Some(&json!({ "autoRollbackSkills": on })),
        )?;
        Ok(v.get("autoRollbackSkills")
            .and_then(Value::as_bool)
            .unwrap_or(false))
    }
}

/// Computers that run bots' tools (see docs/en/computers.md).
pub struct AgentHosts<'a>(&'a Client);

impl AgentHosts<'_> {
    pub fn list(&self) -> Result<Vec<HostInfo>> {
        self.0.get("/api/v1/hosts")
    }
    /// One-time token for `marbots-host enroll`.
    pub fn create_enrollment(&self, name: &str, valid_minutes: u32) -> Result<EnrollmentToken> {
        self.0.send(
            "POST",
            "/api/v1/hosts/enrollments",
            Some(&json!({ "name": name, "validMinutes": valid_minutes })),
        )
    }
    /// Installs marbots-host over SSH; the password/key is used for this call only.
    pub fn bootstrap(&self, options: &BootstrapOptions) -> Result<BootstrapResult> {
        self.0
            .send("POST", "/api/v1/hosts/bootstrap", Some(options))
    }
    pub fn disable(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("POST", &format!("/api/v1/hosts/{}/disable", esc(id)), NONE)
            .map(|_| ())
    }
    pub fn enable(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("POST", &format!("/api/v1/hosts/{}/enable", esc(id)), NONE)
            .map(|_| ())
    }
    pub fn remove(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("DELETE", &format!("/api/v1/hosts/{}", esc(id)), NONE)
            .map(|_| ())
    }
}

/// MCP servers.
pub struct Mcp<'a>(&'a Client);

impl Mcp<'_> {
    pub fn list(&self) -> Result<Vec<McpServer>> {
        self.0.get("/api/v1/mcp")
    }
    pub fn install(&self, id: &str) -> Result<McpServer> {
        self.0
            .send::<Value, _>("POST", &format!("/api/v1/mcp/{}/install", esc(id)), NONE)
    }
}

/// Cron and one-off jobs.
pub struct Schedules<'a>(&'a Client);

impl Schedules<'_> {
    pub fn list(&self) -> Result<Vec<ScheduleJob>> {
        self.0.get("/api/v1/schedules")
    }
    pub fn create(&self, spec: &ScheduleSpec) -> Result<ScheduleJob> {
        self.0.send("POST", "/api/v1/schedules", Some(spec))
    }
    pub fn delete(&self, id: &str) -> Result<()> {
        self.0
            .send::<Value, Value>("DELETE", &format!("/api/v1/schedules/{}", esc(id)), NONE)
            .map(|_| ())
    }
}

/// Long-term memory.
pub struct Memory<'a>(&'a Client);

impl Memory<'_> {
    pub fn list(&self, owner: &str) -> Result<Vec<MemoryRecord>> {
        self.0.get(&format!("/api/v1/memory/{}", esc(owner)))
    }
    pub fn remember(&self, owner: &str, content: &str, kind: MemoryKind) -> Result<MemoryRecord> {
        let body = json!({ "owner": owner, "content": content, "kind": kind, "source": "sdk:rust", "confidence": 1.0 });
        self.0.send("POST", "/api/v1/memory", Some(&body))
    }
}

/// Live events (Server-Sent Events).
pub struct Events<'a>(&'a Client);

impl Events<'_> {
    /// Opens the event stream (one thread, or everything when `thread_id` is `None`). Iterate it on a background
    /// thread if you need to keep working; dropping the iterator closes the connection.
    pub fn stream(&self, thread_id: Option<&str>) -> Result<EventStream> {
        let path = match thread_id {
            Some(t) => format!("/api/v1/threads/{}/events", esc(t)),
            None => "/api/v1/events".to_string(),
        };
        let agent: Agent = Agent::config_builder()
            .http_status_as_error(false)
            .build()
            .into();
        let mut req = agent
            .get(format!("{}{}", self.0.base, path))
            .header("Accept", "text/event-stream");
        if let Some(k) = &self.0.api_key {
            req = req.header("X-Api-Key", k);
        }
        let resp = req.call()?;
        if resp.status().as_u16() >= 300 {
            return Err(Error::Api {
                status: resp.status().as_u16(),
                message: "event stream unavailable".into(),
            });
        }
        Ok(EventStream {
            lines: Box::new(BufReader::new(resp.into_body().into_reader())),
        })
    }
}

/// Iterator over live events.
pub struct EventStream {
    lines: Box<dyn BufRead + Send>,
}

impl Iterator for EventStream {
    type Item = Result<AgentEvent>;

    fn next(&mut self) -> Option<Self::Item> {
        let mut line = String::new();
        loop {
            line.clear();
            match self.lines.read_line(&mut line) {
                Ok(0) => return None,
                Ok(_) => {
                    if let Some(data) = line.trim_end().strip_prefix("data: ") {
                        return Some(serde_json::from_str(data).map_err(Error::from));
                    }
                }
                Err(e) => return Some(Err(Error::Transport(e.to_string()))),
            }
        }
    }
}
