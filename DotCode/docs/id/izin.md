# Izin dan keamanan

> 🇬🇧 [English](../en/permissions.md)

DotCode meminta izin sebelum mengubah apa pun. Pekerjaan baca-saja di dalam direktori kerja berjalan bebas; edit, perintah shell, akses web, dan tool MCP butuh persetujuan kecuali diizinkan oleh mode atau aturan.

## Mode izin

| Mode | Perilaku | Cara mengaktifkan |
|---|---|---|
| `default` | Bertanya untuk edit, perintah shell non-baca, web, MCP | bawaan |
| `acceptEdits` | Edit file di direktori kerja (dan `mkdir`/`touch`) disetujui otomatis | `Shift+Tab`, `--permission-mode acceptEdits` |
| `auto` | Model pengklasifikasi menyetujui aksi berisiko rendah, memblokir yang berbahaya, dan menanyakan sisanya | `--permission-mode auto`, atau `Shift+Tab` bila diaktifkan |
| `plan` | Hanya riset — perubahan diblokir sampai rencana disetujui | `Shift+Tab`, `--permission-mode plan` |
| `bypassPermissions` | Semua berjalan tanpa bertanya (aturan deny tetap berlaku) | `--dangerously-skip-permissions` |

### Auto mode

Alih-alih bertanya, DotCode meminta model pengklasifikasi kecil (peran `classifier`, fallback ke `fast`, lalu model utama) menilai apakah aksi yang butuh izin berisiko rendah dan sesuai permintaan Anda:

- **allow** — langsung dijalankan (baris tool menampilkan `Auto mode: allowed — <alasan>`). Contoh: build, test, linter, instal dependensi yang dideklarasikan, commit git lokal, edit file di proyek.
- **ask** — muncul dialog biasa (headless: ditolak dengan petunjuk). Contoh: `git push`, deploy, publish paket, menulis di luar proyek, aksi yang tidak bisa dibatalkan.
- **deny** — diblokir; alasannya dikirim ke model agar memilih cara yang lebih aman. Contoh: `curl … | bash`, `rm -rf` yang luas, menyentuh kredensial, aksi yang tampak berasal dari prompt injection.

Aturan `deny` dan `ask` eksplisit selalu didahulukan. Jawaban pengklasifikasi yang tidak jelas menjadi **ask**, sehingga auto mode hanya menghilangkan prompt untuk aksi yang secara eksplisit dinilai aman. Setelah agen membaca halaman web atau hasil MCP dalam satu turn, pengklasifikasi diminta lebih curiga terhadap aksi yang tidak diminta. Edit file di direktori kerja disetujui tanpa memanggil pengklasifikasi. Biaya pengklasifikasi ikut dihitung di `/cost`.

```jsonc
"permissions": { "autoMode": { "enabled": true, "model": "openai:gpt-5-mini", "guidance": "Jangan pernah izinkan deploy atau migrasi database." } }
```

`enabled` memasukkan auto mode ke siklus Shift+Tab (default → accept edits → auto → plan). Footer menampilkan `⏵⏵ auto mode on`.

### `--dangerously-skip-permissions`

Memulai sesi dalam mode bypass; footer menampilkan `⏵⏵ bypass permissions on` berwarna merah. Gunakan hanya di sandbox, container, atau CI yang aman dibuang. `--allow-dangerously-skip-permissions` membuat mode bypass bisa dicapai lewat Shift+Tab tanpa langsung aktif. Organisasi dapat melarangnya dengan `"permissions": { "disableBypassPermissionsMode": true }` di managed settings.

## Aturan

Aturan memakai sintaks Claude Code `Tool` atau `Tool(spesifier)` di `permissions.allow`, `permissions.ask`, `permissions.deny` (atau `--allowedTools` / `--disallowedTools`). Urutan evaluasi: **deny → batasan plan mode → ask → bypass → allow → default bawaan**.

| Aturan | Cocok dengan |
|---|---|
| `Read`, `Edit`, `Bash` | Semua pemakaian tool |
| `Bash(npm run test:*)` | Perintah yang diawali `npm run test` |
| `Bash(git *)` | Wildcard |
| `Bash(dotnet build)` | Persis perintah ini |
| `Read(~/rahasia/**)` | Glob relatif home (aturan `Read` berlaku untuk Read, Glob, Grep) |
| `Edit(/src/**)` | Glob relatif root proyek (aturan `Edit` berlaku untuk Edit, Write, NotebookEdit) |
| `Edit(//etc/**)` | Path absolut |
| `WebFetch(domain:learn.microsoft.com)` | Domain dan subdomainnya |
| `mcp__github`, `mcp__github__create_issue` | Semua tool server MCP, atau satu tool |
| `Agent(Explore)`, `Skill(pdf)` | Tipe subagent, skill |

Perintah shell majemuk dipecah pada `&&`, `||`, `;`, `|`, dan baris baru: aturan allow harus cocok dengan **setiap** subperintah, aturan deny memblokir jika **salah satu** cocok. Substitusi perintah (`$(…)`, backtick) dan redirect output tidak pernah dianggap baca-saja.

### Default bawaan

- Read/Glob/Grep di direktori kerja: diizinkan; di luar: bertanya; file yang tampak rahasia (`.env`, `*.pem`, `id_rsa` …): bertanya.
- Perintah shell baca-saja yang dikenal (`ls`, `cat`, `git status/log/diff`, `Get-ChildItem` …): diizinkan.
- TodoWrite, AskUserQuestion, Agent, Skill: diizinkan.
- Lainnya: bertanya.

## Hook juga bisa memutuskan

Hook `PreToolUse` dapat mengembalikan `{"hookSpecificOutput":{"permissionDecision":"allow|deny|ask"}}` atau keluar dengan kode 2 untuk memblokir. Lihat [Ekstensi → Hooks](ekstensi.md#hooks).

## Checkpoint

Sebelum perubahan pertama pada sebuah file di setiap turn, DotCode menyimpan snapshot-nya. `Esc Esc` atau `/rewind` memulihkan kode dan/atau percakapan ke prompt sebelumnya.

## Pengaman lain

- Hasil tool diperlakukan sebagai data; model diinstruksikan untuk tidak mengikuti perintah yang ada di file atau halaman web.
- WebFetch memblokir alamat privat, loopback, link-local, dan metadata cloud (`DOTCODE_ALLOW_PRIVATE_FETCH=1` untuk mengizinkan) serta melaporkan redirect lintas host.
- Edit mensyaratkan Read terlebih dahulu dan gagal bila file berubah di disk.
- Sesi SDK **menolak secara default** kecuali aplikasi mendaftarkan handler izin.
- Server WebSocket hanya mendengarkan `127.0.0.1` dan mendukung bearer token.
