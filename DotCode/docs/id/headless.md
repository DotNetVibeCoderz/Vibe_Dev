# Mode headless (`-p`)

> 🇬🇧 [English](../en/headless.md)

`dotcode -p "<prompt>"` menjalankan satu prompt tanpa UI lalu keluar — untuk skrip, CI, dan pipe.

```bash
dotcode -p "Apa fungsi src/Engine?"
cat error.log | dotcode -p "jelaskan stack trace ini"
dotcode -p "/review" --output-format json
dotcode -c -p "sekarang tambahkan test-nya"      # lanjutkan sesi terakhir
```

## Format output

| Format | Output |
|---|---|
| `text` (default) | Jawaban akhir. Error dan retry ke stderr; `--verbose` juga mencetak pemanggilan tool ke stderr. |
| `json` | Satu objek hasil (`type`, `subtype`, `is_error`, `result`, `session_id`, `total_cost_usd`, `usage`, `permission_denials` …). |
| `stream-json` | Event engine per baris (skema sama dengan `session.event` SDK), lalu objek hasil. `--include-partial-messages` menambahkan delta teks. |

`--input-format stream-json` membaca satu pesan per baris dari stdin dan menjawab masing-masing dalam sesi yang sama.

## Izin di mode headless

Tidak ada orang yang bisa ditanya, jadi tool yang butuh persetujuan **ditolak dengan petunjuk** dan dicatat di `permission_denials`. Berikan izin sesuai kebutuhan:

```bash
dotcode -p "perbaiki build" --permission-mode acceptEdits --allowedTools "Bash(dotnet build*)" "Bash(dotnet test*)"
dotcode -p "refaktor semuanya" --dangerously-skip-permissions   # hanya untuk sandbox/CI
```

Kode keluar: `0` sukses, `1` error, `2` salah pemakaian, `130` dihentikan.

## Contoh GitHub Actions

```yaml
- name: Review DotCode
  env: { DEEPSEEK_API_KEY: "${{ secrets.DEEPSEEK_API_KEY }}" }
  run: |
    git diff origin/main... | dotcode -p "Review diff ini; daftar bug dengan file:baris" \
      --model deepseek:deepseek-v4-flash --output-format json > review.json
```
