# DotCode SDK — Java

Embed the DotCode coding agent in JVM applications and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). Zero dependencies (JDK 17+); talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 [docs/id/sdk.md](../../docs/id/sdk.md)

## Install

Published on **JitPack** (built from this repository's tags):

```xml
<repositories>
  <repository><id>jitpack.io</id><url>https://jitpack.io</url></repository>
</repositories>
<dependency>
  <groupId>com.github.DotNetVibeCoderz</groupId>
  <artifactId>Vibe_Dev</artifactId>
  <version>dotcode-java-v0.2.0</version>
</dependency>
```

Gradle: `repositories { maven { url "https://jitpack.io" } }` and `implementation "com.github.DotNetVibeCoderz:Vibe_Dev:dotcode-java-v0.2.0"`.

Maven Central (`com.gravicode:dotcode-sdk`) is prepared (`publish-central.sh` + the `maven` workflow job) and goes live once the namespace is verified in the Sonatype Central Portal. Or build locally with `mvn install`.

The `dotcode` CLI must be on `PATH` or set `DOTCODE_CLI_PATH`.

## Usage

```java
// Tool parameters are a record: the JSON schema is generated from it, so a typo does not compile.
record WeatherParams(@ToolParam("City name") String city,
                     @ToolParam(value = "Temperature unit", required = false) Unit unit) {}

ToolDefinition getWeather = ToolDefinition.from("get_weather", "Weather for a city", WeatherParams.class,
        p -> p.city() + ": 24°C");

try (var client = new DotCodeClient()) {               // spawns `dotcode serve` on start()
    client.start().get();
    try (var session = client.createSession(new SessionConfig()
            .setModel("anthropic:claude-sonnet-4-5")
            .addTool(getWeather)
            .setOnPermissionRequest(PermissionHandler.APPROVE_ALL)).get()) {
        session.on(SessionEvent.AssistantTextDeltaEvent.class, e -> System.out.print(e.text()));
        session.on(SessionEvent.ToolCompletedEvent.class, e -> System.out.println("
✔ " + e.name() + ": " + e.summary()));
        SendResult result = session.sendAndWait(new MessageOptions("What's the weather in Bogor?")).get();
        System.out.println("cost $" + result.costUsd());
    }
}
```

Inline parameters work too: `ToolDefinition.from("search", "Searches items", Param.of(String.class, "keyword", "Search keyword"), kw -> ...)`
(and a two-parameter overload). Handlers may return a String, a `ToolResult`, a `CompletableFuture` or any
record/Map/List (sent as JSON); arguments are validated and converted before the handler runs.

## API

| | |
|---|---|
| `DotCodeClient` | `new DotCodeClient(DotCodeClientOptions)`, `start()`, `createSession(SessionConfig)`, `resumeSession(id, config)`, `forkSession(id, config)`, `listSessions()`, `listModels()`, `ping()`, `stop()`, `forceStop()`, `close()` — network calls return `CompletableFuture` |
| `DotCodeSession` | `send(MessageOptions)` (completes once dispatched), `sendAndWait(MessageOptions[, Duration])`, `on(handler)` / `on(EventClass, handler)`, `abort()`, `setModel()`, `setPermissionMode(PermissionMode)`, `setReasoningEffort(ReasoningEffort)`, `compact()`, `clear()`, `getMessages()`, `listTools()`, `disconnect()` / `close()` |
| Tools | `ToolDefinition.from(name, description, RecordClass, handler)`, `fromWithToolInvocation(...)`, `fromAsync(...)`, `from(name, description, Param, handler)`, `.readOnly(true)`; `@ToolParam(value, required)` on record components |
| Permissions | `PermissionHandler.APPROVE_ALL` / `REJECT_ALL`, or a lambda returning `PermissionDecision.approveOnce()`, `approveForSession()`, `approveAlways(rule)`, `reject(feedback)`; sessions are deny-by-default without a handler |
| Other handlers | `setOnUserInputRequest` (AskUserQuestion), `setOnExitPlanMode` (plan review), `setOnEvent` |
| Config | `setModel`, `setFallbackModel`, `setWorkingDirectory`, `setPermissionMode`, `setReasoningEffort`, `setSystemMessage(SystemMessageConfig.append(...))`, `setAvailableTools(List<BuiltinTool>)`, `addAllowedTool(BuiltinTool.BASH.rule("npm test:*"))`, `addExcludedTool`, `putMcpServer(name, McpServerConfig.stdio(...))`, `putProvider(name, new ProviderConfig(ProviderType.OLLAMA))`, `setMaxTurns`, `setPersistSession`, `setWorktree`, `setDisableMcp` |

Events are a sealed hierarchy (`SessionEvent.ToolCompletedEvent`, `TurnCompletedEvent`, …), so on Java 21 you can
`switch (event) { case ToolCompletedEvent t -> ...; default -> {} }`.

Tests: `javac` the sources and run `com.gravicode.dotcode.ConformanceTest` (or `mvn verify`), which exercises host tools, streaming and permission callbacks against a real server with the scripted provider.

License: MIT
