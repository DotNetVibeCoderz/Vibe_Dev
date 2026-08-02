# Provider dan model

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

Auto Code berbicara tiga protokol wire. Setiap vendor dipetakan ke salah satunya.

| `kind` | Protokol | Provider |
| --- | --- | --- |
| `OpenAICompatible` *(bawaan)* | OpenAI chat completions | OpenAI, Azure OpenAI, DeepSeek, Qwen, Ollama, LM Studio, vLLM, OpenRouter, Groq, Together, Mistral, xAI, dan apa pun yang mengekspos `/chat/completions` |
| `Anthropic` | Anthropic Messages API | Claude, termasuk extended thinking |
| `Gemini` | Google `streamGenerateContent` | Gemini |

Seluruh lapisan di atas provider — agent loop, tool, subagent — bekerja murni melalui `IChatClient`.
Menambah vendor adalah urusan konfigurasi, bukan kode.

## Preset bawaan

Menyebut nama preset memberi Anda endpoint, model, dan harga; Anda cukup menyediakan key-nya.

| Nama | Model bawaan | Variabel key |
| --- | --- | --- |
| `openai` | `gpt-4.1` | `OPENAI_API_KEY` |
| `anthropic` | `claude-sonnet-4-5` | `ANTHROPIC_API_KEY` |
| `gemini` | `gemini-2.5-pro` | `GEMINI_API_KEY` |
| `deepseek` | `deepseek-chat` | `DEEPSEEK_API_KEY` |
| `qwen` | `qwen-max` | `DASHSCOPE_API_KEY` |
| `ollama` | `qwen2.5-coder:14b` | — (lokal) |
| `lmstudio` | `local-model` | — (lokal) |
| `openrouter` | `anthropic/claude-sonnet-4.5` | `OPENROUTER_API_KEY` |
| `groq` | `llama-3.3-70b-versatile` | `GROQ_API_KEY` |
| `mistral` | `mistral-large-latest` | `MISTRAL_API_KEY` |
| `xai` | `grok-4` | `XAI_API_KEY` |
| `azure-openai` | `gpt-4.1` | `AZURE_OPENAI_API_KEY` |

Apa pun yang Anda isi di profil akan menimpa preset; yang Anda kosongkan akan memakai nilai preset.

## Endpoint lain apa pun

Jika ia menerima `POST /chat/completions` dalam bentuk OpenAI, ia bisa dipakai:

```jsonc
{
  "providers": {
    "gateway-kantor": {
      "kind": "OpenAICompatible",
      "endpoint": "https://llm.internal.example.com/v1",
      "apiKey": "env:INTERNAL_LLM_KEY",
      "model": "internal-coder-v2",
      "contextWindow": 65536,
      "supportsParallelToolCalls": false
    }
  }
}
```

## Berganti saat berjalan

```bash
autocode --provider anthropic --model claude-opus-4-5
```

```
› /provider              # daftar profil, yang aktif ditandai
› /provider deepseek     # ganti
› /model deepseek-reasoner
```

Pergantian membangun ulang agent loop dengan profil baru. Percakapan tetap berlanjut; penghitung
biaya dimulai ulang, karena harganya berubah.

## Peran model

Satu profil bisa menyebut tiga model:

| Kolom | Dipakai untuk |
| --- | --- |
| `model` | Agent loop utama |
| `smallModel` | Ringkasan pemadatan dan kerja latar — lebih murah dan cepat |
| `embeddingModel` | Indeks kode semantik |

Mengisi `smallModel` dengan model murah cukup terasa menurunkan biaya sesi panjang, karena pemadatan
berjalan di model itu, bukan di model utama Anda.

## Embedding

Embedding dikonfigurasi **terpisah dari chat**, karena keduanya jarang cocok memakai backend yang
sama. Anthropic tidak punya endpoint embedding sama sekali, dan meskipun sebuah provider
menyediakannya, mengirim seluruh berkas sumber lewat API berbayar menghabiskan biaya nyata untuk
hasil yang setara dengan model lokal.

Empat backend, dipilih lewat `embeddings.kind`:

| Kind | Kebutuhan | Dipakai bila |
| --- | --- | --- |
| `Auto` *(bawaan)* | — | Mengikuti provider chat; menjadi `None` bila providernya tak punya endpoint embedding |
| `Ollama` | Ollama berjalan lokal | Anda sudah memakai Ollama. Gratis, privat, kualitas baik |
| `Onnx` | Berkas `.onnx` di disk | Tanpa server dan tanpa jaringan sama sekali |
| `OpenAICompatible` | API key | Anda ingin model embedding daring |
| `None` | — | Matikan indeksnya |

### Ollama

Memakai `/api/embed` native milik Ollama lewat OllamaSharp — bukan lapisan kompatibilitas OpenAI.

```bash
ollama pull nomic-embed-text
```

```jsonc
{
  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

Bawaannya `http://localhost:11434`. Endpoint yang ditulis dengan akhiran `/v1` tetap diterima dan
dipangkas, karena itulah yang biasanya Anda salin dari konfigurasi provider chat.

Model embedding kode yang baik: `nomic-embed-text` (768d), `mxbai-embed-large` (1024d),
`all-minilm` (384d, paling cepat).

### ONNX — sepenuhnya offline

Tanpa server, tanpa jaringan, tanpa key. Unduh sekali sebuah sentence-transformer lalu tunjuk
berkasnya:

```bash
mkdir -p models && cd models
curl -LO https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
curl -LO https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt
```

```jsonc
{
  "enableSemanticIndex": true,
  "embeddings": {
    "kind": "Onnx",
    "modelPath": "models/model.onnx",
    "vocabPath": "models/vocab.txt",
    "maxTokens": 256
  }
}
```

Model itu berukuran 90 MB dan menghasilkan vektor 384 dimensi dalam sekitar 20 ms per teks di CPU.
Auto Code melakukan mean pooling atas token state dengan attention mask lalu menormalkan L2 — sesuai
cara model sentence-transformer dilatih.

Model bergaya BERT apa pun yang diekspor dengan input standar `input_ids` / `attention_mask` /
`token_type_ids` bisa dipakai. Set `lowerCase: false` untuk model `-cased`.

### Mencampur backend

Inilah tujuan pemisahannya — bernalar dengan model daring, mengindeks secara lokal:

```jsonc
{
  "activeProvider": "anthropic",
  "providers": { "anthropic": { "kind": "Anthropic", "model": "claude-sonnet-4-5" } },

  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

### Dimensi

Biarkan `embeddings.dimensions` kosong. Auto Code menyimpulkan lebarnya dari batch pertama.

Menuliskan dimensi yang salah dulunya adalah kegagalan diam-diam — semua perbandingan kemiripan
dilewati dan indeks tidak mengembalikan apa pun tanpa pesan galat. Bila Anda mengisinya, nilai itu
ditegakkan sebagai penegasan dan ketidakcocokan akan gagal terang-terangan.

Verifikasi backend-nya sebelum Anda bergantung padanya:

```bash
autocode doctor      # membangun backend-nya lalu melaporkannya
```

```
› /index             # membangun indeks sambil melaporkan dimensinya
› /status            # menampilkan backend hasil resolusi
```

## Extended thinking

```jsonc
{
  "providers": {
    "anthropic": {
      "kind": "Anthropic",
      "model": "claude-sonnet-4-5",
      "enableExtendedThinking": true,
      "thinkingBudgetTokens": 12000
    }
  }
}
```

Penalaran ditampilkan di terminal dengan huruf miring redup; matikan tampilannya dengan
`"showThinking": false` tanpa mematikan penalarannya sendiri.

Untuk Anthropic, blok thinking dikirim ulang beserta signature-nya pada setiap putaran pemanggilan
tool, sesuai keharusan API — detail yang biasanya hilang pada adapter generik.

## Model lokal

Runtime lokal berstatus setara. Dua hal yang perlu diperhatikan:

- **Dukungan tool calling.** Model harus mendukungnya. `qwen2.5-coder`, `llama3.1`+, dan
  `mistral-nemo` mendukung; banyak model kecil tidak, dan akibatnya tidak akan pernah memanggil tool.
- **Pemanggilan paralel.** Beberapa server lokal menolak tool call berkelompok. Set
  `"supportsParallelToolCalls": false` bila Anda melihat galat permintaan tidak valid.

```jsonc
{
  "activeProvider": "ollama",
  "providers": {
    "ollama": {
      "endpoint": "http://localhost:11434/v1",
      "model": "qwen2.5-coder:14b",
      "contextWindow": 32768,
      "supportsParallelToolCalls": false
    }
  }
}
```

## Pelacakan biaya

Isi kedua kolom harga dan Auto Code akan melacak pengeluaran per sesi:

```jsonc
{ "inputCostPerMillionTokens": 3.0, "outputCostPerMillionTokens": 15.0 }
```

`/cost` merinci angkanya; baris status setelah tiap giliran menampilkan totalnya. Biarkan `0` —
seperti pada preset lokal — dan tampilan hanya menunjukkan token.

## Pemecahan masalah

| Gejala | Penyebab |
| --- | --- |
| `API key is not configured` | Variabel `env:` belum diset. Periksa dengan `autocode doctor`. |
| `404` pada endpoint OpenAI-compatible | `endpoint` kurang akhiran `/v1`. |
| Model tidak pernah memanggil tool | Model tidak mendukung tool calling. Coba model lebih besar atau lebih baru. |
| `400` dengan keluhan schema pada Gemini | Laporkan: ada kata kunci yang lolos dari penyaringan schema. |
| Galat permintaan tidak valid berulang | Set `"supportsParallelToolCalls": false`. |
| Konteks cepat penuh | Turunkan `compactionThreshold`, atau isi `contextWindow` dengan benar. |
