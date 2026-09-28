# Tool bawaan

> 🇬🇧 [English](../en/tools.md)

| Tool | Fungsi | Baca-saja | Butuh izin (mode default) |
|---|---|---|---|
| `Read` | Membaca file dengan nomor baris (offset/limit), gambar, PDF, notebook | ✔ | di luar direktori kerja / file rahasia |
| `Write` | Membuat/menimpa file (file lama harus di-Read dulu) | | ✔ |
| `Edit` | Penggantian string persis; `replace_all`; mempertahankan CRLF dan BOM | | ✔ |
| `NotebookEdit` | Ganti/sisip/hapus sel Jupyter | | ✔ |
| `Glob` | Cari file berdasarkan pola (`**/*.cs`, `{a,b}`), terbaru dulu | ✔ | |
| `Grep` | Pencarian regex via ripgrep (ada fallback), mode content/files/count | ✔ | |
| `LSP` | Kecerdasan kode dari language server: definisi, implementasi, referensi, hover, simbol dokumen/workspace, diagnostik | ✔ | di luar direktori kerja |
| `Bash` | Menjalankan bash (Git Bash di Windows); direktori kerja persisten; timeout; latar belakang | hanya perintah baca | ✔ |
| `PowerShell` | Menjalankan PowerShell (Windows PowerShell atau pwsh) | hanya perintah baca | ✔ |
| `BashOutput`, `KillShell` | Baca output / hentikan shell latar belakang | ✔ | |
| `WebFetch` | Ambil URL, ubah HTML ke markdown, jawab prompt dengan model cepat | ✔ | ✔ (per domain) |
| `WebSearch` | Pencarian web (Tavily bila ada `TAVILY_API_KEY`, fallback DuckDuckGo) | ✔ | ✔ |
| `TodoWrite` | Mengelola daftar tugas yang terlihat | ✔ | |
| `Agent` | Menjalankan subagent dengan konteks sendiri | ✔ | |
| `Skill` | Memuat instruksi skill | ✔ | |
| `AskUserQuestion` | Menanyakan 1–4 pertanyaan pilihan ganda | ✔ | |
| `ExitPlanMode` | Menyajikan rencana untuk disetujui (plan mode) | ✔ | |
| `mcp__<server>__<tool>` | Tool dari server MCP | sesuai hint server | ✔ |

## Model eksekusi

- Semua pemanggilan tool dalam satu respons divalidasi, lalu dijalankan: **pemanggilan baca-saja yang berurutan berjalan paralel** (hingga 10), sisanya berurutan; urutan hasil dipertahankan.
- Setiap pemanggilan melewati hook `PreToolUse` → mesin izin (dan dialog bila perlu) → eksekusi → hook `PostToolUse`.
- Error dikembalikan ke model sebagai hasil tool bertanda error; loop tetap berjalan agar model bisa memperbaiki.
- Hasil yang terlalu panjang dipotong (awal+akhir) dan output lengkap disimpan ke file sementara yang bisa dibaca model.
- Direktori kerja shell persisten antar pemanggilan. Timeout default 2 menit, maks 10 menit; seluruh pohon proses dihentikan saat timeout atau `Esc`.

## LSP (language server)

Tool `LSP` memberi model jawaban semantik yang presisi, bukan sekadar pencarian teks:

| Operasi | Input | Jawaban |
|---|---|---|
| `goToDefinition`, `goToImplementation` | `file_path`, `line`, `character` (mulai 1) | `path:baris:kolom` beserta baris kodenya |
| `findReferences` | sama | semua pemakaian di workspace, dengan jumlah per file |
| `hover` | sama | signature tipe dan dokumentasi |
| `documentSymbol` | `file_path` | kerangka file (class, method, field…) dengan nomor baris |
| `workspaceSymbol` | `file_path` (file apa pun dari bahasa itu), `query` | simbol yang cocok di proyek |
| `diagnostics` | `file_path` | error dan warning dari compiler / pemeriksa tipe |

**Server.** DotCode memilih server berdasarkan ekstensi file dan menyalakannya saat pertama dipakai (satu per server dan root workspace; root adalah folder terdekat yang berisi penanda seperti `tsconfig.json`, `pyproject.toml`, `go.mod`, `Cargo.toml`, atau `*.csproj`). Default bawaan dipakai bila executable-nya ada di `PATH` atau di `node_modules/.bin` proyek:

| Nama | Perintah | File | Instalasi |
|---|---|---|---|
| typescript | `typescript-language-server --stdio` | .ts .tsx .js .jsx .mjs .cjs | `npm i -g typescript typescript-language-server` (TypeScript 5.x: compiler native TypeScript 7 tidak lagi menyertakan `tsserver`) |
| python | `pyright-langserver --stdio` | .py .pyi | `npm i -g pyright` |
| go | `gopls` | .go | `go install golang.org/x/tools/gopls@latest` |
| rust | `rust-analyzer` | .rs | `rustup component add rust-analyzer` |
| csharp | `csharp-ls` | .cs | `dotnet tool install -g csharp-ls` |
| cpp | `clangd` | .c .h .cc .cpp .hpp | clangd dari LLVM |
| java | `jdtls` | .java | Eclipse JDT LS |

`dotcode doctor` menampilkan server yang ditemukan. Tambah atau ganti server di pengaturan:

```jsonc
"lsp": {
  "enabled": true,                 // false menghapus tool ini
  "diagnosticsAfterEdit": true,    // laporkan error baru setelah Edit/Write
  "servers": {
    "python": { "command": "pylsp", "args": [] },                         // ganti server bawaan
    "cpp": { "disabled": true },
    "zig": { "command": "zls", "extensions": [".zig"], "rootMarkers": ["build.zig"] }
  }
}
```

**Error setelah edit.** Bila language server sudah berjalan untuk sebuah file, `Edit` dan `Write` mengirim isi terbarunya dan menambahkan error yang kini dilaporkan server (`<new-diagnostics>`), sehingga model langsung memperbaiki error tipe tanpa menjalankan build. Server tidak pernah dinyalakan hanya untuk keperluan ini.

Catatan: permintaan pertama setelah server menyala menunggu (hingga 10 detik) sampai server selesai menganalisis file, karena sebagian server menjawab dengan hasil parsial selagi memuat proyek. Server yang crash dinyalakan ulang pada permintaan berikutnya. Perintah di `PATH` yang sebenarnya tidak bisa berjalan (misalnya proxy rustup tanpa komponennya) tetap tampil "ditemukan" di `doctor`, lalu tool melaporkan error saat menyalakannya.

## Subagent

Tool `Agent` menjalankan sesi anak dengan jendela konteks, system prompt, daftar tool, dan model sendiri. Progresnya tampil bertingkat di bawah pemanggilan, dan hanya laporan akhirnya yang kembali ke percakapan utama. Tipe bawaan: `general-purpose`, `Explore` (baca-saja, model cepat), `Plan` (baca-saja, model planner).

## Manajemen konteks

Saat percakapan mendekati ambang auto-compact (85% dari jendela konteks), DotCode meringkas riwayat menjadi pesan lanjutan yang terstruktur (permintaan, file, error, tugas tertunda, langkah berikutnya, dan daftar tugas) lalu melanjutkan. `/compact` melakukannya sesuai permintaan; `/context` menampilkan rinciannya.
