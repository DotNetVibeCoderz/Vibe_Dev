# DotCode SDK — Java

Embed the DotCode coding agent in JVM applications and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). Zero dependencies (JDK 17+); talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 [docs/id/sdk.md](../../docs/id/sdk.md)

## Install

Maven coordinates `com.gravicode:dotcode-sdk:0.1.0` (or build locally: `mvn install` in this folder). The `dotcode` CLI must be on `PATH` or set `DOTCODE_CLI_PATH`.

## Usage

```java
try (var client = DotCodeClient.start(new DotCodeClient.Options())) {
    var session = client.createSession(SessionOptions.builder()
            .model("anthropic:claude-sonnet-4-5")
            .tool(new Tool("now", "Current time", Map.of("type", "object"), true, in -> java.time.Instant.now().toString()))
            .onPermissionRequest(req -> PermissionDecision.allow()));
    var result = session.stream("What time is it? Then list the files here.", e -> {
        if ("assistant.text.delta".equals(e.type())) System.out.print(e.text());
    });
    System.out.println("\ncost $" + result.costUsd());
}
```

API: `DotCodeClient.start`, `createSession`, `resumeSession`, `listModels`, `listSessions`; `Session.send`, `sendAsync`, `stream`, `onEvent`, `abort`, `setModel`, `setPermissionMode`, `compact`, `messages`, `close`. Sessions are deny-by-default without `onPermissionRequest`.

Tests: `javac` the sources and run `com.gravicode.dotcode.ConformanceTest` (or `mvn verify`), which exercises host tools, streaming and permission callbacks against a real server with the scripted provider.

License: MIT
