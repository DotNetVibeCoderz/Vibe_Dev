# Troubleshooting

[English](../en/troubleshooting.md) · [Bahasa Indonesia](../id/troubleshooting.md)

| Symptom | Cause and fix |
|---|---|
| Bots reply "I'm running in offline mock mode" | No model provider is configured. Add one in **Settings** or via configuration ([getting started](getting-started.md#2-connect-a-model)). |
| `HTTP 401` from the provider | Wrong API key, or an Azure endpoint used with kind `openai`. Azure needs kind `azure-openai`. |
| `HTTP 404` from Azure | The model/deployment name in the profile does not exist on that resource. |
| A task sits in **WaitingForHuman** | An approval is pending. Open **Approvals** (badge in the menu) or run `marbots approvals`. |
| "Denied by policy" in tool results | The bot's permission profile forbids that category. Edit the bot and choose another profile, or let another bot do it. |
| MCP server shows *error* | Open the MCP page, click **Test & list tools** and read the message. Check that `node`/`npx` or `uv`/`uvx` are on PATH. The first start downloads packages and can take a minute. Some community servers break on newer MCP libraries; pin a version in the arguments. |
| Web search returns no results | DuckDuckGo may rate-limit. Add the secret `TAVILY_API_KEY` in Settings. |
| UI shows no styles when running from source | Make sure you start with `dotnet run --project src/Marbots.Server` (static web assets are enabled automatically). |
| Tasks show *Interrupted by a platform restart* | The server stopped while they ran. Use **Retry** on the Tasks page. |
| Thread got slow or expensive | Type `/compact`, lower the bot's compaction threshold, or **Reset context**. |
| Need a clean slate | Stop the server and delete the data folder (`src/Marbots.Server/data` by default). This removes all bots, threads, memory and secrets. |

Logs are written to the console. Raise verbosity with `Logging:LogLevel:Marbots=Debug`.

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
