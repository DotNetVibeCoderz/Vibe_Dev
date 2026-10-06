# marbots-sdk (Python)

Typed, zero-dependency Python client for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots),
the multi-agent collaboration platform. Responses are frozen dataclasses; kernel packs, permission profiles, event types
and model settings are enums/helpers, so `mypy` (and your IDE) catch typos before you run anything.

```bash
pip install marbots-sdk
```

```python
from marbots_sdk import MarbotsClient, BotSpec, KernelPack, PermissionProfile, ModelRef, EventType

mb = MarbotsClient("http://localhost:5170")            # api_key="..." if the server requires one

# Every bot can use its own model; ModelRef.DEFAULT follows the workspace default.
sari = mb.bots.create(BotSpec(
    name="Sari", role="UX designer",
    kernel_functions=[KernelPack.FILES, KernelPack.WEB],
    permission_profile=PermissionProfile.WORKSPACE_WRITE,
    model=ModelRef.of("azure", "gpt-5.6-luna"),
))
mb.bots.set_model("atlas", ModelRef.DEFAULT)
print(mb.models.list().default)                         # e.g. azure/gpt-5-mini

thread = mb.threads.create(sari.id)
result = mb.threads.send(thread.id, "Sketch a landing-page wireframe", wait=True)
print(result.task.model, result.task.cost_usd, result.text)

for event in mb.events.stream(thread.id):               # live Server-Sent Events
    if event.type == EventType.TOOL_CALL_STARTED:
        print("tool:", event.message)
    if event.is_task_finished():
        break
```

| Area | API |
|---|---|
| Bots | `bots.list/get/create(BotSpec)/update/delete/hire(template)/get_model/set_model/pause/resume/export/import_package` |
| Models | `models.list()` → `ModelCatalog`, `models.set_default("provider/model")` |
| Chat | `threads.create/send(wait=True)/messages/files/download/export_transcript`, `chat(bot, text)` |
| Work | `tasks`, `approvals.approve(id, scope="Session")`, `schedules.create(ScheduleSpec)`, `memory`, `skills`, `mcp` |
| Live | `events.stream(thread_id)` → `AgentEvent` |

Type safety is tested: `python tests/typecheck/run.py` requires `mypy --strict` to pass on the SDK and to reject every
deliberate typo in `tests/typecheck/check_types.py`. The conformance tests (`tests/test_conformance.py`) run against a
real Marbots server.

Built by Gravicode Studios, led by Kang Fadhil. MIT license.
