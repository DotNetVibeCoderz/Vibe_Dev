use crate::event::SessionEvent;
use crate::handler::{
    ApproveAllHandler, DenyAllHandler, ExitPlanModeHandler, PermissionHandler, UserInputHandler,
};
use crate::tool::Tool;
use crate::types::{
    BuiltinTool, ExitPlanModeResult, Invocation, McpServerConfig, PermissionDecision,
    PermissionMode, PermissionRequest, ProviderConfig, ReasoningEffort, SystemMessageConfig,
    UserQuestion, UserQuestionAnswer,
};
use serde_json::{json, Map, Value};
use std::collections::BTreeMap;
use std::sync::Arc;

pub(crate) type EventCallback = Arc<dyn Fn(&SessionEvent) + Send + Sync>;

/// Session configuration; unset fields fall back to the user's DotCode settings.
///
/// ```no_run
/// use dotcode_sdk::{BuiltinTool, PermissionMode, SessionConfig};
/// let config = SessionConfig::default()
///     .with_model("openai:gpt-5")
///     .with_permission_mode(PermissionMode::AcceptEdits)
///     .with_allowed_tools([BuiltinTool::Bash.rule("npm test:*")])
///     .approve_all_permissions();
/// ```
#[derive(Clone, Default)]
pub struct SessionConfig {
    /// `provider:model`, an alias or a role, e.g. `anthropic:claude-sonnet-4-5`, `ollama:qwen3-coder`.
    pub model: Option<String>,
    pub fallback_model: Option<String>,
    /// Working directory of the session (default: the client's `cwd`).
    pub working_directory: Option<String>,
    pub permission_mode: Option<PermissionMode>,
    pub reasoning_effort: Option<ReasoningEffort>,
    pub system_message: Option<SystemMessageConfig>,
    /// Custom tools implemented by your application.
    pub tools: Vec<Tool>,
    /// Restricts the built-in tools.
    pub available_tools: Option<Vec<BuiltinTool>>,
    /// Permission rules to pre-approve, e.g. `BuiltinTool::Bash.rule("npm test:*")`.
    pub allowed_tools: Vec<String>,
    /// Tools to remove / rules to deny.
    pub excluded_tools: Vec<String>,
    pub mcp_servers: BTreeMap<String, McpServerConfig>,
    /// Skip MCP servers from settings files.
    pub disable_mcp: bool,
    /// Named providers (bring your own key), referenced as `"<name>:<model>"`.
    pub providers: BTreeMap<String, ProviderConfig>,
    /// Advanced: raw settings merged over the settings files (prefer the typed fields).
    pub settings: Option<Value>,
    pub max_turns: Option<u32>,
    /// Save the transcript for `Client::resume_session` (default true).
    pub persist_session: Option<bool>,
    /// Run in a fresh git worktree (`Some("")`) or a named one.
    pub worktree: Option<String>,
    pub(crate) permission_handler: Option<Arc<dyn PermissionHandler>>,
    pub(crate) user_input_handler: Option<Arc<dyn UserInputHandler>>,
    pub(crate) exit_plan_mode_handler: Option<Arc<dyn ExitPlanModeHandler>>,
    pub(crate) on_event: Option<EventCallback>,
}

impl SessionConfig {
    pub fn with_model(mut self, model: impl Into<String>) -> Self {
        self.model = Some(model.into());
        self
    }
    pub fn with_fallback_model(mut self, model: impl Into<String>) -> Self {
        self.fallback_model = Some(model.into());
        self
    }
    pub fn with_working_directory(mut self, dir: impl Into<String>) -> Self {
        self.working_directory = Some(dir.into());
        self
    }
    pub fn with_permission_mode(mut self, mode: PermissionMode) -> Self {
        self.permission_mode = Some(mode);
        self
    }
    pub fn with_reasoning_effort(mut self, effort: ReasoningEffort) -> Self {
        self.reasoning_effort = Some(effort);
        self
    }
    pub fn with_system_message(mut self, message: SystemMessageConfig) -> Self {
        self.system_message = Some(message);
        self
    }
    pub fn with_tools(mut self, tools: impl IntoIterator<Item = Tool>) -> Self {
        self.tools.extend(tools);
        self
    }
    pub fn with_available_tools(mut self, tools: impl IntoIterator<Item = BuiltinTool>) -> Self {
        self.available_tools = Some(tools.into_iter().collect());
        self
    }
    pub fn with_allowed_tools(
        mut self,
        rules: impl IntoIterator<Item = impl Into<String>>,
    ) -> Self {
        self.allowed_tools.extend(rules.into_iter().map(Into::into));
        self
    }
    pub fn with_excluded_tools(
        mut self,
        rules: impl IntoIterator<Item = impl Into<String>>,
    ) -> Self {
        self.excluded_tools
            .extend(rules.into_iter().map(Into::into));
        self
    }
    pub fn with_mcp_server(mut self, name: impl Into<String>, server: McpServerConfig) -> Self {
        self.mcp_servers.insert(name.into(), server);
        self
    }
    pub fn with_disable_mcp(mut self, disable: bool) -> Self {
        self.disable_mcp = disable;
        self
    }
    pub fn with_provider(mut self, name: impl Into<String>, provider: ProviderConfig) -> Self {
        self.providers.insert(name.into(), provider);
        self
    }
    pub fn with_settings(mut self, settings: Value) -> Self {
        self.settings = Some(settings);
        self
    }
    pub fn with_max_turns(mut self, turns: u32) -> Self {
        self.max_turns = Some(turns);
        self
    }
    pub fn with_persist_session(mut self, persist: bool) -> Self {
        self.persist_session = Some(persist);
        self
    }
    /// Runs in a git worktree: `""` for a fresh one, or a name to create/reuse.
    pub fn with_worktree(mut self, name: impl Into<String>) -> Self {
        self.worktree = Some(name.into());
        self
    }

    /// Installs a permission handler (a type implementing [`PermissionHandler`]).
    pub fn with_permission_handler(mut self, handler: impl PermissionHandler + 'static) -> Self {
        self.permission_handler = Some(Arc::new(handler));
        self
    }
    /// Decides permission requests with a closure.
    pub fn on_permission_request<F>(self, f: F) -> Self
    where
        F: Fn(&PermissionRequest, &Invocation) -> PermissionDecision + Send + Sync + 'static,
    {
        self.with_permission_handler(f)
    }
    /// Approves every permission request.
    pub fn approve_all_permissions(self) -> Self {
        self.with_permission_handler(ApproveAllHandler)
    }
    /// Rejects every permission request (the default without a handler).
    pub fn deny_all_permissions(self) -> Self {
        self.with_permission_handler(DenyAllHandler)
    }
    /// Approves requests matching `predicate`, rejects the rest.
    pub fn approve_permissions_if<F>(self, predicate: F) -> Self
    where
        F: Fn(&PermissionRequest) -> bool + Send + Sync + 'static,
    {
        self.on_permission_request(move |req, _| {
            if predicate(req) {
                PermissionDecision::approve_once()
            } else {
                PermissionDecision::reject(None)
            }
        })
    }
    pub fn with_user_input_handler(mut self, handler: impl UserInputHandler + 'static) -> Self {
        self.user_input_handler = Some(Arc::new(handler));
        self
    }
    /// Answers AskUserQuestion with a closure.
    pub fn on_user_input_request<F>(self, f: F) -> Self
    where
        F: Fn(&[UserQuestion], &Invocation) -> Vec<UserQuestionAnswer> + Send + Sync + 'static,
    {
        self.with_user_input_handler(f)
    }
    pub fn with_exit_plan_mode_handler(
        mut self,
        handler: impl ExitPlanModeHandler + 'static,
    ) -> Self {
        self.exit_plan_mode_handler = Some(Arc::new(handler));
        self
    }
    /// Reviews plans with a closure.
    pub fn on_exit_plan_mode<F>(self, f: F) -> Self
    where
        F: Fn(&str, &Invocation) -> ExitPlanModeResult + Send + Sync + 'static,
    {
        self.with_exit_plan_mode_handler(f)
    }
    /// Receives every event, including those emitted while the session is created.
    pub fn on_event<F>(mut self, f: F) -> Self
    where
        F: Fn(&SessionEvent) + Send + Sync + 'static,
    {
        self.on_event = Some(Arc::new(f));
        self
    }

    pub(crate) fn wire(&self, default_cwd: Option<&str>) -> Value {
        let mut m = Map::new();
        let mut set = |k: &str, v: Option<String>| {
            if let Some(v) = v.filter(|v| !v.is_empty()) {
                m.insert(k.into(), Value::String(v));
            }
        };
        set(
            "cwd",
            self.working_directory
                .clone()
                .or_else(|| default_cwd.map(str::to_string)),
        );
        set("model", self.model.clone());
        set("fallbackModel", self.fallback_model.clone());
        match &self.system_message {
            Some(SystemMessageConfig::Replace(s)) => set("systemPrompt", Some(s.clone())),
            Some(SystemMessageConfig::Append(s)) => set("appendSystemPrompt", Some(s.clone())),
            None => {}
        }
        if let Some(mode) = self.permission_mode {
            m.insert("permissionMode".into(), json!(mode));
        }
        if let Some(effort) = self.reasoning_effort {
            m.insert("effort".into(), json!(effort));
        }
        if !self.allowed_tools.is_empty() {
            m.insert("allowedTools".into(), json!(self.allowed_tools));
        }
        if !self.excluded_tools.is_empty() {
            m.insert("disallowedTools".into(), json!(self.excluded_tools));
        }
        if let Some(tools) = &self.available_tools {
            m.insert("tools".into(), json!(tools));
        }
        if !self.mcp_servers.is_empty() {
            let servers: Map<String, Value> = self
                .mcp_servers
                .iter()
                .map(|(k, v)| (k.clone(), v.wire()))
                .collect();
            m.insert("mcpServers".into(), Value::Object(servers));
        }
        let mut settings = match &self.settings {
            Some(Value::Object(s)) => s.clone(),
            _ => Map::new(),
        };
        if !self.providers.is_empty() {
            let mut providers = match settings.remove("providers") {
                Some(Value::Object(p)) => p,
                _ => Map::new(),
            };
            for (name, p) in &self.providers {
                providers.insert(name.clone(), serde_json::to_value(p).unwrap_or(Value::Null));
            }
            settings.insert("providers".into(), Value::Object(providers));
        }
        if !settings.is_empty() {
            m.insert("settings".into(), Value::Object(settings));
        }
        if let Some(t) = self.max_turns {
            m.insert("maxTurns".into(), json!(t));
        }
        if let Some(p) = self.persist_session {
            m.insert("persistSession".into(), json!(p));
        }
        if self.disable_mcp {
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
            m.insert(
                "hostTools".into(),
                Value::Array(self.tools.iter().map(Tool::wire).collect()),
            );
        }
        Value::Object(m)
    }
}
