//! Rust SDK for [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev): embed a multi-LLM coding agent
//! (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible) in Rust applications.
//! The SDK starts `dotcode serve` and talks JSON-RPC 2.0 to it over stdio. It is synchronous and thread-based
//! (no async runtime required); from async code, call it inside `spawn_blocking`.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
//!
//! ```no_run
//! use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionOptions};
//!
//! let client = Client::new(ClientOptions::default())?;          // spawns `dotcode serve`
//! let session = client.create_session(
//!     SessionOptions::default()
//!         .model("openai:gpt-5-mini")
//!         .on_permission_request(|_req| PermissionDecision::allow()),
//! )?;
//! let result = session.send("Summarize README.md")?;
//! println!("{}", result.result);
//! # Ok::<(), dotcode_sdk::Error>(())
//! ```

mod client;
mod session;
mod types;

pub use client::{Client, ClientOptions, PROTOCOL_VERSION};
pub use session::{
    EventHandler, EventStream, PermissionHandler, PlanHandler, QuestionHandler, Session,
    SessionOptions, Tool,
};
pub use types::{
    Error, Event, McpServer, PermissionDecision, PermissionRequest, QuestionOption, Result,
    SendResult, SessionInfo, Usage, UserQuestion, UserQuestionAnswer, WorktreeInfo,
};
