# DotCode SDK — Rust

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your Rust application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK starts a `dotcode serve` process and talks JSON-RPC 2.0 to it over stdio.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```toml
[dependencies]
dotcode-sdk = "0.1"
serde_json = "1"   # for tool schemas and event payloads
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the `settings` option.

The API is synchronous and thread-based, so no async runtime is required. From async code, call it inside `tokio::task::spawn_blocking` (or your runtime's equivalent). The only dependencies are `serde` and `serde_json`.

## Usage

```rust
use dotcode_sdk::{Client, ClientOptions, PermissionDecision, SessionOptions, Tool};
use serde_json::json;

let client = Client::new(ClientOptions::default())?;              // spawns `dotcode serve`
let session = client.create_session(
    SessionOptions::default()
        .model("azure:gpt-5-mini")
        .tool(Tool::new("get_weather", "Weather for a city",
            json!({"type": "object", "properties": {"city": {"type": "string"}}, "required": ["city"]}),
            |input| Ok(format!("{}: rainy, 24°C", input["city"].as_str().unwrap_or("?")))).read_only())
        .on_permission_request(|req| {
            if req.tool_name == "Bash" { PermissionDecision::deny_with("No shell commands, please") }
            else { PermissionDecision::allow() }
        }),
)?;

// Run to completion…
let result = session.send("What's the weather in Bogor?")?;
println!("{} (${:.4})", result.result, result.cost_usd);

// …or stream events as they happen.
let mut stream = session.stream("List the files here and summarize the project");
for event in stream.by_ref() {
    match event.kind.as_str() {
        "assistant.text.delta" => print!("{}", event.text.unwrap_or_default()),
        "tool.started" => println!("● {}", event.display_name.unwrap_or_default()),
        _ => {}
    }
}
let outcome = stream.result()?;
# Ok::<(), dotcode_sdk::Error>(())
```

A complete sample is in [`samples/sdk/rust`](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/DotCode/samples/sdk/rust) (`cargo run -- azure:gpt-5-mini`).

## Features

- Sessions with `send` (blocking) and `stream` (iterator of events: text deltas, tool calls, diffs, todos, usage, cost)
- **Custom tools** implemented as Rust closures
- **Permission handler**: sessions are deny-by-default without one. Decisions: `allow`, `allow_always(rule)`, `allow_session`, `deny` / `deny_with(feedback)`
- Question (AskUserQuestion) and plan-review handlers, plus an `on_event` callback
- MCP servers, allowed/disallowed tool rules, permission modes (including `auto`), system prompt overrides, `set_model`, `compact`, `abort`, transcripts, resume/fork, git `worktree` sessions
- BYOK: pass `settings` with a `providers` block to use your own keys and endpoints per session

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

## Tests

`cargo test` runs conformance tests against a real `dotcode serve` with DotCode's offline scripted model (no keys, no network). They need the CLI built (`dotnet build` in `DotCode/`) or `DOTCODE_CLI_PATH`.

License: MIT
