# marbots-sdk (Rust)

Typed, synchronous Rust client for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots), the
multi-agent collaboration platform. Kernel packs, permission profiles, task states, event types and model settings are
enums/helpers (`KernelPack::Files`, `PermissionProfile::DeveloperSafe`, `TaskState::Completed`,
`EventType::ToolCallStarted`, `ModelRef::of`), so typos are compile errors.

```toml
[dependencies]
marbots-sdk = "0.1"
```

```rust
use marbots_sdk::{BotSpec, Client, EventType, KernelPack, ModelRef, PermissionProfile};

let mb = Client::new("http://localhost:5170");             // .with_api_key("...") if required
let sari = mb.bots().create(
    BotSpec::new("Sari").role("UX designer")
        .kernel_functions([KernelPack::Files, KernelPack::Web])
        .permission_profile(PermissionProfile::WorkspaceWrite)
        .model(ModelRef::of("azure", "gpt-5.6-luna")),     // or ModelRef::DEFAULT
)?;
let thread = mb.threads().create(&sari.id, None)?;
let r = mb.threads().send(&thread.id, "Sketch a wireframe", true)?;
println!("{:?}: {}", r.task.model, r.text());

for e in mb.events().stream(Some(&thread.id))? {
    let e = e?;
    if e.event_type == EventType::ToolCallStarted { println!("tool: {:?}", e.message); }
    if e.is_task_finished() { break; }
}
```

| Area | API |
|---|---|
| Bots | `bots().list/get/create(BotSpec)/update/delete/hire/get_model/set_model/pause/resume/export/import_package` |
| Models | `models().list()` → `ModelCatalog`, `models().set_default("provider/model")` |
| Chat | `threads().create/send/send_with_timeout/messages/files/download/delete`, `chat(bot, text)` |
| Work | `tasks()`, `approvals()` (incl. `set_skip_approvals`), `schedules().create(&ScheduleSpec::cron(..))`, `memory()`, `skills()`, `mcp()` |
| Live | `events().stream(thread_id)` → `Iterator<Item = Result<AgentEvent>>` |

`cargo test` runs the conformance test against a real Marbots server (`dotnet build Marbots.slnx` first).

Built by Gravicode Studios, led by Kang Fadhil. MIT license.

## Computers, placement and learning evaluation (0.2.0)

See [docs/en/computers.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/Marbots/docs/en/computers.md) and the Skills guide.

```rust
// Computers (agent hosts): list, one-time enrollment token, SSH bootstrap (credentials used once)
for h in mb.agent_hosts().list()? {
    println!("{} {} {:?}", h.name, h.status, h.capabilities);
}
println!("{}", mb.agent_hosts().create_enrollment("design-pc", 60)?.enroll_command);

// A bot that runs on whichever computer fits, with its shell in Docker and parallel sub-agents
let nova = mb.bots().create(
    BotSpec::new("Nova")
        .host_ref(HostRef::AUTO)
        .container(ContainerProfile::new("python:3.12-slim"))
        .kernel_functions([KernelPack::Files, KernelPack::Shell, KernelPack::Subagents]),
)?;

// Learning evaluation
for e in mb.skills().evaluations()? {
    println!("{} {} {:?} {}", e.name, e.version, e.verdict, e.reason);
}
```
