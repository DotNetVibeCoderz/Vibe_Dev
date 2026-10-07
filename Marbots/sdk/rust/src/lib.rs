//! Typed Rust SDK for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots), the multi-agent
//! collaboration platform. Synchronous (no async runtime required); from async code, call it inside `spawn_blocking`.
//! Kernel packs, permission profiles, task states, event types and model settings are enums/helpers, so typos are
//! compile errors.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
//!
//! ```no_run
//! use marbots_sdk::{BotSpec, Client, EventType, KernelPack, ModelRef, PermissionProfile};
//!
//! let mb = Client::new("http://localhost:5170");
//! // Every bot can run on its own model; ModelRef::DEFAULT follows the workspace default.
//! let sari = mb.bots().create(
//!     BotSpec::new("Sari")
//!         .role("UX designer")
//!         .kernel_functions([KernelPack::Files, KernelPack::Web])
//!         .permission_profile(PermissionProfile::WorkspaceWrite)
//!         .model(ModelRef::of("azure", "gpt-5.6-luna")),
//! )?;
//! mb.bots().set_model("atlas", ModelRef::DEFAULT)?;
//!
//! let thread = mb.threads().create(&sari.id, None)?;
//! let result = mb.threads().send(&thread.id, "Sketch a wireframe", true)?;
//! println!("{:?}: {}", result.task.model, result.text());
//!
//! for event in mb.events().stream(Some(&thread.id))? {
//!     let event = event?;
//!     if event.event_type == EventType::ToolCallStarted { println!("tool: {:?}", event.message); }
//!     if event.is_task_finished() { break; }
//! }
//! # Ok::<(), marbots_sdk::Error>(())
//! ```

mod client;
mod types;

pub use client::{
    AgentHosts, Approvals, Bots, Client, Error, EventStream, Events, Mcp, Memory, Models, Result,
    Schedules, Skills, Tasks, Templates, Threads,
};
pub use types::*;
