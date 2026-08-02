# Arsitektur

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

## Proyek

| Proyek | Tanggung jawab |
| --- | --- |
| `AutoCode.Core` | Agent loop, konfigurasi, izin, hook, skill, subagent, sesi, konteks, biaya, indeks semantik |
| `AutoCode.Providers` | Satu-satunya kode yang mengenal vendor berdasarkan nama |
| `AutoCode.Providers.Onnx` | Embedding offline. Dipisah karena ONNX Runtime membawa pustaka native yang jarang diperlukan |
| `AutoCode.Tools` | Tool bawaan |
| `AutoCode.Mcp` | Klien MCP dan adaptasi tool |
| `AutoCode.Cli` | Antarmuka terminal, slash command, entry point |
| `AutoCode.Tests` | Suite xUnit |

Ketergantungan mengalir satu arah: `Cli → {Core, Providers, Tools, Mcp} → Core`. Tidak ada isi `Core`
yang mengetahui Spectre.Console maupun vendor mana pun.

## Loop-nya

`AgentLoop.RunTurnAsync` adalah pusat sistem ini.

```
pesan pengguna
     │
     ▼
hook UserPromptSubmit ──── diblokir? ──▶ akhiri giliran
     │
     ▼
┌──▶ susun permintaan (prompt sistem + transkrip + deklarasi tool)
│    │
│    ▼
│  streaming dari IChatClient ──▶ pancarkan peristiwa teks / penalaran / penggunaan
│    │
│    ▼
│  ada pemanggilan tool?  ── tidak ──▶ akhiri giliran
│    │ ya
│    ▼
│  untuk setiap pemanggilan:
│    hook PreToolUse   ── veto  ──▶ hasil: "blocked by hook"
│    pemeriksaan izin  ── tolak ──▶ hasil: "permission denied"
│                      ── tanya ──▶ tanyakan ke pengguna
│    jalankan tool
│    hook PostToolUse (boleh menyisipkan konteks)
│    │
│    ▼
└── tambahkan hasilnya, ulangi
```

Dua keputusan membentuk semua hal di bawahnya.

**Eksekusi tool diatur secara manual**, bukan diserahkan ke pemanggilan fungsi otomatis milik chat
client. Setiap pemanggilan harus melewati tiga gerbang terlebih dahulu — hook siklus hidup, mesin
izin, dan renderer yang menunjukkan kepada pengguna apa yang akan terjadi. Pemanggilan otomatis akan
melewati ketiganya.

**Penolakan adalah hasil tool, bukan exception.** Model diberi tahu bahwa ia ditolak dan alasannya,
lalu bisa menyesuaikan diri. Agent yang macet saat ditolak adalah agent yang harus Anda mulai ulang.

### Paralelisme

Tool yang hanya membaca dalam satu kelompok berjalan bersamaan; apa pun yang mengubah keadaan
berjalan berurutan, sehingga pengguna melihat urutan peristiwa yang koheren. Hasilnya kemudian
diurutkan ulang mengikuti urutan pemanggilan model, karena beberapa provider memeriksa hal itu.

### Pemadatan

Ketika `LastContextTokens / contextWindow` melampaui `compactionThreshold` (bawaan `0.82`), separuh
transkrip yang lebih lama diganti dengan ringkasan tertulis yang dibuat oleh `smallModel`. Bagian
akhir yang terkini disimpan apa adanya — itulah yang sedang aktif dikerjakan agent.

Kontraknya adalah **padatkan sebelum request berikutnya**, bukan padatkan seketika. Pemakaian token
baru diketahui setelah sebuah respons melaporkannya, sehingga pemeriksaan berjalan di awal tiap
iterasi loop berdasarkan biaya request sebelumnya. Praktiknya, pemadatan terjadi di tengah giliran
sebelum putaran tool berikutnya, atau di awal giliran selanjutnya — selalu sebelum request yang akan
melampaui batas.

`LastContextTokens` dihitung per request, bukan kumulatif: ia mengukur seberapa penuh jendela pada
pemanggilan terakhir, dan itulah besaran yang relevan. Sesi dengan *total* token besar tetapi
percakapan pendek sebenarnya tidak mendekati batas.

Titik pemisahnya tidak pernah boleh memisahkan hasil tool dari pemanggilan yang memintanya.
`FunctionResultContent` tanpa pasangan adalah galat API keras di semua provider.

**Keterbatasan yang diketahui.** `ContextCompactor` menyimpan delapan pesan terakhir apa adanya,
sehingga percakapan sekitar sepuluh pesan atau kurang tidak bisa dipadatkan sama sekali. Transkrip
pendek yang terdiri dari beberapa pesan raksasa — misalnya satu `Read` atas berkas yang sangat besar
— karenanya memenuhi jendela tanpa ada yang aman untuk dilipat. Auto Code mendeteksi kondisi ini dan
memunculkan peringatan sekali, alih-alih membiarkannya menjadi galat panjang konteks yang tidak bisa
dijelaskan pengguna. Solusinya `/clear` atau membaca potongan yang lebih kecil.

## Provider

`ProviderFactory` memetakan `ProviderProfile` menjadi `IChatClient`. Hanya di sinilah nama vendor
disebut.

- **OpenAI-compatible** melalui `Microsoft.Extensions.AI.OpenAI`.
- **Anthropic** dan **Gemini** diimplementasikan langsung terhadap format wire-nya.

Kedua yang terakhir ditulis manual dengan alasan jelas. Anthropic mengharuskan blok thinking dikirim
ulang beserta signature-nya pada tiap putaran pemanggilan tool; Gemini memerlukan function call
diberi id sintetis dan JSON Schema-nya disederhanakan ke subset OpenAPI yang ia terima. Adapter
generik menghilangkan kedua detail itu, dan kegagalan yang muncul sulit ditelusuri.

`ProviderIntegrationTests` menguji keduanya terhadap server HTTP lokal sungguhan, dengan pemeriksaan
pada byte persis yang dikirim.

## Izin

```
deny  →  mode  →  allow  →  ask
```

Deny menang atas segalanya, termasuk `bypassPermissions`. Kemampuan yang hanya membaca langsung
diizinkan bahkan sebelum mode diperiksa.

Aturan berbentuk `Tool` atau `Tool(pola)`, dicocokkan dengan subjek yang diekstrak dari argumen —
perintah untuk tool shell, jalur untuk tool berkas, URL untuk tool jaringan.

`ToolExecutor` mengimplementasikan ketiga gerbang itu satu kali. `AgentLoop` dan subagent sama-sama
memakainya, dan itulah yang membuat "subagent melewati permintaan persetujuan" mustahil secara
struktural, bukan sekadar belum teruji.

## Subagent

Dibangun di atas `ChatClientAgent` milik Microsoft Agent Framework. Masing-masing memperoleh sesi,
prompt sistem, dan kumpulan tool tersaringnya sendiri. Pemanggilan tool dibungkus dalam
`GatedToolFunction`, yang mengalirkannya ke `ToolExecutor` yang sama dengan loop utama.

Penyarangan dimatikan — subagent yang bisa memunculkan subagent mengubah proses terbatas menjadi tak
terbatas pada kedalaman yang tidak disetujui siapa pun.

## Embedding

Embedding diselesaikan terpisah dari chat, melalui `EmbeddingOptions.Resolve`. Pemisahan ini ada
karena keduanya memang keputusan yang berbeda: Anthropic tidak punya endpoint embedding, sehingga
setelan `Auto` pada profil Claude menjadi `None` alih-alih mematikan indeks diam-diam tanpa
penjelasan apa pun.

Backend-nya: `Ollama` (`/api/embed` native lewat OllamaSharp — `OllamaApiClient` sudah
mengimplementasikan `IEmbeddingGenerator`, jadi ini pemakaian langsung, bukan adapter),
`OpenAICompatible`, dan `Onnx`.

Kasus ONNX dirangkai di CLI, bukan di `EmbeddingFactory`, agar dependensi native ONNX Runtime tetap
terkurung di composition root. Apa pun yang memakai `AutoCode.Core` bisa meninggalkan proyek itu.

`OnnxEmbeddingGenerator` melakukan mean pooling atas token state dengan attention mask lalu
menormalkan L2. Keduanya penting: memakai `[CLS]` alih-alih pooling menghasilkan vektor yang jauh
lebih lemah, dan tidak memask padding membuat embedding hasil batch berbeda dari teks yang sama bila
di-embed sendiri — keduanya tidak gagal terang-terangan, hanya diam-diam memberi hasil lebih buruk.

Lebar vektor dipelajari dari batch pertama. Dimensi yang salah tulis adalah kegagalan diam-diam,
karena semua perbandingan kemiripan lalu dilewati dan indeks tidak mengembalikan apa pun; nilai yang
dikonfigurasi tetap disimpan sebagai penegasan yang gagal terang-terangan.

## Indeks semantik

`Microsoft.Extensions.VectorData` menyediakan kontraknya; Auto Code mengimplementasikan sendiri
`VectorStoreCollection<string, CodeChunk>` alih-alih menambah dependensi connector.

Dua alasan. Indeks kode itu sekali pakai — lebih murah dibangun ulang daripada mengelola basis data
berdampingan dengan sebuah CLI. Dan yang menentukan, connector in-memory yang tersedia mengunci versi
Abstractions yang lebih lama daripada yang dibutuhkan Microsoft Agent Framework; keduanya tidak bisa
hidup berdampingan. Dengan mengimplementasikan kontraknya, sambungannya tetap utuh, sehingga beralih
ke Qdrant atau Postgres kelak hanya perlu satu baris perubahan di sisi pemanggil.

Berkas dipecah pada batas baris kosong dengan tumpang tindih, yang jauh lebih sering menjaga sebuah
fungsi dan tanda tangannya berada dalam satu potongan dibanding jendela berukuran tetap.

## Konfigurasi

Tujuh lapisan, dari terendah ke tertinggi: preset, `app.config`, settings pengguna, settings proyek,
settings lokal, environment variable `AUTOCODE_*`, argumen baris perintah. Dibangun di atas
`Microsoft.Extensions.Configuration`, sehingga aturan binding standar berlaku.

Indireksi `env:NAMA` dibaca saat diperlukan, bukan saat pemuatan, sehingga key yang dirotasi langsung
berlaku tanpa memulai ulang.

## Sesi

Transkrip JSON di bawah `~/.autocode/sessions/<workspace>-<hash>/`, dipisahkan per workspace sehingga
`--continue` di satu repositori tidak akan pernah melanjutkan percakapan dari repositori lain.

Serialisasi memakai `AIJsonUtilities.DefaultOptions` demi konverter polimorfik `AIContent`-nya —
tanpa itu, transkrip yang berisi pemanggilan tool akan kembali sebagai pesan kosong. Penulisan
memakai pola tulis-lalu-pindah, sehingga kegagalan di tengah penulisan tidak menyisakan transkrip
terpotong.

## Terminal

`IAgentUserInterface` adalah yang memungkinkan satu loop menggerakkan tiga permukaan: REPL
Spectre.Console, eksekusi headless `--print`, dan aliran peristiwa `stream-json`. `Core` tidak punya
dependensi konsol.

Teks asisten ditulis sambil mengalir, bukan ditahan lalu digambar ulang — respons yang muncul
kata demi kata terasa responsif, sedangkan yang muncul sekaligus terasa seperti menggantung.

## Titik perluasan

| Titik | Kontrak |
| --- | --- |
| Tool | `IAgentTool` |
| Provider | `IChatClient` melalui `ProviderFactory` |
| Permukaan UI | `IAgentUserInterface` |
| Vector store | `VectorStoreCollection<string, CodeChunk>` |
| Kemampuan luar proses | Server MCP |

Tidak ada API plugin terkompilasi, dan itu disengaja. Memuat assembly sembarangan ke dalam proses
agent akan memungkinkan plugin melewati mesin izin, dan batas itu layak dijaga mutlak.
