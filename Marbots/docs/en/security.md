# Security, approvals and secrets

[English](../en/security.md) · [Bahasa Indonesia](../id/security.md)

Bots that can run tools have a large attack surface, so security is enforced by code outside the language model.

## Policy engine

Every tool call (built-in, skill or MCP) is checked by a deterministic policy engine **before** it runs. Tools declare
a permission category and a risk level:

| Category | Examples |
|---|---|
| ReadOnly | `read_file`, `grep`, `recall`, `list_bots` |
| WorkspaceWrite | `write_file`, `edit_file`, `remember` |
| Network | `web_search`, `web_fetch` |
| ProcessExecution | `run_shell` |
| DestructiveFilesystem | `delete_file` |
| ExternalCommunication | MCP servers marked *external* (e.g. GitHub) |
| AgentControl | `delegate_tasks`, `create_bot`, `schedule_task` |
| Admin | creating a bot with the `autonomous` profile |

The bot's permission profile maps each category to **Allow**, **Ask** or **Deny** (see
[bots](bots-and-templates.md#permission-profiles)). Critical-risk actions always ask. A denied call is returned to the
model as an error so the bot can change its plan; it is never executed.

## Approvals

**Ask** pauses the task (`WaitingForHuman`) and creates an approval visible in the chat (inline card), on the
**Approvals** page (with a badge in the menu), in the CLI (`marbots approvals`) and through the API.

![Approvals](../images/approvals.png)

| Decision | Effect |
|---|---|
| Approve once | Runs this call only |
| Approve for this thread | Allows this tool for this bot in this thread until restart |
| Reject | The bot receives "the user did not approve" and must adapt |

Approvals expire after `ApprovalTimeoutMinutes` (default 30). Approvals left pending by a restart are marked expired.
Every decision is kept in the history with who resolved it.

## Skipping approvals (dangerous mode)

Like Claude Code's `--dangerously-skip-permissions`, Marbots can run without asking:

| Where | How |
|---|---|
| Web UI | Settings → **Skip approvals (dangerous)** → *Skip approvals* |
| CLI | `marbots approvals skip on` (`off`, `status`), or per command: `marbots chat alice "…" --dangerously-skip-permissions` (alias `--dangerously-skip-approvals`; the previous mode is restored when the command ends) |
| Server start | `dotnet run --project src/Marbots.Server -- --dangerously-skip-permissions` (or `--dangerously-skip-approvals`) or `"Marbots": { "DangerouslySkipApprovals": true }` |
| API / SDK | `PUT /api/v1/system/approvals {"dangerouslySkipApprovals": true}` · `client.Approvals.SetSkipApprovalsAsync(true)` |

While it is on, every action that would **ask** runs immediately (shell, deletes, external messages, critical-risk
actions, creating autonomous bots). Turning it on also approves everything pending. Actions a bot's permission profile
**denies** stay denied, so a `read-only` bot still cannot write. Every auto-approved action is still recorded in the
approval history with `ResolvedBy = dangerously-skip-approvals`, and a red banner is shown on every page. The setting
persists across restarts until you turn it off. Use it only in a sandbox or with inputs you fully trust.

![Skip approvals](../images/settings-skip-approvals.png)

## Workspaces and paths

File tools resolve every path inside the thread workspace and reject traversal (`../`) and absolute paths outside it.
`run_shell` starts in the workspace folder. Choose `developer-safe` (the default) unless you trust a bot to run
commands unattended.

## Secrets

- Secrets entered in Settings are encrypted with ASP.NET Core Data Protection (keys protected by DPAPI on Windows) in
  `data/secrets.dat`. The UI and API only ever show secret **names**.
- Provider keys and secrets can also come from configuration or environment variables.
- MCP environment values can reference secrets (`secret:GITHUB_TOKEN`) so values never sit in configs or exports.
- `.marbot` exports never contain secrets; `remember` and Auto-Learn refuse content that looks like a credential.

## Prompt injection

Bots are told that tool output, web pages, files and messages from other agents are **untrusted data**. Even if a
model is tricked, the policy engine still decides what may run. Web content is stripped to text before the model sees it.

## API access

The REST API and A2A endpoints are open on localhost by default. To expose Marbots on a network, set an API key:

```json
{ "Marbots": { "ApiKey": "a-long-random-value" } }
```

Clients then send `X-Api-Key: <key>` (or `Authorization: Bearer <key>`). Put the server behind HTTPS (reverse proxy) when
it is reachable from other machines.

## Auto-Learn safeguards

Learned memories are filtered for secrets and length and are stored with lower confidence and provenance
`autolearn:<task>`. Learned skills go to a review queue and are never published automatically.

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
