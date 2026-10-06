# Nusantara Kopi Tech — New Hire Onboarding

Date: 2026-10-06 · Author: Wren (onboarding guide)

## Introduction

Welcome to Nusantara Kopi Tech. Our mission is to build delightful, reliable software that connects Indonesian coffee growers, local roasters, and consumers using smart, humane technology. We’re a small, cross-functional team of 20 people: engineers, product, design, operations and customer success. We move fast, ship often, and care about respect, learning, and practical solutions.

Our culture is friendly, pragmatic, and inclusive. We expect everyone to treat colleagues with respect, ask questions early, and document decisions so knowledge stays with the team. We value clarity over cleverness and working code over perfection.

---

## 1) First week checklist

Goal: get productive and comfortable. Expected completion: end of Week 1. If anything is blocked, notify HR and your manager immediately.

Day 0 / Pre-arrival
- Confirm start date with HR (contact: HR: Sari — slack: @sari · email: sari@nusantarakopi.tech).
- Fill pre-onboarding paperwork: https://confluence.nusantarakopi.internal/onboarding-forms (placeholder).

Day 1 — Accounts & equipment
- Receive equipment (laptop + power adapter). If remote, confirm shipping (IT: Dedi — @dedi · it@nusantarakopi.tech).
- Sign into Google Workspace and set up company email.
- Join Slack (invite sent by HR). Confirm your Slack display name and timezone.

Day 2 — Access & docs
- Request GitHub access to core repositories (see Tools & accounts). Typical permission: read + triage; request write from your Team Lead.
- Request Jira access and assignment to your project board.
- Verify access to internal docs: Confluence / Notion link placeholder: https://confluence.nusantarakopi.internal/handbook
- Read: Company handbook, Engineering README, Deployment guide, On-call/Incident runbook.

Day 3 — Meet the team
- Intro meeting with your Team Lead (30–60 min) — agenda: role expectations, first tasks.
- Quick intro with CTO/Engineering Manager (20 min).
- 1:1 with HR to confirm benefits and policies.
- Team intro: attend daily standup and a team sync meeting.

Day 4 — First tasks
- Pick a small onboarding task (bugfix, doc cleanup, or a small feature) from the onboarding label on GitHub.
- Open a PR/MR (see code review practice) and request reviewers.
- Pair with a teammate for walkthrough of the codebase.

Day 5 — Review & plan
- Celebrate first PR merge and get feedback.
- Plan next two-week sprint with your Team Lead.
- Confirm recurring meetings and calendar invites.

Checklist items (priority):
- [ ] Company email active
- [ ] Slack joined
- [ ] GitHub access
- [ ] Jira access
- [ ] Laptop configured
- [ ] First PR opened
- [ ] Read handbook + deployment docs

---

## 2) Tools & accounts

We use a small, reliable stack common to 20-person startups. If you need a different tool (e.g., personal preference), discuss with your manager.

- Slack — Purpose: day-to-day communication, channels for teams and projects. Default permission: workspace member. How to get access: HR will invite; contact IT or HR for re-invite. Contact: IT: @dedi · it@nusantarakopi.tech.

- Google Workspace (Gmail, Drive, Calendar) — Purpose: email, docs, calendar. Default permission: standard user. How to get access: provisioned by IT/HR on first day.

- GitHub (repositories + GitHub Actions for CI/CD) — Purpose: source code, PRs, CI. Default permission for new hires: read + triage. To get write: request from Team Lead. How to request: open a ticket to IT or ask your Team Lead to add you. Contact: Eng Manager: @angga · angga@nusantarakopi.tech.

- Jira — Purpose: task tracking, sprints, backlog. Default permission: browse + comment. How to request: HR will send invite; if not, ask your Team Lead.

- CI/CD (GitHub Actions) — Purpose: automated build & deployment pipelines. Typical permission: pipelines run automatically; only senior engineers have deploy permissions by default. To request deployment permission, speak to the Engineering Manager and read the deployment checklist.

- Dev / Staging environment access — Purpose: test changes before production. How to request: raise an access ticket to IT with your GitHub handle and justify needs.

- VPN / Remote access — Purpose: secure access to internal services. How to request: IT provides credentials (two-factor recommended). If you need VPN, contact IT.

- Password manager (1Password or LastPass) — Purpose: shared credentials. Default permission: invitation to company vault. How to request: IT will invite; for additional secrets, follow the credential request process.

- Internal docs (Confluence / Notion) — Purpose: company handbook, runbooks, onboarding tasks. Link: https://confluence.nusantarakopi.internal/handbook (placeholder). Ask your Team Lead or HR for missing pages.

For all account requests: include your full name, GitHub handle, Slack handle, and start date. Expect 1–3 business days for most requests.

---

## 3) Ways of working

Meeting cadence
- Daily standup — 15 minutes, team channel, every weekday. Purpose: unblock coordination.
- Weekly planning — 60–90 minutes, team + product, set sprint goals.
- Weekly demo / show-and-tell — 30 minutes, optional but encouraged.
- Retrospective — every two weeks at the end of the sprint.

Communication
- Slack for immediate chat; use threads to keep context. Expected response times: same-day for important messages, 24 hours for non-urgent. Use email for formal or long-form topics.
- Use status indicators (away / focus) and update your Slack status for meetings or deep work.

Code review practice
- Open PRs via GitHub. Use small, focused PRs (ideally < 400 lines). Add a clear description, testing steps, and link to Jira ticket.
- Review SLA: request at least one reviewer; aim for a review response within 24 business hours. Merge only after approvals and green CI.
- Merge strategy: use feature branches and merge via Squash-and-merge to keep history tidy. For hotfixes use the hotfix branch pattern.

Branching & release process
- Branch naming: feature/<short-desc>, fix/<ticket-number>, chore/<desc>.
- Release: tagged via GitHub Actions; deployments pass through staging then production after QA sign-off.
- Production deploys: scheduled or on-demand with rollout notes; only authorized engineers can trigger production deploys.

Incident reporting
- Use the incident channel (#incidents) on Slack and follow the incident runbook in Confluence.
- Triage: on-call engineer or Engineering Manager leads initial response; notify CEO for high-severity incidents.

Working hours & leave
- Core hours: 10:00–16:00 local time — overlap window for synchronous work.
- Flexible start/end outside core hours. Remote/hybrid: office available — aim for at least one in-person week per quarter if possible.
- Leave policy summary: request via HR (link to HR portal placeholder). Emergency leave: inform your manager and HR as soon as possible.

---

## 4) Who to ask

- CEO / Founder — Rini — Role: strategic decisions, company vision. Contact: @rini · rini@nusantarakopi.tech
- CTO / Engineering Manager — Budi / Angga — Role: technical direction and engineering operations. Contact: @budi · budi@nusantarakopi.tech; @angga · angga@nusantarakopi.tech
- HR — Sari — Role: onboarding, payroll, benefits, policies. Contact: @sari · sari@nusantarakopi.tech
- Team Lead — Putri — Role: day-to-day task assignments, code reviews, mentoring. Contact: @putri · putri@nusantarakopi.tech
- IT Support — Dedi — Role: equipment, access, VPN, password manager. Contact: @dedi · it@nusantarakopi.tech
- Office Admin — Maya — Role: office supplies, booking meeting rooms. Contact: @maya · office@nusantarakopi.tech

(These are placeholders — if you have existing contacts, HR will provide your team roster.)

---

## 5) Glossary

- PR / MR — Pull Request / Merge Request: request to merge code into a branch. (ID: PR / MR)
- Code review — Tinjauan kode: review of changes before merge.
- Repo / Repository — Repository kode sumber.
- CI / CD — Continuous Integration / Continuous Deployment: automated build and deploy.
- Staging — Pre-production environment for verification.
- Production — Live environment serving customers.
- Sprint — Timeboxed development cycle (usually 2 weeks).
- Standup — Short daily sync meeting.
- Retro — Retrospective meeting to improve team process.
- Issue / Ticket — Work item tracked in Jira or GitHub Issues.
- Branch — Git branch used for feature or fix.
- Merge — Combine branch changes into mainline.
- Runbook — Step-by-step instructions for incidents or operations.
- VPN — Virtual Private Network for secure internal access.
- Vault / Password manager — Shared credential storage (company vault).

---

## Appendix: key bilingual term map (EN ↔ ID)

See docs/onboarding-term-map.yml for a machine-friendly list. This map guided translations: keep technical terms consistent across languages.

---

Assumptions & notes
- Choice: GitHub + GitHub Actions for CI/CD and Jira for issue tracking — common for 20-person startups.
- Internal links are placeholders; HR/IT will provide real URLs after provisioning.

