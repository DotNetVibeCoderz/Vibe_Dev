# DotCode SDK — Go

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```bash
go get github.com/DotNetVibeCoderz/Vibe_Dev/DotCode/sdk/go
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the typed `Providers` option.

## Usage

```go
type WeatherParams struct {                     // parameters are a Go type, never a hand-written schema map
    City string `json:"city" jsonschema:"City name"`
    Unit string `json:"unit,omitempty" jsonschema:"Temperature unit" enum:"celsius,fahrenheit"`
}

getWeather := dotcode.DefineTool("get_weather", "Weather for a city",
    func(p WeatherParams, inv dotcode.ToolInvocation) (any, error) {
        return p.City + ": 24°C", nil            // p.Cty would not compile
    })

client := dotcode.NewClient(nil)                // spawns `dotcode serve` on Start
if err := client.Start(ctx); err != nil { log.Fatal(err) }
defer client.Stop()

session, err := client.CreateSession(ctx, &dotcode.SessionConfig{
    Model:               "azure:gpt-5-mini",
    Tools:               []dotcode.Tool{getWeather},
    OnPermissionRequest: dotcode.PermissionHandler.ApproveAll,
})
if err != nil { log.Fatal(err) }
defer session.Disconnect()

session.On(func(e dotcode.SessionEvent) {
    switch d := e.Data.(type) {
    case *dotcode.AssistantTextDeltaData:
        fmt.Print(d.Text)
    case *dotcode.ToolCompletedData:
        fmt.Printf("
✔ %s: %s
", d.Name, d.Summary)
    }
})
res, err := session.SendAndWait(ctx, dotcode.MessageOptions{Prompt: "What's the weather in Bogor?"})
```

The schema comes from the struct: `json` tags name the properties, `jsonschema` tags describe them, `enum` tags
(or a type with an `EnumValues() []string` method) restrict values, and `omitempty`/pointer fields are optional.
Arguments are validated and decoded before your handler runs. Permission modes, reasoning effort, built-in tools
(`dotcode.BuiltinToolRead`, `dotcode.BuiltinToolBash.Rule("npm test:*")`), providers
(`ProviderConfig{Type: dotcode.ProviderOllama}`), MCP servers and events are typed.

## API

| | |
|---|---|
| `Client` | `NewClient(*ClientOptions)`, `Start(ctx)`, `CreateSession(ctx, *SessionConfig)`, `ResumeSession(ctx, id, *ResumeSessionConfig)`, `ListSessions`, `ListModels`, `Ping`, `Stop()`, `ForceStop()` |
| `Session` | `Send(ctx, MessageOptions)` (returns once dispatched), `SendAndWait(ctx, MessageOptions)` (ctx cancellation aborts the turn), `Stream`, `On(handler)`, `Abort`, `SetModel`, `SetPermissionMode`, `SetReasoningEffort`, `Compact`, `Clear`, `GetMessages`, `ListTools`, `Disconnect()` |
| Tools | `DefineTool[T](name, description, func(T, ToolInvocation) (any, error))`, `.WithReadOnly()`; return a string, a `ToolResult` or any JSON value. The `Tool` struct with a raw `Parameters` map remains for hand-tuned schemas |
| Permissions | `PermissionHandler.ApproveAll` / `RejectAll`, or return `&PermissionDecisionApproveOnce{}`, `&PermissionDecisionApproveForSession{}`, `&PermissionDecisionApproveAlways{Rule: …}`, `&PermissionDecisionReject{Feedback: …}`; sessions are deny-by-default without a handler |
| Other handlers | `OnUserInputRequest` (AskUserQuestion), `OnExitPlanMode` (plan review), `OnEvent` |
| Config | `Model`, `FallbackModel`, `WorkingDirectory`, `PermissionMode`, `ReasoningEffort`, `SystemMessage`, `AvailableTools`, `AllowedTools`, `ExcludedTools`, `McpServers` (`*McpStdioServer` / `*McpHTTPServer`), `Providers` (BYOK), `MaxTurns`, `PersistSession`, `Worktree`, `DisableMcp` |

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

License: MIT
