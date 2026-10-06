# Kanal, trigger, dan mode suggest

[English](../en/channels-and-triggers.md) · [Bahasa Indonesia](../id/channels-and-triggers.md)

## Kanal

**Kanal** menghubungkan layanan pesan eksternal ke satu bot. Setiap percakapan eksternal (chat Telegram, kanal Slack,
nomor WhatsApp, pengunjung web chat) menjadi utas Marbots tersendiri sehingga bot mengingatnya, dan balasan dikirim
otomatis saat tugas selesai. Jika bot menunggu persetujuan, pengguna kanal diberi tahu bahwa operator perlu menyetujui
langkah berikutnya.

![Kanal](../images/channels.png)

| Jenis | Masuk | Keluar | Secret (disimpan terenkripsi) | Status |
|---|---|---|---|---|
| **Web chat** | Halaman bawaan `/webchat/{id}` (sematkan dengan iframe) | Tampil di halaman | tidak ada | diuji end to end dengan LLM sungguhan |
| **Webhook** | `POST /api/v1/channels/{id}/inbound` dengan `X-Marbots-Secret` | `POST` ke `outboundUrl` | `inboundSecret`, `outboundSecret` | diuji end to end |
| **Telegram** | Long polling (bawaan, tanpa URL publik) atau `/api/v1/channels/{id}/telegram` | Bot API `sendMessage` (dipotong per 4096 karakter) | `token`, `webhookSecret` | diuji dengan Bot API simulasi |
| **Slack** | Events API → `/api/v1/channels/{id}/slack` (signing secret, jendela replay 5 menit, `url_verification`) | `chat.postMessage` | `token`, `signingSecret` | diuji dengan API simulasi |
| **WhatsApp** | Webhook Cloud API `/api/v1/channels/{id}/whatsapp` (verify token, `X-Hub-Signature-256`) | Graph API `messages` | `token`, `appSecret`, `verifyToken` | diuji dengan API simulasi |
| **Discord** | Relay Anda melakukan POST ke URL inbound generik | Webhook kanal | `webhookUrl`, `inboundSecret` | diuji dengan API simulasi |

Microsoft Teams, Zapier, n8n, dan Make bekerja melalui kanal **Webhook**.

```bash
# Kanal webhook generik
curl -X POST localhost:5170/api/v1/channels/$ID/inbound -H "X-Marbots-Secret: $SECRET" \
     -H 'Content-Type: application/json' -d '{"conversationId":"order-42","senderName":"Dina","text":"Status pesanan saya?"}'
```

Keamanan:
- Pesan dari kanal adalah masukan yang tidak tepercaya. Beri bot kanal profil izin yang sempit (mis. `workspace-write`
  atau `read-only`) dan hanya tool yang dibutuhkan.
- Daftar **pengirim yang diizinkan** opsional per kanal.
- Webhook dari penyedia diautentikasi dengan tanda tangan masing-masing dan tidak memerlukan API key Marbots; endpoint
  `/api` lainnya tetap memerlukannya.

![Web chat](../images/webchat.png)

## Trigger

**Trigger** menjalankan bot tanpa ada yang mengetik. Atur di **Jadwal → Triggers**, lewat API (`/api/v1/triggers`),
atau dengan SDK .NET (`client.Triggers`).

| Jenis | Kapan | Placeholder prompt |
|---|---|---|
| **Webhook** | `POST /api/v1/hooks/{id}` dengan `X-Marbots-Secret: <secret>` atau `X-Marbots-Signature: sha256=<HMAC isi request>` | `{{payload}}` (isi request) |
| **Event** | Saat sebuah bot menyelesaikan tugas (opsional hanya bot tertentu) | `{{bot}}`, `{{task}}`, `{{result}}` |

```text
Trigger "Ringkas Atlas": saat Atlas menyelesaikan tugas → Wren: "Ringkas untuk newsletter: {{result}}"
Trigger "CI gagal": POST /api/v1/hooks/trg_… → Quinn: "Selidiki build yang gagal ini: {{payload}}"
```

Pengaman loop: trigger tidak pernah bereaksi pada tugas yang ia mulai sendiri, hanya bereaksi pada tugas tingkat atas,
dan paling banyak menyala 10 kali per menit.

## Mode suggest (delegasi)

**Pengaturan → Delegation**, `marbots delegation suggest`, atau `PUT /api/v1/system/delegation {"mode":"Suggest"}`.
Dalam mode Suggest, Boss Man tetap menyusun rencana, tetapi sebelum anggota tim mulai bekerja, rencana tampil sebagai
kartu persetujuan (siapa mengerjakan apa, dalam urutan apa). Setujui untuk menjalankannya; jika ditolak, Boss Man
menanyakan apa yang perlu diubah. Mode Auto (bawaan) langsung mendelegasikan.

## Streaming A2A

Agent Card setiap bot kini menyatakan `"streaming": true`. `message/stream` mengembalikan Server-Sent Events: tugasnya,
event `status-update` saat bot berpikir, memanggil tool, atau mendelegasikan, lalu status akhir dan `artifact-update`
berisi jawabannya.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
