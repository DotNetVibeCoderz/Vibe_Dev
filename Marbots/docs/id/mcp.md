# Server MCP

[English](../en/mcp.md) · [Bahasa Indonesia](../id/mcp.md)

Marbots menyertakan klien Model Context Protocol (MCP) untuk server **stdio** (proses lokal) dan server **streamable
HTTP**. Tool MCP terlihat oleh bot sebagai `mcp__<server>__<tool>` dan melewati mesin kebijakan, persetujuan, dan
event audit yang sama dengan tool bawaan.

![Galeri MCP](../images/mcp.png)

## Galeri

| Server | Transport | Terpasang bawaan |
|---|---|---|
| Filesystem (`@modelcontextprotocol/server-filesystem`) | npx | ✅ (terbatas pada workspace utas) |
| Sequential Thinking | npx | ✅ |
| Knowledge Graph Memory | npx | |
| Everything (server uji) | npx | |
| Playwright Browser (`@playwright/mcp`) | npx | |
| GitHub (butuh secret `GITHUB_TOKEN`) | npx | |
| Fetch, Time, Git | uvx | |
| Context7 docs | npx | |

1. **Install** server di galeri MCP.
2. **Test & list tools** menjalankannya dan menampilkan daftar tool. Saat pertama dijalankan, `npx`/`uvx` mungkin
   mengunduh paket terlebih dahulu.
3. Aktifkan pada bot (Tim → Ubah → MCP servers).

## Server yang terikat workspace

Jika argumen server mengandung `{workspace}`, Marbots menjalankan **satu proses per workspace proyek** dan mengganti
placeholder dengan path folder. Akibatnya server Filesystem hanya bisa menyentuh berkas milik utas yang dilayaninya.

## Server kustom

**Add custom server** menerima:

- *stdio*: perintah, argumen (satu per baris, boleh `{workspace}`), variabel lingkungan. Gunakan `secret:NAME`
  untuk merujuk secret tersimpan alih-alih menempel nilainya.
- *HTTP*: URL endpoint (respons JSON atau SSE; `Mcp-Session-Id` ditangani otomatis).
- *Kategori izin*: read-only, tulis workspace, jaringan, eksekusi proses (bertanya), atau komunikasi eksternal
  (bertanya). Ini menentukan cara mesin kebijakan memperlakukan semua tool dari server tersebut.

Di Windows, *shim* `npx`/`uvx` otomatis dijalankan melalui `cmd.exe`.

## Contoh

```
Boss Man › Buat "Mira" dari templat data-engineer dengan server MCP filesystem dan time,
           lalu minta Mira menulis reports/world-clock.md berisi jam di Jakarta dan Tokyo menggunakan tool MCP.
```

Mira memanggil `mcp__time__get_current_time` dua kali dan `mcp__filesystem__write_file` sekali. Lihat [uji coba](trials.md).

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
