# Pemecahan masalah

[English](../en/troubleshooting.md) · [Bahasa Indonesia](../id/troubleshooting.md)

| Gejala | Penyebab dan solusi |
|---|---|
| Bot menjawab "I'm running in offline mock mode" | Belum ada provider model. Tambahkan di **Pengaturan** atau lewat konfigurasi ([memulai](getting-started.md#2-hubungkan-model)). |
| `HTTP 401` dari provider | API key salah, atau endpoint Azure dipakai dengan kind `openai`. Azure memerlukan kind `azure-openai`. |
| `HTTP 404` dari Azure | Nama model/deployment di profil tidak ada pada resource tersebut. |
| Tugas tertahan di **WaitingForHuman** | Ada persetujuan yang menunggu. Buka **Persetujuan** (lencana di menu) atau jalankan `marbots approvals`. |
| "Denied by policy" pada hasil tool | Profil izin bot melarang kategori tersebut. Ubah bot dan pilih profil lain, atau serahkan pekerjaan ke bot lain. |
| Server MCP berstatus *error* | Buka halaman MCP, klik **Test & list tools**, lalu baca pesannya. Pastikan `node`/`npx` atau `uv`/`uvx` ada di PATH. Saat pertama dijalankan, paket diunduh dan bisa memakan waktu sekitar satu menit. Beberapa server komunitas rusak pada pustaka MCP yang lebih baru; kunci versinya di argumen. |
| Pencarian web tanpa hasil | DuckDuckGo mungkin membatasi permintaan. Tambahkan secret `TAVILY_API_KEY` di Pengaturan. |
| UI tampil tanpa gaya saat dijalankan dari source | Jalankan dengan `dotnet run --project src/Marbots.Server` (static web assets diaktifkan otomatis). |
| Tugas berstatus *Interrupted by a platform restart* | Server berhenti saat tugas berjalan. Gunakan **Retry** di halaman Tugas. |
| Utas menjadi lambat atau mahal | Ketik `/compact`, turunkan ambang pemadatan bot, atau gunakan **Reset konteks**. |
| Ingin memulai dari nol | Hentikan server dan hapus folder data (`src/Marbots.Server/data` secara bawaan). Ini menghapus semua bot, utas, memori, dan secret. |

Log ditulis ke konsol. Naikkan detailnya dengan `Logging:LogLevel:Marbots=Debug`.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
