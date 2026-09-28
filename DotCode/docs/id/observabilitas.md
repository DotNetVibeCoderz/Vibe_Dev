# Observabilitas: audit log & OpenTelemetry

> 🇬🇧 [English](../en/observability.md)

Kedua fitur ini **nonaktif secara default** — tidak ada data yang keluar dari mesin Anda kecuali Anda mengaktifkannya.

## Audit log

Catatan yang tahan manipulasi (tamper-evident) tentang apa yang dilakukan agen: setiap pemanggilan tool (tool, input, keputusan izin, hasil, durasi, model), akhir setiap giliran (token, biaya, alasan berhenti) dan, opsional, prompt pengguna.

```jsonc
// ~/.dotcode/settings.json — atau managed settings untuk mewajibkannya di organisasi
{
  "audit": {
    "enabled": true,
    "path": "~/.dotcode/audit/audit.jsonl",   // default: ~/.dotcode/audit/audit-YYYY-MM.jsonl
    "includePrompts": false                    // ikut catat prompt pengguna (disensor)
  }
}
```

Setiap baris adalah objek JSON:

```json
{"ts":"2026-09-28T10:51:44.59Z","event":"tool_call","session":"…","user":"dev","host":"PC-01","cwd":"C:\\src\\app",
 "tool":"Bash","tool_use_id":"…","input":"{\"command\":\"npm test\"}","mode":"auto","decision":"auto",
 "reason":"runs the project's tests","outcome":"ok","duration_ms":5321,"model":"openai:gpt-5",
 "prev":"<hash entri sebelumnya>","hash":"<sha256(prev + entri)>"}
```

| `decision` | Arti |
|---|---|
| `mode` | Diizinkan oleh mode izin aktif / default bawaan (tool read-only, acceptEdits, bypass) |
| `rule:<aturan>` | Diizinkan oleh aturan allow yang cocok, mis. `rule:Bash(npm run test:*)` |
| `hook` | Hook PreToolUse mengembalikan `allow` |
| `auto` | Disetujui classifier auto mode (`reason` berisi penjelasannya) |
| `user:allowonce` / `user:allowsession` / `user:allowalways` | Disetujui di dialog izin |
| `denied` / `rejected` / `blocked` | Aturan deny atau penolakan auto mode / pengguna menolak / diblokir hook PreToolUse |

**Rantai hash.** Setiap entri menyimpan SHA-256 entri sebelumnya, sehingga mengubah, menghapus, atau menukar urutan baris akan memutus rantai:

```bash
dotcode audit path            # lokasi file log
dotcode audit verify          # ✓ …: 128 entries, chain intact   (exit code 1 bila rusak)
dotcode audit verify lain.jsonl
```

**Sensor rahasia.** Kredensial umum disamarkan sebelum ditulis: `sk-…`, `ghp_…`/`github_pat_…`, Slack `xox…`, AWS `AKIA…`, Google `AIza…`, token npm/PyPI, JWT dan penugasan `password=… / token: … / api_key=…`. Input dipotong maksimal 8.000 karakter. *Output* tool tidak pernah dicatat.

> Rantai hash membuktikan integritas file sebagaimana ditulis; untuk melindungi dari penulisan ulang seluruh file, kirim ke penyimpanan append-only (SIEM, bucket WORM) — misalnya lewat hook `Stop` atau log forwarder.

## OpenTelemetry

DotCode memancarkan trace dan metrik melalui `ActivitySource`/`Meter` standar .NET bernama **`DotCode`**, dan menyertakan exporter **OTLP/HTTP (JSON)** bawaan yang kecil — tanpa SDK tambahan, berjalan di binary NativeAOT.

```jsonc
{
  "otel": {
    "enabled": true,
    "endpoint": "http://localhost:4318",          // → /v1/traces dan /v1/metrics
    "headers": { "x-api-key": "${env:OTEL_KEY}" },
    "serviceName": "dotcode",
    "logPrompts": false                            // tambahkan prompt (disensor) ke span giliran
  }
}
```

Variabel standar juga didukung: `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` (`k=v,k2=v2`), `OTEL_SERVICE_NAME`. `"telemetry": true` atau `DOTCODE_ENABLE_TELEMETRY=1` mengaktifkan ekspor dengan default tersebut.

### Span

| Span | Atribut |
|---|---|
| `dotcode.turn` / `dotcode.subagent` | `session.id`, `dotcode.turn.id`, `gen_ai.request.model`, `dotcode.permission.mode`, `dotcode.stop_reason`, `dotcode.model_calls`, `gen_ai.usage.input_tokens`/`output_tokens`, `dotcode.cost_usd` |
| `chat <model>` (client) | `gen_ai.system`, `gen_ai.request.model`, `gen_ai.response.finish_reasons`, penggunaan token, `error.type` |
| `execute_tool <nama>` | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `dotcode.permission.decision`, `dotcode.tool.outcome` |

Span model dan tool menjadi anak dari span giliran; span subagent bersarang di bawah pemanggilan tool `Agent` yang memulainya.

### Metrik (kumulatif)

| Metrik | Satuan | Atribut |
|---|---|---|
| `dotcode.tokens` | token | `gen_ai.request.model`, `gen_ai.system`, `type` (input/output/cache_read/cache_write) |
| `dotcode.cost` | USD | model, system |
| `dotcode.turns` | giliran | `stop_reason` |
| `dotcode.tool.calls` | panggilan | `tool`, `outcome`, `decision` |
| `dotcode.llm.duration` | ms (histogram) | model, `outcome` |
| `dotcode.tool.duration` | ms (histogram) | `tool` |

Span dikirim setiap 5 detik, metrik setiap 30 detik, dan keduanya saat keluar. Kegagalan ekspor diabaikan — telemetri tidak pernah mengganggu agen.

Coba cepat secara lokal dengan Jaeger:

```bash
docker run --rm -p 16686:16686 -p 4318:4318 jaegertracing/all-in-one
DOTCODE_ENABLE_TELEMETRY=1 dotcode -p "ringkas repo ini"
# buka http://localhost:16686, service "dotcode"
```

Menanamkan engine di aplikasi .NET Anda sendiri (`DotCode.Sdk` in-process)? Berlangganan source/meter `DotCode` dengan SDK OpenTelemetry resmi (`AddSource("DotCode")`, `AddMeter("DotCode")`) alih-alih exporter bawaan.
