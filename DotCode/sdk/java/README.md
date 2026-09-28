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
  <version>dotcode-java-v0.1.2</version>
</dependency>
```

Gradle: `repositories { maven { url "https://jitpack.io" } }` and `implementation "com.github.DotNetVibeCoderz:Vibe_Dev:dotcode-java-v0.1.2"`.

Maven Central (`com.gravicode:dotcode-sdk`) is prepared (`publish-central.sh` + the `maven` workflow job) and goes live once the namespace is verified in the Sonatype Central Portal. Or build locally with `mvn install`.

The `dotcode` CLI must be on `PATH` or set `DOTCODE_CLI_PATH`.

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
