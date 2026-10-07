// SDK conformance test: runs against a real Marbots.Server process with the offline mock model.
// Build the server first (`dotnet build Marbots.slnx`) or set MARBOTS_SERVER_DLL.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, rmSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { MarbotsClient, MarbotsError, HostRef, KernelPack, PermissionProfile, EventType, ModelRef, BOSS_MAN, isTaskFinished } from "../dist/index.js";

const dll = process.env.MARBOTS_SERVER_DLL ?? resolve("../../src/Marbots.Server/bin/Debug/net10.0/Marbots.Server.dll");
const skip = !existsSync(dll);
let proc, data, mb;

const freePort = () => new Promise((ok) => { const s = createServer(); s.listen(0, "127.0.0.1", () => { const p = s.address().port; s.close(() => ok(p)); }); });

before(async () => {
  if (skip) return;
  const port = await freePort();
  data = mkdtempSync(join(tmpdir(), "mb-ts-"));
  proc = spawn("dotnet", [dll, "--urls", `http://127.0.0.1:${port}`], {
    cwd: dirname(dll), stdio: "ignore",
    env: { ...process.env, Marbots__DataDirectory: data, Marbots__Providers__0__Name: "lab", Marbots__Providers__0__Kind: "mock",
      Marbots__Providers__0__Models__0: "lab-small", Marbots__Providers__0__Models__1: "lab-large" },
  });
  mb = new MarbotsClient({ baseUrl: `http://127.0.0.1:${port}` });
  for (let i = 0; i < 120; i++) {
    try { await mb.system(); return; } catch { await new Promise((r) => setTimeout(r, 500)); }
  }
  throw new Error("server did not start");
});

after(() => {
  proc?.kill();
  if (data) setTimeout(() => rmSync(data, { recursive: true, force: true }), 500);
});

test("system and team", { skip }, async () => {
  const sys = await mb.system();
  assert.equal(sys.product, "Marbots");
  assert.match(sys.creditsEn, /Gravicode/);
  const bots = await mb.bots.list();
  assert.ok(bots.some((b) => b.id === BOSS_MAN && b.isSystem));
});

test("hire, per-bot model, chat, export/import", { skip }, async () => {
  const bot = await mb.bots.hire("data-analyst", "Dina");
  assert.equal(bot.modelProfile, ModelRef.Default);
  assert.ok(bot.kernelFunctions.includes(KernelPack.Shell));
  assert.equal(bot.permissionProfile, PermissionProfile.DeveloperSafe);

  const info = await mb.bots.setModel(bot.id, ModelRef.of("lab", "lab-large"));
  assert.equal(info.effective, "lab/lab-large");
  assert.equal(info.usesDefault, false);
  await assert.rejects(() => mb.bots.setModel(bot.id, ModelRef.of("ghost", "x")), (e) => e instanceof MarbotsError && e.status === 400);

  const thread = await mb.threads.create(bot.id);
  const r = await mb.threads.send(thread.id, "hello", { wait: true, timeoutSeconds: 30 });
  assert.equal(r.task.state, "Completed");
  assert.equal(r.task.model, "lab/lab-large");
  assert.match(r.reply.content, /mock/);

  const imported = await mb.bots.importPackage(await mb.bots.export(bot.id));
  assert.equal(imported.modelProfile, "lab/lab-large");
  await mb.bots.delete(imported.id);
  await mb.bots.delete(bot.id);
});

test("create with a typed spec", { skip }, async () => {
  const bot = await mb.bots.create({ name: "Typed Tia", kernelFunctions: [KernelPack.Files], permissionProfile: PermissionProfile.ReadOnly, model: ModelRef.of("lab", "lab-small") });
  assert.deepEqual(bot.kernelFunctions, ["files"]);
  assert.equal(bot.permissionProfile, "read-only");
  assert.equal((await mb.bots.getModel(bot.id)).effective, "lab/lab-small");
  await mb.bots.delete(bot.id);
});

test("models catalog and default", { skip }, async () => {
  const catalog = await mb.models.list();
  assert.ok(catalog.choices.includes("lab/lab-small"));
  assert.equal(await mb.models.setDefault(ModelRef.of("lab", "lab-small")), "lab/lab-small");
  const wren = await mb.bots.getModel("wren");
  assert.equal(wren.usesDefault, true);
  assert.equal(wren.effective, "lab/lab-small");
});

test("event stream reports completion", { skip }, async () => {
  const thread = await mb.threads.create("atlas");
  const ac = new AbortController();
  const seen = [];
  const listening = (async () => {
    try {
      for await (const e of mb.events(thread.id, ac.signal)) { seen.push(e); if (isTaskFinished(e)) break; }
    } catch { /* aborted */ }
  })();
  await new Promise((r) => setTimeout(r, 400));
  await mb.threads.send(thread.id, "ping");
  await Promise.race([listening, new Promise((r) => setTimeout(r, 20000))]);
  ac.abort();
  assert.ok(seen.some((e) => e.type === EventType.TaskStateChanged && e.data === "Completed"));
});

test("skip approvals toggle", { skip }, async () => {
  assert.equal(await mb.approvals.getSkipApprovals(), false);
  assert.equal(await mb.approvals.setSkipApprovals(true), true);
  assert.equal(await mb.approvals.getSkipApprovals(), true);
  assert.equal(await mb.approvals.setSkipApprovals(false), false);
});

test("templates, approvals, schedules", { skip }, async () => {
  assert.ok((await mb.templates.list("designer")).some((t) => t.id === "ux-designer"));
  assert.deepEqual(await mb.approvals.pending(), []);
  const job = await mb.schedules.create({ name: "weekly", botId: "atlas", prompt: "brief", cron: "0 8 * * 1" });
  assert.ok(job.nextRunAt);
  await mb.schedules.delete(job.id);
  await assert.rejects(() => mb.schedules.create({ name: "bad", botId: "atlas", prompt: "x", cron: "nope" }), MarbotsError);
});

test("agent hosts and enrollment", { skip }, async () => {
  const hosts = await mb.agentHosts.list();
  const local = hosts.find((h) => h.id === HostRef.Local);
  assert.ok(local?.capabilities.includes("shell"));
  const token = await mb.agentHosts.createEnrollment("lab-pc", 10);
  assert.match(token.token, /^mbe_/);
  assert.match(token.enrollCommand, /marbots-host enroll/);
  await assert.rejects(mb.agentHosts.disable("host-that-does-not-exist"), MarbotsError);
});

test("placement and container round trip", { skip }, async () => {
  const bot = await mb.bots.create({ name: "Placed Ts", hostRef: HostRef.Auto,
    container: { image: "python:3.12-slim", cpus: 1.5, memoryMb: 512, network: false },
    kernelFunctions: [KernelPack.Shell, KernelPack.Subagents] });
  assert.equal(bot.hostRef, "auto");
  assert.deepEqual([bot.container?.cpus, bot.container?.memoryMb, bot.container?.network], [1.5, 512, false]);
});

test("skill evaluations and auto rollback", { skip }, async () => {
  const evals = await mb.skills.evaluations();
  assert.ok(evals.length > 0 && evals.every((e) => e.verdict === "CollectingEvidence"));
  assert.equal(await mb.skills.setAutoRollback(true), true);
  assert.equal(await mb.skills.autoRollback(), true);
  assert.equal(await mb.skills.setAutoRollback(false), false);
  await assert.rejects(mb.skills.rollback("no-such-skill"), MarbotsError);
});
