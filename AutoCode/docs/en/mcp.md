# Model Context Protocol (MCP)

> Auto Code — Gravicode Studios, led by Kang Fadhil

MCP is an open protocol for exposing tools and data to AI agents. Auto Code is an MCP **client**: any
MCP server's tools become tools the agent can call, alongside the built-in ones.

This is the supported way to extend Auto Code with new capabilities. An MCP server can be written in
any language, and works with every MCP client — not just this one.

## Configuring a server

```jsonc
{
  "mcpServers": {
    "github": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_..." }
    },
    "postgres": {
      "transport": "stdio",
      "command": "uvx",
      "args": ["mcp-server-postgres", "postgresql://localhost/mydb"]
    },
    "internal-api": {
      "transport": "http",
      "url": "https://mcp.internal.example.com/sse",
      "headers": { "Authorization": "Bearer ..." }
    },
    "staging-only": {
      "transport": "stdio",
      "command": "node",
      "args": ["./tools/staging-mcp.js"],
      "disabled": true
    }
  }
}
```

| Field | Applies to | Meaning |
| --- | --- | --- |
| `transport` | both | `stdio` (default) or `http` |
| `command`, `args` | stdio | The process to launch |
| `env` | stdio | Environment overrides for that process |
| `url`, `headers` | http | Endpoint and any auth headers |
| `disabled` | both | Skip without deleting the configuration |

Put server credentials in `.autocode/settings.local.json`, not in the committed file.

## Naming

MCP tools are exposed as `mcp__<server>__<tool>`. `github`'s `create_issue` becomes
`mcp__github__create_issue`.

The prefix is not decoration. It stops a server shadowing a built-in tool, and it makes permission
rules expressible:

```jsonc
{
  "permissions": {
    "allow": ["mcp__github__get_*", "mcp__postgres__query"],
    "deny":  ["mcp__postgres__execute", "mcp__github__delete_*"]
  }
}
```

## Permissions and MCP

The protocol does not tell a client what a tool will do, so Auto Code assumes the worst: **every MCP
tool is treated as capable of writing files, running commands and reaching the network**, and is
therefore subject to approval unless you have explicitly allowed it.

That is deliberately conservative. If a server is trusted and its tools are read-only, allow-list
them and you will not be asked again.

## Inspecting

```bash
autocode mcp        # configured servers, from outside a session
```

```
› /mcp              # live connection status and tool counts
› /tools            # every tool, MCP tools included
```

A server that fails to start is reported and skipped — a broken integration never costs you the
session:

```
● github      12 tools
● postgres    Command 'uvx' not found
```

## Lifecycle

Servers start when the session starts and shut down when it ends. `stdio` servers run as child
processes rooted at the workspace directory.

Auto Code does not restart a server that dies mid-session. Fix the cause and restart the session.

## Useful servers

| Server | Gives the agent |
| --- | --- |
| `@modelcontextprotocol/server-github` | Issues, pull requests, code search |
| `@modelcontextprotocol/server-filesystem` | Access outside the workspace root |
| `mcp-server-postgres` | Schema inspection and queries |
| `@modelcontextprotocol/server-puppeteer` | Browser automation |
| `@modelcontextprotocol/server-slack` | Reading and posting to Slack |

## Writing one

The .NET SDK is the same package Auto Code uses as a client:

```bash
dotnet new console -o my-mcp-server
cd my-mcp-server
dotnet add package ModelContextProtocol
```

```csharp
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static class DeploymentTools
{
    [McpServerTool, Description("Returns the currently deployed version in an environment.")]
    public static string GetDeployedVersion(
        [Description("Environment name: staging or production")] string environment) =>
        environment switch
        {
            "staging" => "1.4.2",
            "production" => "1.3.9",
            _ => $"Unknown environment '{environment}'.",
        };
}
```

Then point Auto Code at it:

```jsonc
{
  "mcpServers": {
    "deploy": { "command": "dotnet", "args": ["run", "--project", "./tools/my-mcp-server"] }
  }
}
```

Write tool descriptions the way you would write documentation for a colleague who cannot ask
follow-up questions. The description is the entire interface as far as the model is concerned.
