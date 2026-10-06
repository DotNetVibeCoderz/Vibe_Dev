"""Real-LLM trial run for Marbots.

Drives a running Marbots server through representative jobs (web app, documents, scripts + spreadsheets,
web research, MCP tools, skills, scheduling, conversational bot creation, A2A) and records the outcome.
An auto-approver plays the human: it approves pending actions after a delay (so screenshots can catch them).

Usage: python samples/trials/run_trials.py [--only name,name] [--approve-delay 40]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "sdk", "python"))
from marbots import Marbots, MarbotsError  # noqa: E402

BASE = os.environ.get("MARBOTS_URL", "http://localhost:5170")
mb = Marbots(BASE, timeout=1800)

TRIALS = {
    "web-app": ("boss-man", "Kas Warung web app", """Tolong buatkan aplikasi web "Kas Warung" — pencatat pemasukan dan pengeluaran harian untuk warung kecil.
1) Alice: bangun aplikasi statis di folder apps/kas-warung (index.html, styles.css, app.js). Fitur: tambah transaksi (tanggal, keterangan, kategori, nominal, jenis masuk/keluar), daftar transaksi, ringkasan saldo hari ini dan bulan ini, hapus transaksi, simpan di localStorage, format Rupiah. Desain rapi, responsif, ramah mobile.
2) Quinn: setelah Alice selesai, review kodenya dan uji: tulis tests/check_kas_warung.py yang memvalidasi struktur HTML (elemen form, id yang dipakai app.js ada di HTML) lalu jalankan. Laporkan bug yang ditemukan.
Ringkas hasil akhirnya untuk saya."""),
    "bilingual-doc": ("boss-man", "Onboarding guide EN/ID", """Ask Wren to write an employee onboarding guide for a 20-person Indonesian software startup called "Nusantara Kopi Tech".
Deliver docs/onboarding-en.md (English) and docs/onboarding-id.md (Bahasa Indonesia) with identical structure (first week checklist, tools & accounts, ways of working, who to ask, glossary),
plus a print-ready docs/onboarding.html based on the report-writing skill's HTML template. Use the bilingual-docs and report-writing skills."""),
    "script-spreadsheet": ("boss-man", "Expense summary script", """I need a small automation:
1) Create realistic sample data data/expenses.csv with 60 rows across July-September 2026 (date, description, category, amount in IDR).
2) Write scripts/monthly_summary.py that reads the CSV and produces reports/monthly-summary.xlsx: one sheet per month listing expenses, a category subtotal table using SUM/SUMIF formulas, and a "Summary" sheet comparing months. Use openpyxl.
3) Run it, then re-open the workbook and print the summary values to prove it works.
Give this to Alice, then ask Quinn to run the script again and verify the totals against the CSV with an independent pandas check."""),
    "research": ("atlas", "POS apps research", """Research the top 3 point-of-sale (aplikasi kasir) apps used by UMKM in Indonesia. For each: pricing tiers, key features, strengths, weaknesses.
Use web search and read primary sources. Save a cited brief to research/pos-indonesia.md (with a comparison table and sources list) using the market-research skill, then give me the key takeaways."""),
    "mcp": ("boss-man", "MCP showcase", """Create a new teammate named "Mira" from the data-engineer template, with MCP servers "filesystem" and "time" enabled.
Then delegate to Mira: use the time MCP server to get the current time in Asia/Jakarta and Asia/Tokyo, and use the filesystem MCP server (not the built-in file tools) to write reports/world-clock.md with a small table of both times. Report back what Mira did."""),
    "schedule-and-memory": ("boss-man", "Schedule + memory", """Ingat ini untuk ke depannya: perusahaan saya bernama "Nusantara Kopi Tech" dan saya lebih suka laporan dalam Bahasa Indonesia.
Lalu jadwalkan: setiap Senin jam 08:00 WIB (time zone "SE Asia Standard Time"), minta Atlas membuat ringkasan singkat berita AI minggu ini."""),
    "landing-page": ("boss-man", "Landing page with research", """Riset singkat 3 kompetitor kedai kopi digital (aplikasi pemesanan kopi) di Indonesia, lalu buat landing page produk kita "Kopi Kilat" (pesan kopi lewat WhatsApp dalam 30 detik).
Atlas: riset kompetitor (ringkas, simpan research/kopi-competitors.md). Alice (bergantung pada riset Atlas): landing page statis di apps/kopi-kilat/index.html (hero, 3 keunggulan dibanding kompetitor, cara pesan 3 langkah, testimoni, CTA WhatsApp), desain menarik dan responsif. Ringkas hasil akhirnya."""),
}


def auto_approver(delay: float, stop: threading.Event) -> None:
    first_seen: dict[str, float] = {}
    while not stop.is_set():
        try:
            for a in mb.approvals.pending():
                first_seen.setdefault(a["id"], time.time())
                if time.time() - first_seen[a["id"]] >= delay:
                    mb.approvals.approve(a["id"], "Session")
                    print(f"  [approver] approved {a['toolName']} for {a['botId']}", flush=True)
        except (MarbotsError, OSError) as e:
            print("  [approver] error:", e, flush=True)
        stop.wait(2)


def run_trial(name: str, results: dict) -> None:
    bot, title, prompt = TRIALS[name]
    started = time.time()
    try:
        thread = mb.threads.create(bot, title)
        print(f"> {name}: thread {thread['id']} with {bot}", flush=True)
        r = mb.threads.send(thread["id"], prompt, wait=True, timeout_seconds=2400)
        task = r["task"]
        files = mb.threads.files(thread["id"])
        children = [t for t in mb.tasks.list(thread["id"]) if t.get("parentTaskId")]
        results[name] = {
            "thread": thread["id"], "state": task["state"], "seconds": round(time.time() - started),
            "steps": task["steps"], "tokens": task["inputTokens"] + task["outputTokens"], "costUsd": task["costUsd"],
            "delegated": [(c["botId"], c["state"]) for c in children],
            "files": [f["path"] for f in files],
            "reply": (r.get("reply") or {}).get("content", task.get("error")),
        }
        print(f"< {name}: {task['state']} in {results[name]['seconds']}s, files={len(files)}", flush=True)
    except (MarbotsError, OSError) as e:
        results[name] = {"state": "ERROR", "error": str(e)}
        print(f"! {name}: {e}", flush=True)


def a2a_trial(results: dict) -> None:
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "message/send", "params": {
        "message": {"role": "user", "messageId": "m1", "parts": [{"kind": "text", "text": "In two sentences: what makes a good README?"}]}}}).encode()
    req = urllib.request.Request(f"{BASE}/a2a/wren", data=body, headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=600) as resp:
        doc = json.loads(resp.read())
    card = json.loads(urllib.request.urlopen(f"{BASE}/a2a/wren/.well-known/agent-card.json").read())
    results["a2a"] = {"state": doc["result"]["status"]["state"], "card": card["name"],
                      "reply": doc["result"]["status"]["message"]["parts"][0]["text"]}
    print("< a2a:", results["a2a"]["state"], flush=True)


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--only", default="")
    p.add_argument("--approve-delay", type=float, default=40)
    p.add_argument("--out", default="trial-results.json")
    args = p.parse_args()
    names = [n for n in TRIALS if not args.only or n in args.only.split(",")]
    results: dict = {}
    stop = threading.Event()
    threading.Thread(target=auto_approver, args=(args.approve_delay, stop), daemon=True).start()
    workers = [threading.Thread(target=run_trial, args=(n, results)) for n in names]
    for w in workers:
        w.start()
        time.sleep(3)
    if not args.only or "a2a" in args.only:
        a2a_trial(results)
    for w in workers:
        w.join()
    stop.set()
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print(json.dumps({k: {kk: v[kk] for kk in ("state", "seconds", "files") if kk in v} for k, v in results.items()}, indent=1))


if __name__ == "__main__":
    main()
