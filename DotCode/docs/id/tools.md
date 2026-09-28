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

## Subagent

Tool `Agent` menjalankan sesi anak dengan jendela konteks, system prompt, daftar tool, dan model sendiri. Progresnya tampil bertingkat di bawah pemanggilan, dan hanya laporan akhirnya yang kembali ke percakapan utama. Tipe bawaan: `general-purpose`, `Explore` (baca-saja, model cepat), `Plan` (baca-saja, model planner).

## Manajemen konteks

Saat percakapan mendekati ambang auto-compact (85% dari jendela konteks), DotCode meringkas riwayat menjadi pesan lanjutan yang terstruktur (permintaan, file, error, tugas tertunda, langkah berikutnya, dan daftar tugas) lalu melanjutkan. `/compact` melakukannya sesuai permintaan; `/context` menampilkan rinciannya.
