//! Rust SDK for [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev): embed a multi-LLM coding agent
//! (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible) in Rust applications.
//! The SDK starts `dotcode serve` and talks JSON-RPC 2.0 to it over stdio. It is synchronous and thread-based
//! (no async runtime required); from async code, call it inside `spawn_blocking`.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
//!
//! ```no_run
//! use dotcode_sdk::tool::{define_tool, JsonSchema};
//! use dotcode_sdk::{Client, ClientOptions, SessionConfig, SessionEventData};
//! use serde::Deserialize;
//!
//! #[derive(Deserialize, JsonSchema)]
//! struct WeatherParams {
//!     /// City name
//!     city: String,
//! }
//!
//! let weather = define_tool("get_weather", "Weather for a city", |_inv, p: WeatherParams| {
//!     Ok::<_, String>(format!("{}: sunny", p.city))
//! });
//!
//! let client = Client::start(ClientOptions::default())?;          // spawns `dotcode serve`
//! let session = client.create_session(
//!     SessionConfig::default()
//!         .with_model("openai:gpt-5-mini")
//!         .with_tools([weather])
//!         .approve_all_permissions(),
//! )?;
//! session.on(|e| if let SessionEventData::AssistantTextDelta { text } = &e.data { print!("{text}") }).detach();
//! let result = session.send_and_wait("What's the weather in Bogor?")?;
//! println!("\n${:.4}", result.cost_usd);
//! # Ok::<(), dotcode_sdk::Error>(())
//! ```

mod client;
mod config;
mod event;
pub mod handler;
mod session;
pub mod tool;
mod types;

pub use client::{Client, ClientOptions, PROTOCOL_VERSION};
pub use config::SessionConfig;
pub use event::{SessionEvent, SessionEventData, TodoItem, ToolCallInfo};
pub use handler::{
    ApproveAllHandler, DenyAllHandler, ExitPlanModeHandler, PermissionHandler, UserInputHandler,
};
pub use session::{EventStream, Session, Subscription};
pub use tool::{Tool, ToolError, ToolHandler, ToolInvocation, ToolResult};
pub use types::{
    Attachment, BuiltinTool, Error, ExitPlanModeResult, Invocation, McpServerConfig,
    MessageOptions, ModelInfo, PermissionDecision, PermissionMode, PermissionRequest,
    ProviderConfig, ProviderType, QuestionOption, ReasoningEffort, Result, SendResult, SessionInfo,
    SessionMetadata, StopReason, SystemMessageConfig, ToolInfo, Usage, UserQuestion,
    UserQuestionAnswer, WorktreeInfo,
};
