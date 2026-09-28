# Ekstensi: skills, commands, subagent, hooks, plugin, MCP

> 🇬🇧 [English](../en/extensions.md)

DotCode membaca tata letaknya sendiri (`.dotcode/`, `~/.dotcode/`) sekaligus milik Claude Code (`.claude/`, `~/.claude/`), sehingga skill, command, dan agent yang sudah ada tetap berfungsi. Definisi proyek menimpa milik pengguna.

## Skills

Skill adalah folder berisi `SKILL.md`. Model melihat nama dan deskripsi setiap skill di tool `Skill` dan hanya memuat instruksi lengkapnya bila tugas cocok. Pengguna juga bisa memanggil langsung dengan `/nama-skill [argumen]`.

```
.dotcode/skills/csv-insights/
├── SKILL.md
└── analyze.py
```

```markdown
---
name: csv-insights
description: Analisis file CSV dan buat laporan insight Markdown. Gunakan bila pengguna meminta analisis data CSV.
---
1. Jalankan `python "{baseDir}/analyze.py" <file.csv>` …
2. Tulis `<nama>-report.md` berisi Ringkasan, Angka kunci, Insight, Rekomendasi.
```

`{baseDir}` (atau `${DOTCODE_PLUGIN_ROOT}` / `${CLAUDE_PLUGIN_ROOT}`) diganti dengan path folder skill.

![Skill beraksi](../images/skill-csv-report.png)

## Slash command kustom

File markdown di `.dotcode/commands/` (proyek) atau `~/.dotcode/commands/` (pengguna). Subfolder menjadi namespace (`commands/frontend/lint.md` → `/frontend:lint`).

```markdown
---
description: Buat entri CHANGELOG
argument-hint: [sejak-ref]
allowed-tools: Bash(git log:*)
---
Commit terbaru:
!`git log --oneline -n 20`

Buat entri CHANGELOG untuk perubahan sejak $ARGUMENTS.
```

`$ARGUMENTS` dan `$1…$9` disubstitusi. `` !`cmd` `` menjalankan perintah shell (hanya bila diizinkan `allowed-tools`) dan menyisipkan outputnya.

## Subagent

`.dotcode/agents/<nama>.md`:

```markdown
---
name: code-reviewer
description: Reviewer kode ahli. Gunakan setelah menulis atau mengubah kode.
tools: Read, Grep, Glob, Bash
model: inherit          # inherit | fast | planner | subagent | provider:model
isolation: worktree     # opsional: bekerja di git worktree sendiri
---
Anda adalah reviewer kode senior. …
```

Dengan `isolation: worktree` (atau input tool `"isolation": "worktree"`) subagent mendapat git worktree baru di branch sendiri — lihat [Git worktree](worktree.md).

## Hooks

Perintah shell yang berjalan pada event agen, dengan format Claude Code:

```json
{
  "hooks": {
    "PostToolUse": [ { "matcher": "Edit|Write", "hooks": [ { "type": "command", "command": "dotnet format", "timeout": 60 } ] } ],
    "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "echo \"Branch: $(git branch --show-current)\"" } ] } ]
  }
}
```

| Event | Kapan | Bisa |
|---|---|---|
| `PreToolUse` | Sebelum tool berjalan | blokir, allow/deny/ask, ubah input |
| `PostToolUse` | Setelah tool berjalan | tambah umpan balik/konteks |
| `UserPromptSubmit` | Sebelum prompt dikirim | blokir, tambah konteks |
| `Stop` / `SubagentStop` | Saat agen hendak berhenti | blokir dengan alasan → agen lanjut |
| `SessionStart` / `SessionEnd` | Mulai/lanjut, keluar | tambah konteks |
| `Notification` | Dialog izin muncul | notifikasi |
| `PreCompact` | Sebelum kompaksi | — |

**Kontrak:** hook menerima JSON di stdin (`session_id`, `cwd`, `hook_event_name`, `tool_name`, `tool_input`, `tool_response`, `prompt` …). Keluar `0` = sukses (stdout boleh JSON keputusan), keluar `2` = blokir dengan stderr sebagai alasan, kode lain = error non-blokir. Di Windows hook dijalankan dengan Git Bash.

## Output style

`default`, `explanatory` (menambah catatan "★ Insight"), `learning` (meminta Anda menulis bagian kecil kode, `TODO(human)`), atau kustom `.dotcode/output-styles/<nama>.md`.

## Plugin

Plugin membundel commands, agents, skills, hooks, dan server MCP (kompatibel dengan plugin Claude Code):

```
plugin-saya/
├── .dotcode-plugin/plugin.json     # atau .claude-plugin/plugin.json
├── commands/*.md                   # → /plugin-saya:command
├── agents/*.md
├── skills/<nama>/SKILL.md
├── hooks/hooks.json
└── .mcp.json
```

```bash
dotcode plugin install ./plugin-saya
dotcode plugin install https://github.com/org/plugin.git
dotcode plugin marketplace add ./samples/marketplace
dotcode plugin install gravicode-toolkit@gravicode
dotcode plugin list | enable <nama> | disable <nama> | remove <nama>
```

Contoh plugin di [`samples/plugins/gravicode-toolkit`](../../samples/plugins/gravicode-toolkit): `/gravicode-toolkit:changelog`, `/gravicode-toolkit:explain`, subagent `code-reviewer`, skill `readme-writer`, dan hook edit-log.

![Command plugin](../images/plugin-command.png)

## Server MCP

DotCode adalah klien Model Context Protocol (stdio dan Streamable HTTP). Tool tampil sebagai `mcp__<server>__<tool>`, prompt sebagai `/mcp__<server>__<prompt>`.

```bash
dotcode mcp add notes node samples/mcp-server-notes/server.mjs
dotcode mcp add --scope project github --transport http https://api.githubcopilot.com/mcp/ -H "Authorization: Bearer $GH_TOKEN"
dotcode mcp list
dotcode mcp get notes
dotcode mcp remove notes
```

![Server MCP notes](../images/mcp-notes.png)
