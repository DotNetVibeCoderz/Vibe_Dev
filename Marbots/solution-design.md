# Marbots — Marvelous Bots
## Solution Design / Dokumen Solusi Multi-Agent Collaboration Platform

**Document:** `marbots-solution-design.md`  
**Status:** Proposed Architecture  
**Target Runtime:** .NET 10  
**Product Name:** **Marbots (Marvelous Bots)**  
**Primary Language:** Bahasa Indonesia, with bilingual documentation requirements (English + Bahasa Indonesia)

---

## 1. Executive Summary

**Marbots** adalah platform kolaborasi multi-agent yang memperlakukan AI agent sebagai sekumpulan rekan kerja digital yang memiliki identitas, peran, memory, tools, skills, environment eksekusi, dan lifecycle sendiri. Platform menggabungkan gagasan terbaik dari agent gateway/multi-channel, agent yang dapat belajar, persistent bot/computer, interoperabilitas agent, serta developer harness yang dapat di-embed.

Pusat koordinasi Marbots adalah agent default bernama **Boss Man**. Boss Man selalu tersedia ketika platform aktif dan bertindak sebagai manager/orchestrator. Pengguna cukup memberikan tujuan tingkat tinggi kepada Boss Man, lalu Boss Man dapat menganalisis pekerjaan, memilih bot berdasarkan persona, skill, capability, model, tool, lokasi host, availability dan cost, memecah pekerjaan menjadi sub-task, mengeksekusinya secara paralel, mengawasi progres, meminta approval jika diperlukan, lalu menggabungkan hasil.

Bot lain dapat dibuat melalui percakapan dengan Boss Man, UI, CLI, configuration-as-code, atau SDK. Setiap bot dapat berjalan pada host PC lokal, VM, container/Docker, maupun host remote. Beberapa bot boleh berbagi satu host tetapi tetap memiliki runtime instance, workspace, session, permissions, resource quota dan lifecycle yang terisolasi.

Marbots dirancang sebagai **local-first, distributed-ready, secure-by-default, model-agnostic, tool-agnostic dan protocol-friendly**.

---

## 2. Product Vision

> **“Build, hire, teach, connect and orchestrate marvelous AI coworkers from one platform.”**

Marbots bukan sekadar chatbot multi-agent. Marbots adalah **Agent Operating & Collaboration Platform** dengan empat lapisan besar:

1. **Management Plane** — Boss Man, registry, orchestration, policy, scheduler, deployments, observability.
2. **Agent Runtime Plane** — BotAgent instance, memory, context, reasoning loop, skills, MCP, kernel functions, computer-use.
3. **Experience Plane** — CLI, Web, Desktop, Mobile, headless SDK, external channels.
4. **Integration Plane** — A2A, MCP, SDK/API, providers, channels, remote host protocol.

### 2.1 Product principles

- **Boss-first, direct when needed.** Default flow melalui Boss Man, tetapi user dapat berkomunikasi langsung dengan bot.
- **Agent as a durable entity.** Bot mempunyai ID, persona, memory, workspace, capabilities dan lifecycle yang bertahan lintas chat.
- **Everything is composable.** Skill, MCP, model, memory provider, channel, host, permission profile dan kernel tool dapat dipasang-lepas.
- **Local-first, remote-capable.** Satu PC sudah cukup untuk memulai; scale-out ke VM/container tanpa mengubah mental model.
- **Secure by default.** Least privilege, explicit secrets, policy-based tools, approval gates dan audit trail.
- **Protocol over lock-in.** A2A untuk agent-to-agent; MCP untuk agent-to-tool; SDK dan API untuk aplikasi.
- **Human observable.** Semua delegation, tool execution, resource use, learning dan deployment dapat dilihat.
- **Fast path in .NET.** Implementasi utama .NET 10; Rust hanya untuk hot path/native subsystem yang terbukti membutuhkan optimasi.

---

## 3. Inspiration & Compatibility Targets

Marbots mengambil pola desain, bukan cloning implementation:

### 3.1 OpenClaw-inspired
- Multi-channel gateway.
- Multi-agent routing dan isolated sessions.
- Skills packaged sebagai instruction/resources.
- Scheduling/cron, hooks dan webhooks.
- CLI/TUI configurable.
- Browser/computer-oriented automation.

OpenClaw mendokumentasikan channel seperti Discord, Signal, Telegram, WhatsApp, Slack dan Teams, multi-agent routing, skills dan automation. Format skill utamanya memakai `SKILL.md` berikut resource terkait. [Source: OpenClaw docs, accessed 2026-10-06]

### 3.2 Hermes-inspired
- Optional **Auto-Learn** loop.
- Progressive disclosure pada skills untuk menghemat context.
- Agent dapat membuat atau memperbaiki skill berdasarkan pengalaman, dengan governance dan approval.
- Persistent memory dan learn-from-session workflow.

Hermes mendeskripsikan skills sebagai on-demand knowledge documents dan mendukung skill-compatible open standard; pola ini menjadi inspirasi kompatibilitas skill Marbots. [Source: Hermes Agent Skills documentation, accessed 2026-10-06]

### 3.3 Grok Bot-inspired
- Bot sebagai teammate bernama, ber-role dan ber-context persisten.
- Conversation-first bot creation.
- Long-running work dan computer use.
- Paralelisme dan handoff antar bot.
- Desktop/mobile messenger-like UI.

Dokumentasi Grok Bot menjelaskan bot dengan persistent computer, browser, filesystem dan terminal serta koordinasi paralel antar bot. Marbots memperluas konsep ini dengan pilihan host milik user dan isolasi configurable per BotAgent. [Source: xAI Grok Bot docs, accessed 2026-10-06]

### 3.4 Dots-inspired
- Persistent delegated worker.
- Background tasks dan connected applications.
- Clear permission boundary untuk pekerjaan autonomous.

### 3.5 DotCode-inspired
`DotCode` menjadi referensi penting untuk **Default Kernel Function Pack** dan terminal/developer harness. Repository tersebut mendeskripsikan toolbox seperti Read, Write, Edit, Glob, Grep, LSP, Bash, PowerShell, background shells, WebFetch, WebSearch, TodoWrite, subagents, Skills, AskUserQuestion, plan mode, notebooks, MCP tools/resources, permission modes, checkpoint/rewind, headless JSON-RPC dan SDK multi-language. Marbots sebaiknya menggunakan pola capability yang setara dan, jika lisensi/arsitektur memungkinkan, reuse library internal yang relevan daripada mengimplementasikan ulang. [Source: DotCode repository README, accessed 2026-10-06]

### 3.6 Three.Net-inspired 3D experience
`Three.Net` merupakan native multiplatform 3D untuk .NET dengan Rust/wgpu core dan C# API, serta sudah mencakup skeletal animation, PBR, shadow, SSAO, physics, spatial audio dan Avalonia control. Ini menjadikannya kandidat sangat cocok untuk fitur **Marbots Office 3D Simulation**. [Source: Three.Net repository README, accessed 2026-10-06]

### 3.7 A2A + MCP
A2A diposisikan untuk agent-to-agent interoperability, sementara MCP untuk koneksi agent ke tools/data. A2A mendukung discovery, delegation, collaborative tasks, streaming/asynchronous work, Agent Card dan interoperabilitas framework. [Source: A2A Protocol specification, accessed 2026-10-06]

---

# 4. Functional Scope

## 4.1 Boss Man — Default Manager Agent

Boss Man dibuat secara otomatis saat workspace/tenant Marbots pertama kali diinisialisasi dan tidak dapat dihapus. User boleh mengubah model, style/persona tambahan, policies dan resources, tetapi core manager capability tetap dijaga platform.

### Responsibilities

- menerima request user;
- menentukan apakah request dikerjakan sendiri atau didelegasikan;
- melakukan capability discovery terhadap Bot Registry;
- decomposition menjadi task/sub-task;
- memilih bot berdasarkan `Skills × Tools × Persona × Availability × Cost × Host × Trust`;
- parallel fan-out dan result fan-in;
- mengelola dependencies antar-task;
- meminta human approval;
- recovery/retry/reassignment saat bot gagal;
- monitor timeout, token budget, money budget dan compute budget;
- merangkum hasil dari beberapa bot;
- membuat bot baru atas instruksi user apabila policy mengizinkan;
- deploy BotAgent ke host baru melalui secure bootstrap;
- menjadi chat entry point default.

### Delegation modes

1. **Auto** — Boss Man menentukan semuanya.
2. **Suggest** — Boss Man menyarankan delegation dan user menyetujui.
3. **Manual** — user menentukan bot.
4. **Policy-driven** — routing berdasarkan organisasi, security clearance, data locality, budget atau SLA.

### Example

```text
User -> Boss Man:
"Buat landing page produk, riset kompetitor, implementasikan dan test."

Boss Man
  -> Researcher Bot: competitor research
  -> UX Bot: information architecture + visual direction
  -> Developer Bot: implementation
      depends-on Researcher + UX
  -> QA Bot: browser tests
      depends-on Developer
  -> Boss Man: synthesis + report
```

---

# 5. Bot Model

Setiap bot merupakan durable logical entity di Control Plane yang dapat memiliki satu atau lebih execution instance.

```yaml
bot:
  id: bot_01J...
  name: "Alice"
  description: "Senior software engineer"
  persona: "..."
  modelProfile: "coding-primary"
  autoLearn: true
  memory:
    shortTerm: enabled
    longTerm: enabled
  skills:
    - dotnet-engineering
    - code-review
  mcpServers:
    - github
    - postgres-readonly
  kernelFunctions:
    - filesystem
    - shell
    - process
    - web
    - playwright
    - computer-use
  hostRef: local-default
  resourceProfile: medium
  permissions: developer-safe
  channels: []
```

## 5.1 Bot creation paths

### Conversational creation

```text
User: Boss Man, buat bot bernama Atlas untuk riset pasar.
      Aktifkan browsing, Excel, long-term memory, auto-learn,
      tapi jangan beri permission menulis ke CRM tanpa approval.
```

Boss Man membuat draft manifest, melakukan validation, menampilkan permission diff, lalu provisioning.

### UI creation wizard
1. Identity: name, avatar, description.
2. Persona/instruction/system prompt.
3. Model/provider profile.
4. Memory.
5. Skills.
6. MCP servers.
7. Kernel functions.
8. Auto-Learn on/off + policy.
9. Host/resource allocation.
10. Permission/security profile.
11. Channels.
12. Review + deploy.

### CLI

```bash
marbots bot create
marbots bot create --from ./researcher.marbot
marbots bot list
marbots bot inspect researcher
marbots bot chat researcher
marbots bot stop researcher
marbots bot move researcher --host vm-gpu-01
```

### SDK

API SDK harus memberikan object model yang konsisten di .NET, Go, Python dan TypeScript.

---

# 6. Agent Runtime Architecture

Setiap running agent memakai **BotAgent Runtime**.

```text
Incoming Message / Scheduled Job / A2A Task
                    |
                    v
              Session Router
                    |
                    v
             BotAgent Runtime
  +-----------------------------------------+
  | Persona + Policy + Context Builder      |
  | Short-Term Memory                       |
  | Long-Term Memory Retrieval              |
  | Context Compaction                      |
  | Planner / Agent Loop                    |
  | Tool & Kernel Function Dispatcher       |
  | Skills Runtime                          |
  | MCP Client                              |
  | A2A Client/Server                       |
  | Computer Use / Browser                  |
  | Auto-Learn                              |
  | Scheduler / Todo                        |
  | Telemetry / Cost Meter                  |
  +-----------------------------------------+
       |             |              |
       v             v              v
   LLM Provider    Tools/MCP      Workspace
```

## 6.1 Execution loop

Recommended loop:

```text
Receive -> Build Context -> Decide/Plan -> Invoke Model
        -> Tool/Delegate/Answer
        -> Observe -> Update Working Memory
        -> Continue until terminal state
        -> Compact/Learn/Persist -> Emit Result
```

State machine:

```text
Queued
 -> Preparing
 -> Running
 -> WaitingForTool
 -> WaitingForAgent
 -> WaitingForHuman
 -> Running
 -> Completed

Terminal: Completed | Failed | Cancelled | TimedOut
```

Semua transition memiliki event tersimpan sehingga UI dapat melakukan live replay dan dashboard/3D simulation dapat menampilkan status realtime.

---

# 7. Memory System

## 7.1 Short-Term Memory

Scope session/thread. Memuat:
- current conversation turns;
- tool observations;
- temporary task state;
- todo/task graph;
- artifacts aktif;
- ephemeral notes.

## 7.2 Long-Term Memory

Kategori memory:
- **Semantic**: fakta/preference/user knowledge.
- **Episodic**: kejadian/tugas sebelumnya.
- **Procedural**: cara kerja yang terbukti.
- **Relational**: hubungan entity/project/person.
- **Artifact memory**: link ke dokumen/files/results.

Memory record wajib membawa provenance, timestamp, owner, visibility, source, confidence, expiry/retention dan sensitivity label.

## 7.3 Retrieval pipeline

```text
Query
 -> normalize
 -> permission filter
 -> hybrid retrieval (keyword + vector + metadata)
 -> rerank
 -> deduplicate
 -> token budget selection
 -> context injection with provenance
```

## 7.4 Memory isolation

Default:
- private bot memory;
- shared workspace memory hanya jika explicitly enabled;
- Boss Man tidak otomatis melihat secret/private memory bot kecuali policy memperbolehkan;
- cross-bot memory sharing melewati explicit capability/ACL.

---

# 8. Context Compaction

Context compaction diperlukan untuk thread panjang dan long-running agents.

### Strategy
- keep latest turns verbatim;
- preserve system policy/persona;
- preserve unresolved todos;
- pin critical facts/artifacts;
- summarize older conversation;
- preserve tool outputs sebagai references, bukan selalu raw content;
- incrementally summarize summaries;
- store pre-compaction snapshot sehingga dapat direcover/debug.

### Compaction triggers
- token threshold;
- turn count;
- task boundary;
- model switch;
- session idle checkpoint;
- manual `/compact`.

Target: compaction tidak mengubah permission state, pending approval, security constraints atau contractual output requirement.

---

# 9. Auto-Learn

Auto-Learn **optional per bot** dan default yang disarankan adalah `Off` untuk enterprise tenant sampai policy diset.

## 9.1 Learning loop

```text
Experience
 -> Detect reusable pattern
 -> Draft learning candidate
 -> Validate & sanitize
 -> Test in sandbox
 -> Risk/quality score
 -> Human approval when required
 -> Publish as skill/memory/procedure
 -> Version + provenance
 -> Evaluate future performance
 -> Promote / rollback
```

Output Auto-Learn tidak langsung mendapatkan privilege baru. Skill hasil belajar mewarisi batas permission bot dan tool/MCP baru tetap membutuhkan installation/approval tersendiri.

## 9.2 Learning modes
- `off`
- `memory-only`
- `suggest-skills`
- `auto-create-skills-sandboxed`
- `managed-enterprise`

## 9.3 Anti-poisoning
- source provenance;
- trust score;
- prompt-injection scanning;
- never learn secrets;
- deterministic static checks;
- sandbox trial;
- signed revisions;
- rollback;
- learning audit log.

---

# 10. Skills System

Marbots Skill format kompatibel sebanyak mungkin dengan Claude/Agent Skills style dan OpenClaw-style directory skill.

```text
my-skill/
├── SKILL.md
├── scripts/
├── references/
├── templates/
├── assets/
└── tests/
```

Example frontmatter:

```yaml
---
name: dotnet-api-review
version: 1.2.0
description: Review .NET APIs for correctness and performance
entry: SKILL.md
requires:
  tools: [read, grep]
  mcp: []
permissions:
  network: false
  shell: false
---
```

## 10.1 Progressive disclosure
Registry hanya memasukkan metadata pendek ke context. Full `SKILL.md` atau resource dibaca saat skill dipilih. Ini penting untuk token efficiency.

## 10.2 Skill Gallery
Sources:
- curated/trusted Marbots registry;
- compatible Claude/Agent Skills sources;
- OpenClaw/ClawHub compatible sources;
- Git repositories;
- local folder;
- private enterprise registry.

### Trust labels
- `Marbots Verified`
- `Trusted Publisher`
- `Community`
- `Local`
- `Unverified`

Install flow wajib menampilkan scripts, executable actions, requested permissions, outbound network needs dan checksum/signature sebelum activation.

---

# 11. MCP Integration

Per bot, user dapat memilih MCP server yang diizinkan.

### MCP Gallery
Menampilkan:
- name/publisher;
- transport;
- tools/resources/prompts exposed;
- install source/version;
- trust level;
- permissions;
- required secrets;
- compatible platforms;
- health status;
- latency.

### Configuration model

```yaml
mcp:
  github:
    enabled: true
    transport: stdio
    permissionProfile: developer
  company-db:
    enabled: true
    transport: http
    endpoint: https://mcp.example.internal
    permissionProfile: readonly
```

Secret tidak disimpan di manifest export. Export hanya menyimpan secret references.

---

# 12. Default Kernel Functions

Kernel Function adalah built-in capability dengan strongly typed schema, mirip konsep tool/function pada agent kernel.

Recommended packs:

### Filesystem
`Read`, `Write`, `Edit`, `Patch`, `Glob`, `List`, `Move`, `Copy`, `Delete`, `Watch`

### Search / Code Intelligence
`Grep`, `LspDefinition`, `LspReferences`, `LspHover`, `LspDiagnostics`, `TreeSitterQuery`

### Shell / Process
`Bash`, `PowerShell`, `Exec`, `BackgroundProcess`, `KillProcess`, `ProcessStatus`

### Web
`WebFetch`, `WebSearch`, `Download`, allowlisted HTTP client.

### Browser / Playwright
navigation, DOM query, inspect, screenshot, forms, controlled download/upload, browser console/network inspection.

### Computer Use
screen observe, mouse, keyboard, window focus, screenshot, accessibility-tree integration where supported.

### Agent
`DelegateTask`, `SpawnEphemeralAgent`, `SendToAgent`, `WaitAgent`, `CancelAgentTask`, `GetAgentStatus`.

### Planning/Productivity
`TodoRead`, `TodoWrite`, `Reminder`, `ScheduleJob`, `AskUserQuestion`, `Plan`, `Checkpoint`, `Rewind`.

### Artifacts
create/read/version artifact; artifact metadata and signed result bundles.

### Security
`RequestApproval`, credential broker access, scoped secret lease.

Setiap function mempunyai schema, permission category, risk level, timeout, cancellation token, telemetry hooks dan audit event.

---

# 13. Model Provider Abstraction

Marbots tidak boleh bergantung pada satu model/vendor.

`IModelProvider` abstraction:
- chat/completion;
- streaming;
- tool calling;
- structured output;
- reasoning tokens where available;
- images/audio where available;
- prompt caching metadata;
- cost/token telemetry;
- retry/fallback;
- capability discovery.

Model Profile dapat mempunyai routing policy:

```yaml
modelProfile:
  primary: azure-openai/deployment-a
  fallback:
    - anthropic/claude-x
    - local/ollama-model
  limits:
    maxCostPerJob: 2.00
    maxOutputTokens: 12000
```

Boss Man dapat menggunakan model yang berbeda dari worker bots.

---

# 14. Multi-Agent Collaboration

## 14.1 Internal collaboration
Untuk deployment internal, transport berperforma tinggi dapat memakai gRPC/HTTP antara Control Plane dan BotAgent runtime.

## 14.2 A2A
Expose A2A endpoint secara optional per bot/workspace untuk interoperabilitas. A2A specification mendukung capability discovery melalui Agent Card, task/message exchange, streaming dan async operation; ini cocok untuk komunikasi dengan agent eksternal maupun antar-host. [Source: A2A Protocol, accessed 2026-10-06]

```text
MCP : Bot <-> Tool/Data
A2A : Bot <-> Bot
API : App/User <-> Marbots Control Plane
```

## 14.3 Delegation envelope

```json
{
  "taskId": "task_123",
  "parentTaskId": "task_root",
  "from": "boss-man",
  "to": "researcher",
  "objective": "Compare three products",
  "constraints": ["Cite sources"],
  "budget": { "seconds": 600, "usd": 1.5 },
  "artifacts": [],
  "traceId": "trace_abc"
}
```

---

# 15. Task Graph, Todo, Loop and Scheduling

Marbots mempunyai persistent **Task Engine** terpisah dari chat thread.

### Task types
- one-shot;
- scheduled once;
- cron/recurring;
- interval/loop;
- event-triggered;
- webhook-triggered;
- dependency-driven DAG;
- reminder;
- approval task.

### Scheduler features
- timezone aware;
- misfire policy;
- retry/backoff;
- max concurrency;
- idempotency key;
- distributed lease;
- timeout;
- calendar view;
- pause/resume;
- history;
- manual re-run.

Example:

```text
"Setiap Senin jam 08:00, minta Analyst Bot membuat ringkasan KPI,
 kemudian Finance Bot mereview angka, setelah itu kirim ke Teams."
```

---

# 16. Host & BotAgent Deployment Model

## 16.1 Host types
- Local PC, default.
- Remote bare-metal/PC.
- VM.
- Docker/container host.
- Future: Kubernetes pool/cloud ephemeral runner.

## 16.2 Architecture

```text
                    Marbots Control Plane
                    + Boss Man + Registry
                            |
             secure mTLS control/data connection
           +----------------+----------------+
           |                |                |
       Local Host         VM Host        Docker Host
       BotAgentSvc        BotAgentSvc    BotAgentSvc
       /    |    \          /   \          /   \
    Bot A Bot B Bot C    Bot D Bot E    Bot F Bot G
```

Satu `BotAgent Host Service` mengelola banyak runtime instance. Masing-masing bot mendapat:
- process/container boundary sesuai profile;
- per-bot workspace;
- independent session store;
- separate cancellation;
- CPU/memory quotas bila platform mendukung;
- environment variable scope;
- permission set;
- logs/metrics;
- optional browser profile.

## 16.3 Remote bootstrap via SSH

Ketika host belum terdaftar:
1. user memilih host type dan memberikan SSH/remote bootstrap access;
2. Boss Man/Deployment Service melakukan host preflight;
3. verify OS/arch/runtime/resources;
4. install/upgrade `marbots-agent` service;
5. generate host identity;
6. establish mTLS trust;
7. store credential melalui secrets vault, bukan plaintext config;
8. register host;
9. deploy bot runtime;
10. stream health/provisioning status.

**Security improvement:** prefer one-time enrollment token + generated host certificate. Password/private key SSH hanya dipakai pada bootstrap dan tidak perlu dipertahankan bila policy tidak membutuhkan remote SSH lagi.

Bila `BotAgentSvc` sudah ada pada host, deployment cukup mengirim Bot Manifest + required packages dan runtime instance baru dibuat.

## 16.4 Offline/reconnect
BotAgent menyimpan bounded local job state. Setelah koneksi kembali, event disinkronkan secara idempotent.

---

# 17. Bot Export / Import

Extension disarankan: `.marbot`.

Secara fisik ZIP package:

```text
researcher.marbot
├── manifest.yaml
├── persona.md
├── skills.lock.json
├── mcp.lock.json
├── model-profile.json
├── permissions.json
├── schedules.json
├── assets/
├── exported-skills/
└── checksums.json
```

**Tidak boleh ikut export secara default:** API key, access token, private key, browser cookies, raw credential, protected enterprise memory.

Optional export:
- long-term memory yang sudah disanitasi;
- conversation templates;
- local skills;
- avatar/assets;
- schedule definitions.

Import paths:

```bash
marbots bot import ./researcher.marbot
marbots bot export researcher -o ./researcher.marbot
```

UI menggunakan drag-and-drop; SDK menyediakan stream/file API.

Manifest harus versioned dan mendukung migration.

---

# 18. User Experience Surfaces

## 18.1 CLI/TUI

Tujuan UX: expressive seperti modern agent coding CLI tetapi menjadi control center multi-agent.

```text
marbots
marbots chat boss-man
marbots chat researcher
marbots bots
marbots hosts
marbots tasks
marbots mcp
marbots skills
marbots logs --follow bot-123
marbots theme set aurora
```

Themes:
- Dark Default
- Light
- Aurora
- Matrix
- Mono
- High Contrast
- custom JSON theme

Configurable:
- colors;
- Unicode/ASCII/Nerd Font glyphs;
- spinner animation;
- compact/comfortable density;
- syntax theme;
- dashboard widgets.

## 18.2 Web
**Blazor (.NET 10)**. Untuk realtime gunakan SignalR/WebSockets. Web menjadi administration/control plane UI lengkap.

## 18.3 Desktop
**Avalonia** untuk Windows/macOS/Linux. Desktop dapat menjalankan embedded/local runtime dan terhubung ke remote Control Plane. Three.Net Avalonia integration ideal untuk Office 3D. [Source: Three.Net repository, accessed 2026-10-06]

## 18.4 Mobile
**.NET MAUI Hybrid** untuk Android/iOS. Prioritas mobile:
- chat;
- task approvals;
- push notifications;
- activity monitoring;
- quick create/delegate;
- voice/image attachments;
- lightweight 3D optional berdasarkan device capability.

## 18.5 Headless
Server/API + SDK tanpa UI. Cocok untuk automation, CI/CD, SaaS embedding dan enterprise integration.

---

# 19. Main UI Information Architecture

```text
Marbots
├── Chat
│   ├── Boss Man
│   ├── Bot A
│   └── Bot B
├── Bots
│   ├── Gallery/List
│   ├── Create
│   ├── Detail
│   └── Import/Export
├── Tasks
│   ├── Active
│   ├── Scheduled
│   ├── Todo
│   └── History
├── Hosts
├── Skills Gallery
├── MCP Gallery
├── Channels
├── Office 3D
├── Activity / Audit
├── Usage & Cost
└── Settings
```

---

# 20. Chat UX

Layout utama mengikuti pola messenger/agent teammate:

```text
+-----------------------+---------------------------------------+
| BOTS                  | # Thread: Website Release             |
|                       |                                       |
| * Boss Man      LIVE  | User                                  |
| * Alice         BUSY  | Make release ready                    |
| * Atlas         IDLE  |                                       |
| * Quinn         BUSY  | Boss Man                              |
|                       | Delegated 2 tasks...                   |
| + New Bot             | [Atlas: research............. 73%]    |
|                       | [Quinn: testing.............. 41%]    |
|                       |                                       |
|                       | [ message........................ ]    |
+-----------------------+---------------------------------------+
```

### Thread functions
- create/new thread;
- rename;
- pin;
- archive;
- save/snapshot;
- reset context;
- rewind/checkpoint;
- branch thread;
- export transcript;
- search;
- inspect raw task/tool timeline;
- attach files/images;
- voice input where appropriate.

**Reset** tidak menghapus long-term memory kecuali user memilih “Reset thread + forget learned memory”.

---

# 21. Bot Creation UI

Form fields:
- Name
- Avatar
- Description
- Persona/System Prompt/Instructions
- Default Model Profile
- Short-Term Memory
- Long-Term Memory
- Context Compaction strategy
- Auto-Learn `Yes/No` + mode
- MCP selection
- Skills selection
- Kernel Functions
- Host
- CPU/RAM profile
- Browser/computer-use enabled
- Permission profile
- Schedules
- Channels
- Advanced environment/settings

Live panel kanan menampilkan **effective capabilities** dan **security warnings** sebelum bot dibuat.

---

# 22. BotAgent Dashboard

## Fleet overview
Cards:
- connected hosts;
- online/offline agents;
- active tasks;
- queued tasks;
- tool calls/min;
- token consumption;
- estimated cost;
- CPU/RAM;
- failures/retries;
- approvals waiting.

## Host detail
- OS/architecture;
- agent service version;
- uptime;
- connectivity/latency;
- CPU/RAM/disk;
- bot instances;
- container/process IDs;
- version drift;
- pending update;
- logs/events.

## Bot detail
- current objective;
- current step/tool;
- delegated tasks;
- runtime duration;
- token/cost;
- current host;
- memory/skill activity;
- live logs;
- recent failures;
- controls: pause/resume/cancel/restart/migrate.

---

# 23. 3D Office Simulation

Office 3D adalah **observability visualization**, bukan sekadar gimmick.

Setiap bot divisualkan sebagai character di kantor virtual. Aktivitas runtime dipetakan ke workplace metaphor:

| Agent activity | 3D metaphor |
|---|---|
| Idle | duduk/idle di meja |
| Thinking | membaca/berpikir di desk |
| Coding | mengetik di workstation |
| Web research | browser/research station |
| Delegating | berjalan/berbicara dengan bot lain |
| A2A communication | meeting / visual signal |
| MCP/tool use | equipment/tool station |
| Long task | fokus bekerja dengan progress indicator |
| Waiting approval | berdiri dekat approval desk |
| Error | warning indicator |
| Completed | delivery interaction / return to desk |

Klik bot membuka contextual panel:
- identity/persona;
- state;
- task;
- progress;
- elapsed time;
- current tool/skill;
- host;
- sanitized live logs;
- token/cost;
- pause/cancel/chat action.

## 23.1 Technology

**Three.Net** adalah recommended rendering engine karena tersedia .NET 10 binding, Rust/wgpu native core, Avalonia control, skeletal animation, physics, PBR, spatial audio dan multiplatform GPU backends. [Source: Three.Net repository, accessed 2026-10-06]

**Blender + Blender MCP workflow** digunakan pada asset pipeline:
- office environment modeling;
- bot character base meshes;
- rigging;
- animation clips;
- LOD generation;
- bake/export;
- glTF/compatible asset output.

Runtime **tidak bergantung pada Blender**. Blender hanya authoring pipeline.

## 23.2 Performance targets
- LOD per character;
- GPU instancing untuk shared assets;
- baked lighting where useful;
- animation culling off-screen;
- event aggregation;
- max visible bot budget;
- fallback 2D map untuk low-power/mobile/web environments.

---

# 24. External Channels

Channel Gateway menormalisasi pesan dari:
- WhatsApp;
- Telegram;
- Slack;
- Facebook/Messenger where supported;
- Microsoft Teams;
- Discord;
- Signal where integration permits;
- Email;
- WebChat;
- custom webhook/channel plugin.

OpenClaw saat ini mendokumentasikan integrated multi-channel gateway untuk banyak pesan populer; Marbots mengikuti pola adapter serupa agar core agent tidak mengetahui implementation channel. [Source: OpenClaw documentation, accessed 2026-10-06]

### Normalized event

```json
{
  "channel": "teams",
  "sender": "user-42",
  "agent": "boss-man",
  "thread": "channel-thread-id",
  "text": "status release?",
  "attachments": [],
  "receivedAt": "..."
}
```

Routing rules dapat mengarahkan channel langsung ke bot tertentu atau ke Boss Man.

---

# 25. SDK & Public API

Official SDKs:
- `.NET`
- `Go`
- `Python`
- `TypeScript/Node.js`

API paradigms:
- HTTP/REST untuk resource management;
- WebSocket/SSE/gRPC streaming untuk live events;
- optional gRPC untuk high-performance internal/SDK scenarios;
- A2A untuk inter-agent interoperability.

Core abstractions:
- `MarbotsClient`
- `BotsClient`
- `ChatClient`
- `TasksClient`
- `HostsClient`
- `SkillsClient`
- `McpClient`
- `ChannelsClient`
- `EventsClient`

SDK requirements:
- async first;
- streaming;
- cancellation;
- retry guidance;
- idempotency;
- generated strongly typed contracts where possible;
- semantic versioning;
- examples in both English and Indonesian docs.

---

# 26. Proposed .NET 10 Solution Structure

```text
Marbots/
├── src/
│   ├── Marbots.Abstractions/
│   ├── Marbots.Domain/
│   ├── Marbots.Application/
│   ├── Marbots.ControlPlane/
│   ├── Marbots.Orchestration/
│   ├── Marbots.AgentRuntime/
│   ├── Marbots.AgentHost/
│   ├── Marbots.Memory/
│   ├── Marbots.Context/
│   ├── Marbots.AutoLearn/
│   ├── Marbots.Skills/
│   ├── Marbots.Mcp/
│   ├── Marbots.A2A/
│   ├── Marbots.Kernel/
│   ├── Marbots.Kernel.Files/
│   ├── Marbots.Kernel.Shell/
│   ├── Marbots.Kernel.Web/
│   ├── Marbots.Kernel.Browser/
│   ├── Marbots.Kernel.ComputerUse/
│   ├── Marbots.Scheduling/
│   ├── Marbots.HostManagement/
│   ├── Marbots.Deployment/
│   ├── Marbots.Channels/
│   ├── Marbots.Security/
│   ├── Marbots.Observability/
│   ├── Marbots.Storage/
│   ├── Marbots.Api/
│   ├── Marbots.Cli/
│   ├── Marbots.Web/
│   ├── Marbots.Desktop/
│   ├── Marbots.Mobile/
│   └── Marbots.Office3D/
├── sdk/
│   ├── dotnet/
│   ├── go/
│   ├── python/
│   └── typescript/
├── native/
│   └── marbots-native/       # optional Rust hot paths
├── protocols/
│   ├── protobuf/
│   └── schemas/
├── docs/
│   ├── en/
│   └── id/
├── samples/
├── tests/
│   ├── unit/
│   ├── integration/
│   ├── contract/
│   ├── e2e/
│   ├── performance/
│   └── security/
└── tools/
```

---

# 27. Logical Services

Untuk MVP gunakan **modular monolith control plane + separate AgentHost process**, bukan microservices berlebihan. Boundary dibuat jelas sehingga service dapat dipecah bila scale membutuhkannya.

### Control Plane modules
- Identity/Tenant
- Bot Registry
- Boss Man Orchestrator
- Task Service
- Host Registry
- Deployment
- Skill/MCP Registry
- Schedule Service
- Channel Gateway
- Memory Control
- Usage/Billing Meter
- Audit/Policy
- Event Stream

### Data Plane
- BotAgent Host Service
- Bot runtime instances
- browser/computer runner
- MCP processes/connections
- local workspace
- optional sandbox/container runtime

---

# 28. Storage Architecture

Recommended defaults:

### Relational database
PostgreSQL for server deployment; SQLite for single-user/local profile where appropriate.

Stores:
- bot config;
- task state;
- schedules;
- hosts;
- registry metadata;
- permissions;
- audit pointers;
- channel/config metadata.

### Vector / semantic memory
Pluggable `IVectorStore`. PostgreSQL vector extension can reduce operational complexity initially; dedicated vector DB optional later.

### Object/artifact storage
Local filesystem in local mode; S3-compatible object storage in server mode.

### Event/cache
Start with in-process channels + persistent outbox. Add Redis/NATS/Kafka only when distributed scale justifies it.

---

# 29. Security Architecture

Agent platform mempunyai attack surface tinggi karena LLM dapat mengeksekusi tools. Security harus menjadi architectural primitive.

## 29.1 Permission model
Tool actions dikelompokkan:
- read-only;
- workspace-write;
- network;
- process execution;
- destructive filesystem;
- browser authenticated actions;
- secret use;
- external communication;
- monetary/irreversible actions;
- admin/deployment.

Policy result:
- `Allow`
- `Deny`
- `Ask`
- `AllowOnce`
- `AllowForSession`

## 29.2 Approval gates
Mandatory configurable approval untuk:
- send external message;
- financial transaction;
- delete outside workspace;
- production deployment;
- permission elevation;
- new MCP/tool install;
- auto-learn publishing executable skill;
- new host enrollment;
- secret exposure.

## 29.3 Secrets
Gunakan `ISecretProvider`:
- OS secure store/keychain;
- enterprise vault;
- cloud Key Vault/Secrets Manager;
- environment only for ephemeral local dev.

LLM menerima opaque secret capability atau short-lived lease jika memungkinkan, bukan secret plaintext di prompt.

## 29.4 Host security
- mTLS;
- per-host identity;
- certificate rotation;
- signed updates;
- least-privilege service account;
- sandbox profiles;
- network egress control;
- audit trail.

## 29.5 Prompt injection boundaries
Content dari web/file/channel dianggap **untrusted data**, bukan instruction dengan priority sama seperti system/user policy. Tool invocation tetap divalidasi oleh deterministic Policy Engine di luar LLM.

---

# 30. Observability

Gunakan OpenTelemetry-oriented instrumentation.

### Trace hierarchy

```text
UserRequest
  -> BossManPlan
    -> DelegatedTask:Researcher
       -> LLM Call
       -> MCP Tool Call
    -> DelegatedTask:Developer
       -> LLM Call
       -> Kernel:Bash
       -> Kernel:Edit
  -> Synthesis
```

Metrics:
- task latency;
- queue depth;
- completion/failure rate;
- p50/p95/p99 tool latency;
- tokens in/out;
- cache hit;
- model cost;
- memory retrieval latency;
- context compaction count;
- host CPU/RAM;
- bot concurrency;
- MCP errors;
- channel delivery latency.

Logs wajib structured, correlation-id aware dan mempunyai secret/PII redaction pipeline.

---

# 31. Performance & Memory Efficiency

## .NET 10 guidelines
- async I/O end-to-end;
- bounded `Channel<T>` untuk event pipelines;
- avoid unbounded queues;
- cancellation token di semua long operations;
- streaming JSON untuk large payloads;
- source-generated JSON serialization;
- pooled buffers/`ArrayPool<T>` hanya pada measured hot paths;
- avoid unnecessary LINQ allocations di inner loops;
- HTTP connection pooling;
- gRPC streaming untuk telemetry/high-frequency internal messaging jika dibutuhkan;
- immutable configuration snapshot;
- batching telemetry/memory writes;
- bounded caches with expiry;
- no full transcript duplication across layers.

## NativeAOT
Pertimbangkan untuk CLI, small host bootstrapper atau lightweight worker components bila dependency graph mendukung.

## Rust
Gunakan hanya bila profiling memperlihatkan keuntungan nyata, contohnya:
- terminal parser/high-throughput text search;
- native computer-capture pipeline;
- GPU/3D via Three.Net's existing Rust/wgpu core;
- tokenizer/embedding preprocessing khusus;
- sandbox/native OS integration.

Jangan memindahkan orchestration/business logic ke Rust tanpa alasan terukur.

---

# 32. Reliability

- append-only task events untuk recovery;
- optimistic concurrency/version fields;
- outbox/inbox pattern untuk distributed event delivery;
- idempotency keys;
- lease-based worker ownership;
- exponential retry + jitter;
- circuit breaker untuk external providers;
- model fallback;
- checkpointing agent state;
- task resume after process restart;
- graceful shutdown/drain;
- host heartbeat;
- dead-agent detection;
- automatic requeue berdasarkan policy.

Exactly-once tidak diasumsikan. Desain action menjadi **at-least-once safe melalui idempotency** bila memungkinkan.

---

# 33. Multi-Tenancy

Deployment modes:
1. Personal local.
2. Self-hosted team.
3. Enterprise multi-tenant.
4. Managed cloud, future.

Entities selalu membawa `TenantId/WorkspaceId`. Tenant isolation diterapkan di database, secrets, storage, event stream, memory dan host assignment.

Enterprise capabilities:
- SSO/OIDC;
- RBAC/ABAC;
- audit retention;
- DLP hooks;
- private MCP/skill registry;
- model allowlist;
- region/data residency;
- network policy;
- admin approval workflows.

---

# 34. Bot Lifecycle

```text
Draft
 -> Validating
 -> Provisioning
 -> Ready
 -> Running
 -> Paused
 -> Ready
 -> Updating
 -> Ready
 -> Archived
```

Failure side states:
- Degraded
- HostUnavailable
- Misconfigured
- PermissionBlocked

Operations:
- create;
- clone;
- update;
- pause;
- restart;
- move host;
- export;
- import;
- archive;
- delete with retention policy.

Boss Man itself: protected system bot, upgradeable but non-deletable.

---

# 35. Host Scheduling

Placement score example:

```text
score =
  capabilityMatch
  + availableMemory
  + cpuHeadroom
  + gpuRequirementMatch
  + dataLocality
  + affinity
  - estimatedCost
  - latency
  - currentLoad
```

Constraints hard-filter lebih dahulu, score diterapkan sesudahnya.

Affinity examples:
- browser-heavy bots pada host yang mempertahankan browser profile;
- confidential bot harus on-prem;
- GPU bot hanya pada GPU host;
- teammate bots boleh ditempatkan bersama untuk local fast path.

---

# 36. API Surface Example

```text
POST   /api/v1/bots
GET    /api/v1/bots
GET    /api/v1/bots/{id}
PATCH  /api/v1/bots/{id}
POST   /api/v1/bots/{id}/start
POST   /api/v1/bots/{id}/stop
POST   /api/v1/bots/{id}/export
POST   /api/v1/bots/import

POST   /api/v1/threads
POST   /api/v1/threads/{id}/messages
GET    /api/v1/threads/{id}/events
POST   /api/v1/threads/{id}/reset
POST   /api/v1/threads/{id}/checkpoint

POST   /api/v1/tasks
GET    /api/v1/tasks/{id}
POST   /api/v1/tasks/{id}/cancel

GET    /api/v1/hosts
POST   /api/v1/hosts/enroll
POST   /api/v1/hosts/{id}/deploy

GET    /api/v1/skills
POST   /api/v1/skills/install
GET    /api/v1/mcp
POST   /api/v1/mcp/install
```

API versioning wajib sejak awal.

---

# 37. Real-Time Event Model

Event examples:
- `BotCreated`
- `BotStateChanged`
- `AgentThinkingStarted`
- `ToolCallStarted`
- `ToolCallCompleted`
- `TaskDelegated`
- `TaskProgressed`
- `ApprovalRequested`
- `ApprovalResolved`
- `MemoryWritten`
- `SkillLoaded`
- `AutoLearnCandidateCreated`
- `HostConnected`
- `HostLost`
- `ScheduleTriggered`

Desktop/Web/3D hanya mengonsumsi event model yang sama, sehingga business logic visualisasi tidak bergantung pada internal agent loop.

---

# 38. Thread, Session and Context Semantics

Definitions:
- **Thread**: durable human-visible conversation.
- **Session**: runtime context window associated dengan thread/task.
- **Task**: durable unit of work.
- **Run**: one execution attempt.
- **Artifact**: produced file/data/result.
- **Memory**: durable retrievable knowledge.

`Reset Thread` membuat runtime context baru tetapi thread history dapat tetap tersimpan untuk audit. `Delete Thread` mengikuti retention policy. `Fork` membuat thread baru dari checkpoint tertentu.

---

# 39. Human-in-the-Loop

Boss Man dan worker dapat menampilkan approval card:

```text
Developer Bot wants to run:
  Deploy production release v2.4.0

Target: prod-cluster-01
Risk: High
Reason: completes task #1234

[Approve once] [Approve task] [Reject] [Inspect plan]
```

Approval dapat dilakukan dari Web, Desktop, Mobile, CLI atau channel yang mendukung authenticated interactive action.

---

# 40. Gallery Architecture

Skill dan MCP Gallery diperlakukan sebagai **catalog aggregator**, bukan blind package installer.

Pipeline:

```text
Source Adapters
 -> Catalog Normalizer
 -> Metadata Cache
 -> Malware/Static Scan
 -> Reputation/Trust
 -> Compatibility Check
 -> Gallery Search
 -> Permission Review
 -> Install/Pin Version
```

Setiap entry memiliki source attribution dan license metadata.

---

# 41. Updates

Components versioned independen:
- Control Plane;
- BotAgent Host;
- UI clients;
- skill runtime schema;
- `.marbot` schema;
- SDKs;
- protocol contracts.

Host dashboard menampilkan drift. Rolling update wajib tidak memutus task aktif; host dapat drain lalu update.

---

# 42. Testing Strategy

## Unit
- routing rules;
- task state machine;
- compaction;
- memory filtering;
- permission evaluation;
- manifest validation.

## Contract
- SDK/API;
- A2A;
- MCP transport;
- AgentHost protocol;
- channel adapters.

## Integration
- model adapters;
- storage;
- browser;
- Docker;
- SSH enrollment;
- scheduler.

## E2E
- create bot -> deploy -> chat -> delegate -> tool -> result;
- multi-host parallel delegation;
- host disconnect/recovery;
- import/export;
- channel message -> Boss Man -> bot -> reply;
- auto-learn approval flow.

## Performance
- 10/100/1,000 bot logical configurations;
- concurrent active runs;
- event fan-out;
- streaming throughput;
- memory retrieval p95;
- dashboard overhead;
- 3D visualization load.

## Security
- prompt injection corpus;
- malicious skills;
- MCP privilege escalation;
- path traversal;
- shell escaping;
- secret redaction;
- tenant isolation;
- replay/forged host events.

---

# 43. Documentation Strategy, English + Bahasa Indonesia

```text
docs/
├── en/
│   ├── getting-started/
│   ├── concepts/
│   ├── bots/
│   ├── boss-man/
│   ├── hosts/
│   ├── skills/
│   ├── mcp/
│   ├── a2a/
│   ├── channels/
│   ├── sdk/
│   ├── security/
│   └── troubleshooting/
└── id/
    └── (same information architecture)
```

Documentation requirements:
- setiap public feature memiliki EN + ID page;
- runnable examples;
- CLI examples;
- screenshots untuk GUI workflows;
- Mermaid diagrams where useful;
- API reference autogenerated dari source contracts;
- code snippets diuji di CI;
- terminology glossary consistent;
- migration guide setiap breaking change.

---

# 44. Example Primary Workflow

## Use Case: Delegated software delivery
1. User membuka Marbots Desktop.
2. Chat ke Boss Man: “Buat service import CSV sesuai issue #512 dan test.”
3. Boss Man membaca available agent capabilities.
4. Planner menghasilkan DAG: research issue -> implementation -> review -> test.
5. Developer Bot pada local PC mengerjakan code.
6. Reviewer Bot pada VM membaca diff.
7. QA Bot pada Docker host menjalankan tests/Playwright.
8. Boss Man menerima events dan menggabungkan status.
9. Jika publish/deploy diperlukan, approval card tampil.
10. User approve via mobile.
11. Deployment selesai.
12. Final artifact/report masuk thread.
13. Auto-Learn, jika aktif, mengusulkan skill “repository release checklist”.
14. User review lalu publish skill.

---

# 45. Example Distributed Workflow

```text
                        USER
                          |
                          v
                     [Boss Man]
                    /     |      \
                   /      |       \
          A2A/task    A2A/task    A2A/task
             v           v           v
       [Research]    [Developer]    [QA]
         VM-A          PC Local    Docker-B
           |              |           |
          MCP           Kernel       Browser
       Search/DB      Files/Shell   Playwright
           \              |          /
            +------- Artifacts ------+
                         |
                         v
                    [Boss Man]
                         |
                   synthesized
                      result
```

---

# 46. MVP Definition

## Phase 0: Foundations
- .NET 10 solution;
- Bot domain model;
- event model;
- model abstraction;
- tool abstraction;
- local storage;
- security/policy core.

## Phase 1: Boss Man + Local Bots
- Boss Man persistent default;
- bot CRUD;
- direct chat;
- delegation;
- parallel bots;
- short/long memory;
- context compaction;
- basic skills + MCP;
- filesystem/shell/web/browser kernel functions;
- CLI;
- Blazor Web UI.

## Phase 2: Distributed BotAgent
- Host registry;
- SSH enrollment;
- AgentHost service;
- VM/Docker runners;
- host sharing + runtime isolation;
- deployment/update;
- dashboard/telemetry.

## Phase 3: Productivity + Ecosystem
- scheduler/loops/reminders;
- Skill Gallery;
- MCP Gallery;
- import/export `.marbot`;
- A2A;
- channels;
- official SDKs.

## Phase 4: Rich Clients
- Avalonia Desktop;
- MAUI Hybrid Android/iOS;
- advanced chat UX;
- push approvals.

## Phase 5: Auto-Learn + 3D
- governed Auto-Learn;
- Blender authoring pipeline;
- Three.Net Office 3D;
- live event mapping;
- scale/LOD optimization.

---

# 47. MVP Acceptance Criteria

Minimum release-worthy outcome:
- Boss Man always exists and can delegate to at least two bots concurrently.
- User can create bot from chat, CLI and Web UI.
- Bot supports persona, memory, compaction, skills, MCP, kernel functions.
- Bot can run locally or on registered remote host.
- Remote provision is secure and observable.
- Multiple bots can coexist on one host without session/workspace collisions.
- Task state survives process restart.
- User can chat directly to a worker bot.
- Permission prompt exists for risky actions.
- Bot export/import strips secrets.
- Scheduler supports one-time + cron jobs.
- Web dashboard shows live agent/host state.
- Public API contracts exist and .NET SDK is delivered first.
- English and Indonesian Getting Started documentation passes CI link/sample checks.

---

# 48. Non-Goals for First Release

- Training foundation models.
- Full Kubernetes scheduler replacement.
- Building a new 3D engine from scratch.
- Reinventing MCP or A2A.
- Automatically trusting community skills.
- Unlimited autonomous self-modification.
- Storing raw secrets inside bot manifests/memory.
- Premature microservice decomposition.

---

# 49. Important Architecture Decisions

### ADR-001: .NET 10 is the primary platform
Rationale: shared domain/runtime/API code, strong async runtime, cross-platform hosting, Blazor/Avalonia/MAUI ecosystem.

### ADR-002: Boss Man is a protected system agent
Rationale: ensures a stable universal orchestration entry point.

### ADR-003: Bot logical identity is separated from runtime instance
Rationale: enables restart, migration, scaling and remote hosts without changing Bot identity.

### ADR-004: MCP for tools, A2A for agents
Rationale: clear interoperability contracts and avoids proprietary coupling.

### ADR-005: Modular monolith first
Rationale: minimizes operational complexity while preserving hard module boundaries.

### ADR-006: Event-oriented observability
Rationale: Web, Desktop, Mobile, CLI and 3D consume the same lifecycle events.

### ADR-007: Auto-Learn cannot elevate privileges
Rationale: learned behavior is untrusted until validated and cannot bypass deterministic policy.

### ADR-008: Three.Net for native 3D
Rationale: aligned with .NET 10/Avalonia stack and already backed by Rust/wgpu native rendering.

---

# 50. Suggested Internal Interfaces

```csharp
public interface IAgentRuntime
{
    IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRequest request,
        CancellationToken cancellationToken);
}

public interface IAgentRouter
{
    ValueTask<AgentRoute> RouteAsync(
        TaskIntent intent,
        IReadOnlyList<AgentCapability> candidates,
        CancellationToken cancellationToken);
}

public interface IKernelFunction
{
    FunctionDescriptor Descriptor { get; }
    ValueTask<FunctionResult> InvokeAsync(
        FunctionCall call,
        FunctionExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IMemoryStore
{
    ValueTask WriteAsync(MemoryRecord record, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MemoryMatch>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken);
}

public interface IPolicyEngine
{
    ValueTask<PolicyDecision> EvaluateAsync(
        ActionRequest action,
        SecurityContext context,
        CancellationToken cancellationToken);
}
```

Interface harus kecil, composable dan tidak expose concerns dari provider tertentu.

---

# 51. Recommended Repository Conventions

```text
/docs/adr/ADR-xxxx-title.md
/docs/en/...
/docs/id/...
/schemas/bot-manifest.schema.json
/schemas/skill-manifest.schema.json
/protocols/agenthost.proto
```

Mandatory engineering policies:
- nullable enabled;
- analyzers as warnings/errors sesuai severity;
- deterministic builds;
- central package management;
- dependency lock/pinning for release;
- code formatting CI;
- public API compatibility tests;
- SBOM;
- signed releases;
- benchmark regression suite pada hot paths.

---

# 52. Branding & Product Language

**Marbots** = **Marvelous Bots**.

Suggested vocabulary:
- Bot = durable AI teammate.
- Boss Man = default manager/orchestrator.
- Office = collective workspace.
- Host = machine/container environment running BotAgent instances.
- Skill = reusable instruction/resource package.
- Tool/Kernel Function = executable capability.
- Channel = human messaging transport.
- Task = durable unit of assigned work.
- Thread = conversation history.

Suggested tagline:

> **Marbots — Your marvelous team of AI coworkers.**

Bahasa Indonesia:

> **Marbots — Tim rekan kerja AI yang bisa Anda bentuk, ajari, dan orkestrasi.**

---

# 53. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Agent runaway/tool loop | step/time/cost budget, cancellation, policy engine |
| Community skill supply-chain risk | scanning, trust tier, signature, permission review |
| Prompt injection | untrusted-content boundary + deterministic policy |
| Secret leakage | secret broker, redaction, no plaintext export |
| Host compromise | instance isolation, least privilege, mTLS, egress control |
| Memory poisoning | provenance, confidence, review, rollback |
| Excessive token cost | compaction, progressive skills, caching, budget routing |
| Over-orchestration | direct bot chat + configurable delegation modes |
| UI event overload | server-side aggregation, sampling, backpressure |
| 3D consumes resources | separate renderer, LOD, capped FPS, disable option |
| Provider outage | adapters, retries, fallback profiles |
| Distributed duplicate execution | lease + idempotency design |

---

# 54. Success Metrics

Product:
- task completion success rate;
- delegated task success rate;
- time-to-first-useful-result;
- human interventions per completed job;
- reusable skill adoption;
- schedule success;
- import/export success.

Engineering:
- Control Plane p95 API latency;
- BotAgent event delivery p95;
- crash-free sessions;
- host reconnect recovery time;
- memory retrieval p95;
- context size reduction after compaction;
- UI memory footprint;
- idle AgentHost footprint;
- 3D FPS/frame time profile.

Safety/trust:
- denied risky actions;
- approval bypass incidents, target 0;
- secret leakage incidents, target 0;
- untrusted skill detection;
- audit completeness.

---

# 55. Recommended First Implementation Slice

Bangun satu vertical slice sebelum memperluas surface area:

```text
Blazor Chat
  -> Boss Man
  -> local Bot Registry
  -> create 2 worker bots
  -> Boss Man runs parallel delegation
  -> worker uses filesystem + web + MCP
  -> durable Task Events
  -> live SignalR timeline
  -> short/long memory
  -> permission approval
  -> completion synthesis
```

Setelah slice ini stabil, ekstrak `BotAgent Host` menjadi remote-capable process. Pendekatan ini memvalidasi abstraction penting tanpa membangun CLI, mobile, channel, gallery dan 3D terlalu dini.

---

# 56. Reference Notes

Referensi yang diverifikasi pada **6 Oktober 2026**:

1. **DotCode** — GitHub `DotNetVibeCoderz/Vibe_Dev/tree/main/DotCode`. README mendeskripsikan .NET 10 agentic coding harness, built-in toolbox, skills, MCP, JSON-RPC headless mode dan SDK lintas bahasa.
2. **Three.Net** — GitHub `DotNetVibeCoderz/Vibe_Graphics/tree/main/ThreeNet`. README mendeskripsikan .NET 10 C# binding di atas Rust/wgpu, Avalonia control, animation, physics, audio dan multiplatform graphics.
3. **OpenClaw documentation** — `docs.openclaw.ai`. Referensi untuk multi-channel gateway, multi-agent routing, skills dan automation.
4. **Hermes Agent Skills** — `hermes-agent.nousresearch.com`. Referensi untuk on-demand skills/progressive disclosure dan ecosystem compatibility.
5. **Grok Bot documentation** — `docs.x.ai/grok-bot/overview`. Referensi untuk persistent named bots, computer-use oriented workers, parallel work dan long-lived context.
6. **A2A Protocol** — `a2a-protocol.org/latest/specification/`. Referensi untuk interoperabilitas agent, Agent Card, collaboration/task models dan asynchronous/streaming interactions.

> Catatan: Marbots harus mengimplementasikan integrasi pihak ketiga sesuai lisensi, terms of service, API support dan trademark rules masing-masing. “Compatibility” berarti format/protocol compatibility yang legal dan terdokumentasi, bukan menyalin proprietary implementation.

---

# 57. Final Architecture Summary

```text
                       +----------------------+
                       |       USERS          |
                       +----------+-----------+
                                  |
       +-------------+------------+------------+--------------+
       |             |                         |              |
      CLI        Blazor Web              Avalonia        MAUI Hybrid
                                                  Desktop   Mobile
       +-------------+------------+------------+--------------+
                                  |
                         Marbots Control Plane
       +------------------------------------------------------+
       | Boss Man | Registry | Tasks | Policy | Scheduler     |
       | Memory   | Deploy   | Hosts | Channels | Observability|
       +------------+-----------------------------+-----------+
                    |                             |
                   A2A                     Channel Gateway
                    |
       +------------+-----------------------+
       |                                    |
   BotAgent Host A                         BotAgent Host B
   Local PC                                VM / Docker
   +--------------------+                  +------------------+
   | Bot A | Bot B      |                  | Bot C | Bot D    |
   | Skill | MCP | Tools|                  | Skill | MCP      |
   +--------------------+                  +------------------+
       |                                    |
       +---- MCP -> Tools/Data               +---- MCP -> Tools/Data
       +---- Browser/Computer Use            +---- Kernel Functions
       +---- Memory/Artifacts                +---- Workspace

                 All runtime changes -> Event Stream
                             |
                  Dashboard + Logs + Office 3D
```

**Marbots** sebaiknya dimulai sebagai platform .NET 10 yang sederhana untuk dijalankan di satu PC, tetapi memiliki contract dan isolation yang sejak awal memungkinkan distribusi ke banyak host. Boss Man menjadi satu pintu orkestrasi yang konsisten; bot tetap dapat diakses langsung; MCP dan A2A menjadi interoperability backbone; AgentHost menjadi execution fabric; dan UI/3D dashboard menjadi jendela observability atas seluruh “kantor” agent.
