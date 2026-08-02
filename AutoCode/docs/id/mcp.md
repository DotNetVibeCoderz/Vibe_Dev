# Model Context Protocol (MCP)

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

MCP adalah protokol terbuka untuk mengekspos tool dan data kepada agent AI. Auto Code adalah **klien**
MCP: tool dari server MCP mana pun menjadi tool yang bisa dipanggil agent, berdampingan dengan tool
bawaan.

Inilah cara resmi memperluas Auto Code dengan kemampuan baru. Server MCP bisa ditulis dengan bahasa
apa pun, dan bekerja dengan semua klien MCP — bukan hanya yang ini.

## Mengonfigurasi server

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
    "api-internal": {
      "transport": "http",
      "url": "https://mcp.internal.example.com/sse",
      "headers": { "Authorization": "Bearer ..." }
    },
    "khusus-staging": {
      "transport": "stdio",
      "command": "node",
      "args": ["./tools/staging-mcp.js"],
      "disabled": true
    }
  }
}
```

| Kolom | Berlaku untuk | Arti |
| --- | --- | --- |
| `transport` | keduanya | `stdio` (bawaan) atau `http` |
| `command`, `args` | stdio | Proses yang dijalankan |
| `env` | stdio | Environment variable untuk proses tersebut |
| `url`, `headers` | http | Endpoint dan header autentikasi |
| `disabled` | keduanya | Lewati tanpa menghapus konfigurasinya |

Taruh kredensial server di `.autocode/settings.local.json`, bukan di berkas yang ikut di-commit.

## Penamaan

Tool MCP diekspos sebagai `mcp__<server>__<tool>`. `create_issue` milik `github` menjadi
`mcp__github__create_issue`.

Awalan itu bukan hiasan. Ia mencegah sebuah server menutupi tool bawaan, dan membuat aturan izin bisa
dituliskan:

```jsonc
{
  "permissions": {
    "allow": ["mcp__github__get_*", "mcp__postgres__query"],
    "deny":  ["mcp__postgres__execute", "mcp__github__delete_*"]
  }
}
```

## Izin dan MCP

Protokolnya tidak memberi tahu klien apa yang akan dilakukan sebuah tool, sehingga Auto Code
mengasumsikan yang terburuk: **setiap tool MCP dianggap mampu menulis berkas, menjalankan perintah,
dan menyentuh jaringan**, karena itu memerlukan persetujuan kecuali Anda mengizinkannya secara
eksplisit.

Sikap itu memang sengaja konservatif. Bila sebuah server tepercaya dan tool-nya hanya membaca,
masukkan ke daftar allow dan Anda tidak akan ditanya lagi.

## Memeriksa

```bash
autocode mcp        # server yang dikonfigurasi, dari luar sesi
```

```
› /mcp              # status koneksi langsung dan jumlah tool
› /tools            # semua tool, termasuk tool MCP
```

Server yang gagal dijalankan dilaporkan lalu dilewati — integrasi yang rusak tidak pernah
mengorbankan sesi Anda:

```
● github      12 tools
● postgres    Command 'uvx' not found
```

## Siklus hidup

Server dijalankan saat sesi dimulai dan dimatikan saat sesi berakhir. Server `stdio` berjalan sebagai
proses anak yang berakar di direktori workspace.

Auto Code tidak menjalankan ulang server yang mati di tengah sesi. Perbaiki penyebabnya lalu mulai
ulang sesinya.

## Server yang berguna

| Server | Memberi agent kemampuan |
| --- | --- |
| `@modelcontextprotocol/server-github` | Issue, pull request, pencarian kode |
| `@modelcontextprotocol/server-filesystem` | Akses di luar akar workspace |
| `mcp-server-postgres` | Inspeksi skema dan query |
| `@modelcontextprotocol/server-puppeteer` | Otomasi peramban |
| `@modelcontextprotocol/server-slack` | Membaca dan mengirim ke Slack |

## Membuat server sendiri

SDK .NET-nya adalah paket yang sama yang dipakai Auto Code sebagai klien:

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
    [McpServerTool, Description("Mengembalikan versi yang sedang ter-deploy di sebuah environment.")]
    public static string GetDeployedVersion(
        [Description("Nama environment: staging atau production")] string environment) =>
        environment switch
        {
            "staging" => "1.4.2",
            "production" => "1.3.9",
            _ => $"Environment '{environment}' tidak dikenal.",
        };
}
```

Lalu arahkan Auto Code ke sana:

```jsonc
{
  "mcpServers": {
    "deploy": { "command": "dotnet", "args": ["run", "--project", "./tools/my-mcp-server"] }
  }
}
```

Tulis deskripsi tool seperti Anda menulis dokumentasi untuk rekan kerja yang tidak bisa bertanya
lanjutan. Deskripsi itulah keseluruhan antarmuka sejauh yang diketahui model.
