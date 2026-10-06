# MCP servers

[English](../en/mcp.md) · [Bahasa Indonesia](../id/mcp.md)

Marbots includes a Model Context Protocol (MCP) client for **stdio** servers (local processes) and **streamable
HTTP** servers. MCP tools appear to a bot as `mcp__<server>__<tool>` and go through the same policy engine, approvals
and audit events as built-in tools.

![MCP gallery](../images/mcp.png)

## The gallery

| Server | Transport | Installed by default |
|---|---|---|
| Filesystem (`@modelcontextprotocol/server-filesystem`) | npx | ✅ (scoped to the thread workspace) |
| Sequential Thinking | npx | ✅ |
| Knowledge Graph Memory | npx | |
| Everything (test server) | npx | |
| Playwright Browser (`@playwright/mcp`) | npx | |
| GitHub (needs secret `GITHUB_TOKEN`) | npx | |
| Fetch, Time, Git | uvx | |
| Context7 docs | npx | |

1. **Install** a server in the MCP gallery.
2. **Test & list tools** starts it and shows its tools. The first `npx`/`uvx` start may download packages.
3. Enable it on a bot (Team → Edit → MCP servers).

## Workspace-scoped servers

If a server's arguments contain `{workspace}`, Marbots starts **one process per project workspace** and substitutes the
folder path. The Filesystem server can therefore only touch the files of the thread it serves.

## Custom servers

**Add custom server** accepts:

- *stdio*: command, arguments (one per line, `{workspace}` allowed), environment variables. Use `secret:NAME` to
  reference a stored secret instead of pasting a value.
- *HTTP*: endpoint URL (JSON or SSE responses; `Mcp-Session-Id` is handled).
- *Permission category*: read-only, workspace write, network, process execution (asks) or external communication
  (asks). This decides how the policy engine treats every tool of that server.

On Windows, `npx`/`uvx` shims are launched through `cmd.exe` automatically.

## Example

```
Boss Man › Create "Mira" from the data-engineer template with MCP servers filesystem and time,
           then have Mira write reports/world-clock.md with the time in Jakarta and Tokyo using MCP tools.
```

Mira calls `mcp__time__get_current_time` twice and `mcp__filesystem__write_file` once. See [trials](trials.md).

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
