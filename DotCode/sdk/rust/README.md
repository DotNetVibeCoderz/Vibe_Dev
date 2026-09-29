# DotCode SDK — Rust

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your Rust application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK starts a `dotcode serve` process and talks JSON-RPC 2.0 to it over stdio.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```toml
[dependencies]
dotcode-sdk = "0.2"
serde = { version = "1", features = ["derive"] }
schemars = "1"     # the JsonSchema derive for tool parameters
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the typed `with_provider` option.

The API is synchronous and thread-based, so no async runtime is required. From async code, call it inside `tokio::task::spawn_blocking` (or your runtime's equivalent).

## Usage

```rust
use dotcode_sdk::tool::{define_tool, JsonSchema};
use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionConfig, SessionEventData};
use serde::Deserialize;

#[derive(Deserialize, JsonSchema)]      // the tool schema is generated from this type
struct WeatherParams {
    /// City name
    city: String,
}

let get_weather = define_tool("get_weather", "Weather for a city", |_inv, p: WeatherParams| {
    Ok::<_, String>(format!("{}: 24°C", p.city))  // p.cty would not compile
});

let client = Client::start(ClientOptions::default())?;             // spawns `dotcode serve`
let session = client.create_session(
    SessionConfig::default()
        .with_model("azure:gpt-5-mini")
        .with_tools([get_weather])
        .on_permission_request(|req, _| {
            if req.tool_name == "Bash" { PermissionDecision::reject(Some("No shell commands, please".into())) }
            else { PermissionDecision::approve_once() }
        }),
)?;

let _sub = session.on(|e| match &e.data {                          // typed events
    SessionEventData::AssistantTextDelta { text } => print!("{text}"),
    SessionEventData::ToolCompleted { name, summary, .. } => println!("\n✔ {name}: {summary}"),
    _ => {}
});
let result = session.send_and_wait("What's the weather in Bogor?")?;
println!("\n${:.4}", result.cost_usd);
# Ok::<(), dotcode_sdk::Error>(())
```

Doc comments on parameter fields become descriptions; `Option<T>` fields are optional. Arguments are decoded before
the handler runs (mismatches are reported to the model). For shared state, implement the `ToolHandler` trait on a
named type and attach it with `Tool::new(name).with_parameters(schema_for::<P>()).with_handler(Arc::new(...))`.

A complete sample is in [`samples/sdk/rust`](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/DotCode/samples/sdk/rust) (`cargo run -- azure:gpt-5-mini`).

## API

| | |
|---|---|
| `Client` | `Client::start(ClientOptions)`, `create_session(SessionConfig)`, `resume_session(id, config)`, `fork_session(id, config)`, `list_sessions()`, `list_models()`, `ping()`, `stop()`, `force_stop()` |
| `Session` | `send(msg)` (returns once dispatched), `send_and_wait(msg)`, `send_and_wait_timeout(msg, Duration)` (aborts on timeout), `stream(msg)` (event iterator + `result()`), `on(handler)` → `Subscription`, `subscribe()` → channel, `abort()`, `set_model()`, `set_permission_mode(PermissionMode)`, `set_reasoning_effort(ReasoningEffort)`, `compact()`, `clear()`, `get_messages()`, `list_tools()`, `disconnect()` |
| Tools | `define_tool(name, description, \|inv, params: P\| ...)`, `schema_for::<P>()`, `ToolHandler` trait, `Tool::read_only()`; results: `String`, `&str`, `Value` or `ToolResult` |
| Permissions | `approve_all_permissions()`, `deny_all_permissions()`, `approve_permissions_if(pred)`, `on_permission_request(closure)` or `with_permission_handler(impl PermissionHandler)`; decisions `PermissionDecision::approve_once()`, `approve_for_session()`, `approve_always(rule)`, `reject(feedback)`; sessions are deny-by-default without a handler |
| Other handlers | `on_user_input_request` (AskUserQuestion), `on_exit_plan_mode` (plan review, `ExitPlanModeResult`), `on_event` |
| Config | `with_model`, `with_fallback_model`, `with_working_directory`, `with_permission_mode`, `with_reasoning_effort`, `with_system_message(SystemMessageConfig::Append(..))`, `with_available_tools([BuiltinTool::Read])`, `with_allowed_tools([BuiltinTool::Bash.rule("npm test:*")])`, `with_excluded_tools`, `with_mcp_server(name, McpServerConfig::stdio(..))`, `with_provider(name, ProviderConfig::new(ProviderType::Ollama))`, `with_max_turns`, `with_persist_session`, `with_worktree`, `with_disable_mcp` |

Feature `derive` (default) enables `define_tool`/`schema_for` via `schemars`; disable default features for a
`serde`-only build.

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

## Tests

`cargo test` runs conformance tests against a real `dotcode serve` with DotCode's offline scripted model (no keys, no network). They need the CLI built (`dotnet build` in `DotCode/`) or `DOTCODE_CLI_PATH`.

License: MIT
