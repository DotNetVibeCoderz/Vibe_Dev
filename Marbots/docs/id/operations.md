# Operasional: telemetri, notifikasi push, rilis, dan performa

[English](../en/operations.md) · [Bahasa Indonesia](../id/operations.md)

## OpenTelemetry

Marbots memancarkan trace dan metrik lewat `System.Diagnostics` (nama source dan meter `Marbots`) dan mengekspornya
lewat OTLP ke collector mana pun: Grafana/Tempo, Jaeger, Honeycomb, Azure Monitor, Datadog, dashboard Aspire, dan
lainnya.

```json
"Marbots": {
  "Telemetry": { "OtlpEndpoint": "http://otel-collector:4317", "Protocol": "grpc", "ServiceName": "marbots", "HeadersSecret": "OTLP_HEADERS" }
}
```

`OTEL_EXPORTER_OTLP_ENDPOINT` juga berfungsi. Tidak ada yang diekspor tanpa endpoint. Dengan `http/protobuf`, path per
sinyal (`/v1/traces`, `/v1/metrics`, `/v1/logs`) ditambahkan otomatis.

**Span** mengikuti konvensi GenAI OpenTelemetry:

| Span | Atribut |
|---|---|
| `invoke_agent <bot>` | `gen_ai.agent.id/name`, `marbots.task.id`, `marbots.thread.id`, `marbots.task.depth`, `marbots.tenant`, total token, langkah |
| `chat <model>` | `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.usage.input_tokens/output_tokens` |
| `execute_tool <nama>` | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `marbots.tool.pack`, `marbots.outcome` |
| `delegate <manajer>` | `marbots.delegation.count`, `marbots.delegation.bots` |

Span `invoke_agent` milik tugas yang didelegasikan berada di bawah span `delegate` manajernya, sehingga satu trace
menampilkan seluruh pohon delegasi. Span ASP.NET Core dan HttpClient ikut disertakan, sehingga panggilan model tampil
sebagai span klien HTTP.

**Metrik**:

| Metrik | Arti |
|---|---|
| `gen_ai.client.token.usage` | Token per panggilan model (`gen_ai.token.type` = input/output) |
| `gen_ai.client.operation.duration` | Latensi panggilan model |
| `marbots.tool.calls`, `marbots.tool.duration` | Panggilan tool per tool dan hasil |
| `marbots.tasks`, `marbots.task.duration` | Tugas selesai per status |
| `marbots.delegations` | Sub-tugas yang didelegasikan |
| `marbots.cost` | Perkiraan biaya model (USD) |

Log juga dikirim lewat OTLP, lengkap dengan scope.

## Notifikasi push

Pengguna mendapat notifikasi saat bot menunggu persetujuan (mendesak), atau saat chat yang mereka mulai selesai atau
gagal, bahkan ketika aplikasi tertutup.

| Platform | Penyiapan |
|---|---|
| **ntfy** (tanpa akun) | Tidak ada yang perlu disiapkan di server. **Settings → When the app is closed** di aplikasi mobile mendaftarkan topik acak pribadi; berlanggananlah di aplikasi ntfy. Server ntfy sendiri: `Push:NtfyServer`, secret token `NTFY_TOKEN`. |
| **Firebase Cloud Messaging** (Android, web) | Secret `FCM_SERVICE_ACCOUNT` = JSON service account Firebase. Marbots memakai API HTTP v1 dengan token OAuth JWT-bearer. |
| **Apple Push Notification service** | Secret `APNS_AUTH_KEY` = kunci `.p8`; `Push:ApnsKeyId`, `Push:ApnsTeamId`, `Push:ApnsBundleId` (dan `ApnsProduction`). |

Perangkat didaftarkan lewat `POST /api/v1/push/devices` dengan `{ platform, token, name, topics }` (peran Operator), atau
`client.Push.RegisterAsync(…)` di SDK .NET. Topik yang tersedia: `approvals`, `completed`, dan `failed`. Token yang
dilaporkan sudah tidak berlaku oleh FCM atau APNs akan dihapus. Isi `Push:PublicUrl` agar notifikasi membuka halaman
yang tepat.

## Rilis dan rantai pasok

- Mendorong tag `marbots-v<versi>` menjalankan CI, lalu menerbitkan paket NuGet dan melampirkan hal berikut ke rilis
  GitHub:
  - binary `marbots-host` untuk win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, dan osx-arm64;
  - **SBOM CycloneDX** untuk solusi .NET, SDK TypeScript, dan SDK Python;
  - `SHA256SUMS`.
- Paket skill dapat ditandatangani (lihat [Skill](skills.md#paket-skill-bertanda-tangan)).

## Performa

Diukur di laptop (Core i7-8650U, 4 core), .NET 10 Release, sebelum dan sesudah penyetelan 0.3:

| Jalur | Sebelum | Sesudah | Yang berubah |
|---|---|---|---|
| `grep` literal langka di 104 MB / 3.000 file | 1.323 ms | 183 ms | Prafilter byte UTF-8, pra-cek seluruh file, potongan paralel berurutan |
| Pencarian memori hybrid, 5.000 memori | 284 ms | 85 ms | Hanya memindai id + vektor, cosine SIMD, indeks `(tenant, owner, created_at)` |
| Embedding hashing memori 2 KB | 0,46 ms | 0,40 ms | — |
| BM25 atas 2.000 kandidat | 24 ms | 21 ms | — |
| Estimasi token, 400 pesan | 0,02 ms | 0,02 ms | — |

Kedua biaya yang tersisa sudah mendekati batas I/O: membaca file yang sama sebagai byte memakan 365 ms secara serial,
dan membaca 5.000 vektor dari SQLite memakan 45 ms. Satu panggilan model memakan 1–10 detik. **Keputusan (ADR-011):
tidak memakai library Rust native.** Profiling menemukan biaya algoritmik, dan C# sudah memperbaikinya. Tinjau ulang
bila profil memperlihatkan kerja CPU di atas 100 ms per langkah agen setelah perbaikan ini.

---
*Marbots: Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
