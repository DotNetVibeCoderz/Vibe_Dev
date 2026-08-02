# Plugins

> Auto Code — Gravicode Studios, led by Kang Fadhil

A plugin is a directory that bundles skills, subagents, hooks and MCP server declarations so a team
can share a whole working setup as one unit.

Nothing in a plugin is compiled or executed at load time. A plugin contributes **declarations**; its
hooks only run when their event fires, and its MCP servers are subject to the same permission engine
as everything else.

## Layout

```
my-plugin/
├── autocode-plugin.json      # required manifest
├── skills/
│   ├── deploy/SKILL.md
│   └── rollback/SKILL.md
└── agents/
    ├── security-reviewer.md
    └── perf-analyst.md
```

```jsonc
// autocode-plugin.json
{
  "name": "acme-platform",
  "version": "1.2.0",
  "description": "Deployment workflows and reviewers for the ACME platform",

  "hooks": {
    "PostToolUse": [
      { "matcher": "Edit|Write", "command": "./scripts/check-conventions.sh", "blocking": false }
    ]
  },

  "mcpServers": {
    "acme-deploy": { "command": "npx", "args": ["-y", "@acme/mcp-deploy"] }
  }
}
```

The `skills/` and `agents/` folders use exactly the same formats as the workspace's own — see
[skills](skills.md) and [subagents](subagents.md).

## Where plugins are found

1. `~/.autocode/plugins/` — yours, every project
2. `<workspace>/.autocode/plugins/` — the project's
3. Anything listed in `plugins` in settings

```jsonc
{ "plugins": ["tools/autocode-plugins/acme-platform", "../shared/autocode-plugins"] }
```

A configured path may be a single plugin (it contains `autocode-plugin.json`) or a directory of them.

## Precedence

Plugin skills and agents are merged in after the workspace's own, so a plugin can add to what a
project defines. A workspace skill and a plugin skill with the same name resolve to the plugin's —
which means naming plugin contributions distinctly is worth doing.

Plugin hooks are **appended** to yours rather than replacing them. Plugin MCP servers are added only
if the name is not already taken, so a workspace can override one deliberately.

## Distributing

Plugins are plain directories, so any mechanism works:

```bash
# Git submodule
git submodule add https://github.com/acme/autocode-plugin tools/autocode-plugins/acme

# Or just clone into the user directory
git clone https://github.com/acme/autocode-plugin ~/.autocode/plugins/acme
```

Then reference it — or, in the user directory, nothing at all, since that location is scanned
automatically.

## Verifying what loaded

```
› /skills     # skills, plugin ones included
› /agents     # subagents
› /mcp        # servers a plugin declared
```

A plugin whose manifest is malformed is skipped silently rather than failing the session. If
something you expected is missing from those lists, check the JSON parses.

## What plugins deliberately are not

There is no compiled-plugin API — no assembly loading, no `IPlugin` interface to implement. Loading
arbitrary .NET assemblies into the agent's process would mean a plugin could bypass the permission
engine entirely, and that is the one boundary worth keeping absolute.

If you need genuinely new capability rather than new configuration, write an [MCP server](mcp.md).
It runs in its own process, works with every MCP client, and can be written in any language.
