# Memulai

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

![Auto Code di terminal](../assets/autocode-cli.png)

## Instalasi

Auto Code membutuhkan [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/gravicode/autocode.git
cd autocode
dotnet build -c Release
dotnet pack -c Release
dotnet tool install --global --add-source ./src/AutoCode.Cli/nupkg Gravicode.AutoCode
```

Pastikan berhasil:

```bash
autocode --version
```

## Arahkan ke sebuah model

Auto Code tidak punya model sendiri. Berikan satu dari tiga cara berikut.

### 1. Environment variable

Variabel vendor yang lazim dikenali tanpa konfigurasi apa pun:

```bash
export OPENAI_API_KEY=sk-...
autocode
```

Yang dikenali: `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `DEEPSEEK_API_KEY`,
`DASHSCOPE_API_KEY` (Qwen), `OPENROUTER_API_KEY`, `GROQ_API_KEY`, `MISTRAL_API_KEY`, `XAI_API_KEY`,
`AZURE_OPENAI_API_KEY`.

### 2. Runtime lokal

Tanpa key, tanpa internet, tidak ada data yang meninggalkan mesin Anda:

```bash
ollama pull qwen2.5-coder:14b
autocode --provider ollama
```

`lmstudio` bekerja sama caranya pada `http://localhost:1234/v1`.

### 3. Berkas settings

```bash
autocode config init
```

Perintah itu menulis `.autocode/settings.json`. Suntinglah, dan simpan hal rahasia di
`.autocode/settings.local.json` — berkas itu memang ditujukan untuk masuk gitignore.

## Pastikan berjalan

```bash
autocode doctor
```

`doctor` memeriksa runtime, workspace, konfigurasi provider, API key, **dan** melakukan permintaan
sungguhan ke model. Hasil hijau berarti seluruh jalurnya berfungsi, bukan sekadar konfigurasinya
terbaca.

## Sesi pertama Anda

```bash
cd ~/projects/my-api
autocode
```

Lalu katakan saja apa yang Anda inginkan:

```
› kenapa OrderService mengembalikan 500 untuk keranjang kosong?
```

Auto Code akan mencari, membaca apa yang ditemukannya, lalu menjawab. Minta ia memperbaiki sesuatu
dan ia akan menyunting, lalu menjalankan build dan test Anda — dengan bertanya sebelum tiap
perubahan.

## Berikan konteks proyek

Satu hal paling berharga yang bisa Anda lakukan untuk sebuah proyek:

```
› /init
```

Perintah ini menulis `AUTOCODE.md` — perintah build, arsitektur, konvensi — yang otomatis dibaca
setiap sesi berikutnya. Commit berkas itu. Perbarui bila sudah tidak sesuai.

Jika proyek Anda sudah punya `CLAUDE.md`, Auto Code membacanya juga, apa adanya.

## Tetapkan perintah verifikasi

Beri tahu Auto Code cara memeriksa hasil kerjanya sendiri:

```jsonc
// .autocode/settings.json
{ "verifyCommands": ["dotnet build", "dotnet test"] }
```

Agent akan mendapat tool `Verify` dan diinstruksikan menjalankannya setelah melakukan perubahan.
Tanpa ini ia menebak sistem build Anda; dengan ini ia tahu.

## Menjalankan tanpa interaksi

```bash
# Jawab lalu keluar
autocode -p "berapa endpoint publik yang diekspos layanan ini?"

# Keluaran terstruktur untuk skrip
autocode -p "daftar semua TODO di src" --output-format json | jq -r .result

# Baca prompt dari pipa
git diff | autocode -p "tinjau perubahan ini"
```

Pada mode `--print` tidak ada yang bisa menanyai Anda, sehingga tool yang memerlukan persetujuan akan
ditolak disertai penjelasan, bukan menggantung. Gunakan `--permission-mode acceptEdits` bila Anda
memang ingin ia mengubah berkas tanpa pengawasan.

## Selanjutnya

- [Konfigurasi](configuration.md) — semua pengaturan dan urutan lapisannya
- [Izin](permissions.md) — cara kerja persetujuan, dan cara berhenti ditanyai
- [Skills](skills.md) — ubah alur kerja berulang menjadi `/deploy`
- [Subagent dan tim](subagents.md) — jauhkan investigasi besar dari konteks utama Anda
