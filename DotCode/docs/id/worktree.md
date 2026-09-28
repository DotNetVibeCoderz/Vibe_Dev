# Git worktree

> 🇬🇧 [English](../en/worktrees.md)

[Git worktree](https://git-scm.com/docs/git-worktree) adalah checkout kedua dari repositori yang sama di branch tersendiri. DotCode bisa menjalankan seluruh sesi, atau satu subagent, di dalamnya. Agen pun bisa mengedit, membangun, dan commit dengan leluasa tanpa menyentuh working tree utama Anda, dan beberapa sesi bisa bekerja pada repositori yang sama secara paralel.

DotCode menyimpan worktree-nya di `<repo>/.dotcode/worktrees/<nama>` pada branch `dotcode/<nama>`. DotCode menulis `.gitignore` di sana, sehingga worktree tidak pernah muncul di `git status` checkout utama.

## Sesi di dalam worktree

```bash
dotcode --worktree                  # nama otomatis, mis. wt-0928-181243-71fd
dotcode --worktree auth-refactor    # bernama (dipakai ulang bila sudah ada)
dotcode -w auth-refactor -c         # lanjutkan sesi terakhir di worktree itu
dotcode -w -p "perbaiki test yang flaky"  # juga bisa di mode headless
```

- DotCode membuat worktree dari `HEAD` saat ini, menampilkan path dan branch-nya, lalu memulai sesi di sana. Bila Anda memulai dari subdirektori (`repo/src`), Anda masuk ke subdirektori yang sama di dalam worktree. Panel sambutan menampilkan `worktree <nama> · dotcode/<nama>`.
- **Saat keluar**, worktree tanpa perubahan (tidak ada file yang belum di-commit dan tidak ada commit baru) dihapus beserta branch-nya. Bila ada perubahan, worktree dipertahankan dan DotCode menampilkan cara melanjutkan, menggabungkan, atau menghapusnya:

```text
Worktree kept: …/.dotcode/worktrees/auth-refactor (branch dotcode/auth-refactor: 2 commit(s)).
  Continue:  dotcode --worktree auth-refactor -c
  Merge:     git merge dotcode/auth-refactor   ·   Remove: dotcode worktree remove auth-refactor
```

Nama boleh berisi huruf, angka, `.`, `_`, dan `-`. Prompt yang diberi tanda kutip seperti `dotcode -w "perbaiki bug"` tidak pernah dianggap sebagai nama. Gunakan `--worktree=nama` bila ingin eksplisit.

## Mengelola worktree

```bash
dotcode worktree list                         # nama, branch, file berubah, commit di depan HEAD
dotcode worktree remove <nama> [--keep-branch]
dotcode worktree prune                        # hapus worktree bersih yang branch-nya sudah di-merge
```

## Subagent terisolasi

Tambahkan `isolation: worktree` ke definisi agen, atau biarkan model mengirim `"isolation": "worktree"` ke tool `Agent`:

```markdown
---
name: experimenter
description: Mencoba refactor berisiko di branch terpisah.
isolation: worktree
---
Lakukan perubahan, jalankan test, commit di branch Anda, lalu laporkan hasilnya.
```

Subagent bekerja di worktree-nya sendiri: path file, perintah `Bash`/`PowerShell`, dan direktori kerja di prompt semuanya mengarah ke sana. System prompt-nya memintanya meng-commit pekerjaan ke branch-nya. Subagent terisolasi yang berjalan paralel tidak saling mengganggu. Setelah selesai:

- **tidak ada perubahan** → worktree dan branch dihapus;
- **ada perubahan** → worktree dipertahankan, dan hasil tool memberi tahu agen utama path, branch, jumlah commit, serta cara meninjau (`git diff base...branch`) dan menggabungkannya.

## SDK

Semua SDK menerima opsi sesi `worktree`: `true` untuk nama otomatis, atau string berisi nama.

```ts
const session = await client.createSession({ worktree: "sdk-task", permissionMode: "acceptEdits" });
```

`session.create` mengembalikan `worktree: { name, path, branch }`. `session.close` menghapus worktree bila tidak ada perubahan. Di .NET gunakan `Worktree = true` / `WorktreeName = "…"`, di Python `worktree=True`/`"nama"`, di Go `Worktree`/`WorktreeName`, di Java `.worktree(true)`/`.worktree("nama")`.

## Persyaratan dan catatan

- `git` harus ada di `PATH`, dan repositori minimal punya satu commit.
- Worktree berbagi objek dan branch dengan repositori. Commit di sana menghasilkan commit di `dotcode/<nama>`, yang bisa di-merge, di-rebase, atau di-push seperti branch lain.
- File untracked yang dibutuhkan build (mis. `.env`, konfigurasi lokal) tidak ikut disalin ke worktree.
- Sesi dan checkpoint disimpan per path worktree, jadi `-c`/`--resume` di dalam worktree melanjutkan percakapan milik worktree tersebut.
