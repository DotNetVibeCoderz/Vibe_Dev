# Marbots Go SDK

Typed Go client for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots), the multi-agent
collaboration platform. Kernel packs, permission profiles, task states, event types and model settings are typed
constants (`KernelPackFiles`, `PermissionDeveloperSafe`, `TaskCompleted`, `EventTaskStateChanged`, `ModelOf`).

```bash
go get github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go
```

```go
import marbots "github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go"

c := marbots.New("http://localhost:5170")        // marbots.WithAPIKey("...") if required

// Every bot can run on its own model; ModelDefault follows the workspace default.
sari, err := c.Bots.Create(ctx, marbots.BotSpec{
    Name: "Sari", Role: "UX designer",
    KernelFunctions:   []marbots.KernelPack{marbots.KernelPackFiles, marbots.KernelPackWeb},
    PermissionProfile: marbots.PermissionWorkspaceWrite,
    Model:             marbots.MustModel("azure", "gpt-5.6-luna"),
})
_, _ = c.Bots.SetModel(ctx, "atlas", marbots.ModelDefault)

thread, _ := c.Threads.Create(ctx, sari.ID, "")
events, _ := c.Events.Stream(ctx, thread.ID)
_, _ = c.Threads.Send(ctx, thread.ID, "Sketch a wireframe", marbots.SendOptions{})
for e := range events {
    if e.Type == marbots.EventToolCallStarted { fmt.Println("tool:", e.Message) }
    if e.TaskFinished() { break }
}
```

| Area | API |
|---|---|
| Bots | `Bots.List/Get/Create(BotSpec)/Update/Delete/Hire/GetModel/SetModel/Pause/Resume/Export/Import` |
| Models | `Models.List()` → `ModelCatalog`, `Models.SetDefault("provider/model")` |
| Chat | `Threads.Create/Send/Messages/Files/Download/Delete`, `Chat(ctx, bot, text)` |
| Work | `Tasks`, `Approvals` (incl. `SetSkipApprovals`), `Schedules.Create(ScheduleSpec)`, `Memory`, `Skills`, `MCP` |
| Live | `Events.Stream(ctx, threadID)` → `<-chan Event` |

`go test ./...` runs the conformance tests against a real Marbots server (`dotnet build Marbots.slnx` first).

Built by Gravicode Studios, led by Kang Fadhil. MIT license.

## Computers, placement and learning evaluation (0.2.0)

See [docs/en/computers.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/Marbots/docs/en/computers.md) and the Skills guide.

```go
// Computers (agent hosts): list, one-time enrollment token, SSH bootstrap (credentials used once)
hosts, _ := mb.AgentHosts.List(ctx)
tok, _ := mb.AgentHosts.CreateEnrollment(ctx, "design-pc", 60)
fmt.Println(len(hosts), tok.EnrollCommand)

// A bot that runs on whichever computer fits, with its shell in Docker and parallel sub-agents
nova, _ := mb.Bots.Create(ctx, marbots.BotSpec{Name: "Nova", HostRef: marbots.HostAuto,
	Container:       &marbots.ContainerProfile{Image: "python:3.12-slim", Cpus: 1, MemoryMb: 1024, Network: true},
	KernelFunctions: []marbots.KernelPack{marbots.KernelPackFiles, marbots.KernelPackShell, marbots.KernelPackSubagents}})

// Learning evaluation
evals, _ := mb.Skills.Evaluations(ctx)
for _, e := range evals {
	fmt.Println(e.Name, e.Version, e.Verdict == marbots.VerdictHealthy, e.Reason)
}
```
