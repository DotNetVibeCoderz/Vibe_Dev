# Ekstensi VS Code

> 🇬🇧 [English](../en/vscode.md)

Ekstensi DotCode membawa agen ke dalam VS Code. Ekstensi ini adalah klien tipis untuk `dotcode serve`, dibangun di atas SDK TypeScript. Engine, tool, aturan izin, hook, server MCP, skill, sandbox, dan pengaturan LSP-nya sama persis dengan aplikasi terminal, begitu juga provider modelnya.

![Chat DotCode di VS Code](../images/vscode-chat.png)

## Instalasi

1. Pasang CLI (lihat [Memulai](memulai.md#1-instalasi)) dan konfigurasikan provider model.
2. Pasang ekstensi dari berkas rilis `dotcode-vscode.vsix`:

   ```bash
   code --install-extension dotcode-vscode.vsix
   ```

   Atau build sendiri: `cd ide/vscode && npm ci && npm run package`.

Ekstensi mencari CLI di `dotcode.cliPath`, lalu `DOTCODE_CLI_PATH`, `PATH`, dan folder default installer. Bila tidak ditemukan, ekstensi menawarkan untuk menjalankan installer di terminal.

## Pemakaian

| Tempat | Fungsi |
|---|---|
| Activity bar → **DotCode** (atau `Ctrl/Cmd+Esc`) | Chat. Jawaban mengalir sebagai markdown; pemanggilan tool menampilkan status langsung, output, diff berwarna, dan tautan *Open file*; daftar todo; pemakaian konteks dan biaya di header |
| Kartu izin | **Yes**, **Yes, don't ask again** (menyimpan aturan yang disarankan; untuk edit berarti menerima edit selama sesi), **No**, dengan umpan balik opsional untuk model. Edit yang diusulkan juga dibuka lebih dulu di **diff editor**. Permintaan yang sama tampil sebagai notifikasi bila chat tersembunyi |
| Seleksi di editor → `Ctrl/Cmd+Alt+K` atau menu konteks **Ask DotCode about Selection** | Memasukkan kode beserta path dan rentang barisnya ke prompt |
| Status bar | Model dan mode izin; klik untuk membuka chat |
| Perintah (`DotCode: …`) | Open Chat, New Session, Ask about Selection, Stop, Select Model, Set Permission Mode, Open DotCode in Terminal (TUI lengkap di terminal terintegrasi) |

Pertanyaan dari agen (AskUserQuestion) tampil sebagai quick pick. Di plan mode, rencana yang diusulkan dibuka sebagai dokumen markdown dengan pilihan **Approve** / **Keep planning**. `Enter` mengirim prompt, `Shift+Enter` menambah baris baru, dan `Esc` menghentikan turn yang sedang berjalan.

## Pengaturan

| Pengaturan | Default | Keterangan |
|---|---|---|
| `dotcode.cliPath` | *(otomatis)* | Path ke `dotcode` atau `dotcode.dll` |
| `dotcode.model` | *(pengaturan DotCode Anda)* | `provider:model`, alias, atau role |
| `dotcode.permissionMode` | `default` | `default`, `acceptEdits`, `auto`, atau `plan` untuk sesi baru |
| `dotcode.showDiffOnPermission` | `true` | Buka edit yang diusulkan di diff editor |
| `dotcode.settings` | `{}` | Pengaturan DotCode inline (mis. blok `providers` untuk endpoint khusus workspace) |

Sesi berjalan di folder workspace milik editor aktif (atau folder pertama), sehingga pengaturan proyek `.dotcode/` / `.claude/`, `DOTCODE.md`, dan `.mcp.json` ikut berlaku.

## Pengembangan

```bash
cd ide/vscode
npm ci
npm run build          # membundel src/ (dan SDK) ke dist/extension.js dengan esbuild
npm test               # menjalankan VS Code dengan ekstensi dan suite integrasi terhadap `dotcode serve`
                       # sungguhan (model scripted offline; perlu `dotnet build` di DotCode/)
npm run package        # dotcode-vscode.vsix
```

`demo/index.html` merender webview chat asli dengan percakapan scripted (dipakai untuk screenshot di atas). CI menjalankan test integrasi di bawah `xvfb` pada setiap push dan melampirkan VSIX ke rilis.
