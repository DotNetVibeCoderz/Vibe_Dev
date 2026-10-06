# Memulai

[English](../en/getting-started.md) · [Bahasa Indonesia](../id/getting-started.md)

Panduan ini membawa Anda dari *clone* repositori hingga tugas multi-bot pertama dalam sekitar sepuluh menit.

## Prasyarat

| Kebutuhan | Versi | Kegunaan |
|---|---|---|
| .NET SDK | 10.0 atau lebih baru | Server, CLI, SDK .NET |
| Endpoint LLM | Azure OpenAI, OpenAI, DeepSeek, Ollama, LM Studio, vLLM… | "Otak" para bot. Tanpa ini, bot berjalan dalam mode *mock* offline. |
| Node.js (opsional) | 18+ | Server MCP yang dijalankan dengan `npx` |
| uv (opsional) | bebas | Server MCP Python yang dijalankan dengan `uvx` |
| Python (opsional) | 3.9+ | Agar bot dapat menjalankan skrip Python; SDK Python |

## 1. Jalankan server

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_Dev.git
cd Vibe_Dev/Marbots
dotnet run --project src/Marbots.Server
```

Buka **http://localhost:5170**. Saat pertama kali berjalan, Marbots membuat folder data (`src/Marbots.Server/data`),
mengisi 58 templat bot dan 20 skill, membuat **Boss Man**, lalu merekrut tim awal:
Atlas (peneliti), Alice (software engineer), Quinn (QA), dan Wren (penulis teknis).

## 2. Hubungkan model

Pilih salah satu cara.

**A. Lewat UI.** Buka **Pengaturan → Model provider**, pilih *Azure OpenAI* atau *OpenAI-compatible*, lalu isi
endpoint, API key, dan model (misalnya `gpt-5-mini`). Key dienkripsi di komputer ini dan tidak pernah ditampilkan lagi.

**B. Lewat konfigurasi** (disarankan untuk server). Gunakan variabel lingkungan atau `appsettings.json`:

```bash
# Azure OpenAI
export Marbots__Providers__0__Name=azure
export Marbots__Providers__0__Kind=azure-openai
export Marbots__Providers__0__Endpoint=https://<resource>.openai.azure.com/
export Marbots__Providers__0__ApiKey=<key>
export Marbots__ModelProfiles__0__Name=default
export Marbots__ModelProfiles__0__Provider=azure
export Marbots__ModelProfiles__0__Model=gpt-5-mini
```

```bash
# Endpoint apa pun yang kompatibel dengan OpenAI, misalnya DeepSeek atau Ollama lokal
export Marbots__Providers__0__Name=deepseek
export Marbots__Providers__0__Kind=openai
export Marbots__Providers__0__Endpoint=https://api.deepseek.com
export Marbots__Providers__0__ApiKey=<key>
export Marbots__ModelProfiles__0__Name=default
export Marbots__ModelProfiles__0__Provider=deepseek
export Marbots__ModelProfiles__0__Model=deepseek-chat
```

Di Windows PowerShell gunakan `$env:Marbots__Providers__0__Name = "azure"` dan seterusnya.

Opsional: tambahkan secret `TAVILY_API_KEY` (Pengaturan → Secrets) untuk pencarian web yang lebih baik. Tanpa itu,
bot memakai endpoint HTML DuckDuckGo.

## 3. Beri Boss Man sebuah tujuan

Di **Obrolan**, Boss Man sudah terpilih. Coba salah satu saran, misalnya:

> Riset 3 kompetitor aplikasi kasir untuk UMKM di Indonesia, lalu minta tim membuat landing page HTML sederhana untuk produk kita.

Boss Man memanggil `list_bots`, membagi pekerjaan dengan `delegate_tasks`, dan anggota tim bekerja paralel dalam satu
*workspace* proyek bersama. Pantau panel **Aktivitas langsung** atau buka **Kantor** untuk melihat siapa mengerjakan apa.

![Obrolan dengan delegasi](../images/chat-delegation.png)

Saat bot ingin menjalankan perintah shell, kartu persetujuan muncul. Pilih **Setujui sekali**,
**Setujui untuk utas ini**, atau **Tolak**.

![Kartu persetujuan](../images/chat-approval.png)

Berkas yang dibuat bot tampil di **Berkas workspace** dan dapat dibuka di browser.

## 4. Gunakan CLI (opsional)

```bash
dotnet run --project src/Marbots.Cli -- status
dotnet run --project src/Marbots.Cli -- chat atlas "Ringkas berita AI hari ini dalam 5 poin"
dotnet run --project src/Marbots.Cli -- approvals
```

Atau pasang sebagai tool: `dotnet pack src/Marbots.Cli -o out && dotnet tool install -g Marbots.Cli --add-source out`.

## 5. Langkah berikutnya

- Rekrut anggota tim lain dari [galeri templat](bots-and-templates.md).
- Berikan bot [skill](skills.md) dan [server MCP](mcp.md).
- Jadwalkan pekerjaan rutin dengan [jadwal](scheduling.md).
- Otomatiskan Marbots dari kode dengan [API dan SDK](api-and-sdks.md).
- Pahami [keamanan dan persetujuan](security.md) sebelum memberi bot profil `autonomous`.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
