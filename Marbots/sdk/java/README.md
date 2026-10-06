# Marbots SDK for Java

Typed Java 17+ client for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots), the multi-agent
collaboration platform. No runtime dependencies. Kernel packs, permission profiles, task states, event types and model
settings are enums/helpers (`KernelPack.FILES`, `PermissionProfile.DEVELOPER_SAFE`, `TaskState.COMPLETED`,
`EventType.TOOL_CALL_STARTED`, `ModelRef.of(...)`), so typos are compile errors.

```xml
<dependency>
  <groupId>com.gravicode</groupId>
  <artifactId>marbots-sdk</artifactId>
  <version>0.1.0</version>
</dependency>
```

```java
import com.gravicode.marbots.*;

var mb = MarbotsClient.create("http://localhost:5170");       // create(url, apiKey) if required

// Every bot can run on its own model; ModelRef.DEFAULT follows the workspace default.
Bot sari = mb.bots().create(BotSpec.builder("Sari").role("UX designer")
    .kernelFunctions(KernelPack.FILES, KernelPack.WEB)
    .permissionProfile(PermissionProfile.WORKSPACE_WRITE)
    .model(ModelRef.of("azure", "gpt-5.6-luna")));
mb.bots().setModel("atlas", ModelRef.DEFAULT);

var thread = mb.threads().create(sari.id(), null);
try (var sub = mb.events().subscribe(thread.id(), e -> {
        if (e.type() == EventType.TOOL_CALL_STARTED) System.out.println("tool: " + e.message());
    })) {
    SendResult r = mb.threads().send(thread.id(), "Sketch a wireframe", true);
    System.out.println(r.task().model() + " → " + r.text());
}
```

| Area | API |
|---|---|
| Bots | `bots().list/get/create(BotSpec)/update/delete/hire/getModel/setModel/pause/resume/export/importPackage` |
| Models | `models().list()` → `ModelCatalog`, `models().setDefault("provider/model")` |
| Chat | `threads().create/send/messages/files/download/delete`, `chat(bot, text)` |
| Work | `tasks()`, `approvals()` (incl. `setSkipApprovals`), `schedules().create(ScheduleSpec.cron(...))`, `memory()`, `skills()`, `mcp()` |
| Live | `events().subscribe(threadId, handler)` → `AutoCloseable` |

Conformance tests: `mvn verify -Pconformance` (or compile and run `com.gravicode.marbots.ConformanceTest`) against a
built Marbots server.

Built by Gravicode Studios, led by Kang Fadhil. MIT license.
