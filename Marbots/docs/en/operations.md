# Operations: telemetry, push notifications, releases and performance

[English](../en/operations.md) · [Bahasa Indonesia](../id/operations.md)

## OpenTelemetry

Marbots emits traces and metrics through `System.Diagnostics` (source and meter name `Marbots`) and exports them over
OTLP to any collector: Grafana/Tempo, Jaeger, Honeycomb, Azure Monitor, Datadog, Aspire dashboard, and others.

```json
"Marbots": {
  "Telemetry": { "OtlpEndpoint": "http://otel-collector:4317", "Protocol": "grpc", "ServiceName": "marbots", "HeadersSecret": "OTLP_HEADERS" }
}
```

`OTEL_EXPORTER_OTLP_ENDPOINT` works too. Nothing is exported unless an endpoint is set. With `http/protobuf`, the
per-signal paths (`/v1/traces`, `/v1/metrics`, `/v1/logs`) are added for you.

**Spans** follow the OpenTelemetry GenAI conventions:

| Span | Attributes |
|---|---|
| `invoke_agent <bot>` | `gen_ai.agent.id/name`, `marbots.task.id`, `marbots.thread.id`, `marbots.task.depth`, `marbots.tenant`, token totals, steps |
| `chat <model>` | `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.usage.input_tokens/output_tokens` |
| `execute_tool <name>` | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `marbots.tool.pack`, `marbots.outcome` |
| `delegate <manager>` | `marbots.delegation.count`, `marbots.delegation.bots` |

A delegated task's `invoke_agent` span sits under the manager's `delegate` span, so one trace shows the whole
delegation tree. ASP.NET Core and HttpClient spans are included as well, so model calls appear as HTTP client spans.

**Metrics**:

| Metric | Meaning |
|---|---|
| `gen_ai.client.token.usage` | Tokens per model call (`gen_ai.token.type` = input/output) |
| `gen_ai.client.operation.duration` | Model call latency |
| `marbots.tool.calls`, `marbots.tool.duration` | Tool calls by tool and outcome |
| `marbots.tasks`, `marbots.task.duration` | Finished tasks by state |
| `marbots.delegations` | Delegated sub-tasks |
| `marbots.cost` | Estimated model cost (USD) |

Logs go to OTLP too, with scopes.

## Push notifications

People get a notification when a bot is waiting for approval (urgent), or when a chat they started finishes or fails,
even when the app is closed.

| Platform | Setup |
|---|---|
| **ntfy** (no account) | Nothing on the server. The mobile app's **Settings → When the app is closed** registers a private random topic; subscribe to it in the ntfy app. A self-hosted ntfy server: `Push:NtfyServer`, token secret `NTFY_TOKEN`. |
| **Firebase Cloud Messaging** (Android, web) | Secret `FCM_SERVICE_ACCOUNT` = the Firebase service-account JSON. Marbots uses the HTTP v1 API with an OAuth JWT-bearer token. |
| **Apple Push Notification service** | Secret `APNS_AUTH_KEY` = the `.p8` key; `Push:ApnsKeyId`, `Push:ApnsTeamId`, `Push:ApnsBundleId` (and `ApnsProduction`). |

Devices register through `POST /api/v1/push/devices` with `{ platform, token, name, topics }` (Operator role), or
`client.Push.RegisterAsync(…)` in the .NET SDK. Topics are `approvals`, `completed` and `failed`. A token that FCM or
APNs reports as gone is removed. Set `Push:PublicUrl` to have notifications open the right page.

## Releases and supply chain

- Pushing a tag `marbots-v<version>` runs CI, then publishes NuGet packages and attaches the following to the GitHub
  release:
  - `marbots-host` binaries for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64;
  - **CycloneDX SBOMs** for the .NET solution, the TypeScript SDK and the Python SDK;
  - `SHA256SUMS`.
- Skill packages can be signed (see [Skills](skills.md#signed-skill-packages)).

## Performance

Measured on a laptop (Core i7-8650U, 4 cores), .NET 10 Release, before and after the 0.3 tuning:

| Path | Before | After | What changed |
|---|---|---|---|
| `grep` for a rare literal over 104 MB / 3,000 files | 1,323 ms | 183 ms | UTF-8 byte prefilter, whole-file pre-check, parallel ordered chunks |
| Hybrid memory search, 5,000 memories | 284 ms | 85 ms | Scan ids + vectors only, SIMD cosine, `(tenant, owner, created_at)` index |
| Hashing embedding of a 2 KB memory | 0.46 ms | 0.40 ms | — |
| BM25 over 2,000 candidates | 24 ms | 21 ms | — |
| Token estimate, 400 messages | 0.02 ms | 0.02 ms | — |

Both remaining costs are close to the I/O floor: reading the same files as bytes takes 365 ms serially, and reading
5,000 vectors from SQLite takes 45 ms. A model call takes 1–10 s. **Decision (ADR-011): no native Rust library.**
Profiling found algorithmic costs, which C# fixed. Revisit if a profile shows CPU-bound work above 100 ms per agent
step after these fixes.

---
*Marbots: Created by Gravicode Studios, led by Kang Fadhil.*
