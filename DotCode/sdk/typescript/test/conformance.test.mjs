// SDK conformance test: runs against a real `dotcode serve` process with the deterministic scripted provider.
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, existsSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { DotCodeClient, defineTool, s, PermissionDecision } from "../dist/index.js";

const cli = process.env.DOTCODE_CLI_PATH ?? resolve("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll");

function setup(responses) {
  const dir = mkdtempSync(join(tmpdir(), "dc-ts-"));
  const script = join(dir, "s.json");
  writeFileSync(script, JSON.stringify({ responses }));
  const client = new DotCodeClient({ cliPath: cli, cwd: dir, env: { DOTCODE_CONFIG_DIR: join(dir, ".cfg") } });
  const base = { model: "mock:scripted", providers: { mock: { type: "mock", script } }, persistSession: false, disableMcp: true };
  return { dir, client, base };
}

const getWeather = defineTool("get_weather", {
  description: "Weather for a city",
  parameters: s.object({ city: s.string().describe("City name") }),
  readOnly: true,
  handler: async ({ city }, invocation) => `${city}: rainy, 24°C (${invocation.toolName})`,
});

test("custom tool round-trip and streaming", { skip: !existsSync(cli) }, async () => {
  const { client, base } = setup([
    { text: "Checking the weather.", toolCalls: [{ name: "get_weather", input: { city: "Bogor" } }] },
    { text: "It is rainy in Bogor." },
  ]);
  try {
    const all = [];
    const session = await client.createSession({ ...base, tools: [getWeather], onEvent: (e) => all.push(e) });
    const completed = [];
    session.on("tool.completed", (e) => completed.push(e));
    const events = [];
    for await (const e of session.stream("weather?")) events.push(e);
    const last = events.at(-1);
    assert.equal(last.type, "turn.completed");
    assert.equal(last.resultText, "It is rainy in Bogor.");
    assert.equal(completed[0].name, "get_weather");
    assert.match(completed[0].output, /rainy, 24°C \(get_weather\)/);
    assert.equal((await session.getMessages()).length, 4);
    assert.ok(all.some((e) => e.type === "assistant.text.delta"), "onEvent receives every event");
    await session.disconnect();
  } finally {
    await client.stop();
  }
});

test("invalid tool arguments are reported to the model", { skip: !existsSync(cli) }, async () => {
  const { client, base } = setup([
    { toolCalls: [{ name: "get_weather", input: { town: "Bogor" } }] },
    { text: "done" },
  ]);
  try {
    const session = await client.createSession({ ...base, tools: [getWeather] });
    const completed = [];
    session.on("tool.completed", (e) => completed.push(e));
    const result = await session.sendAndWait({ prompt: "weather?" });
    assert.equal(result.result, "done");
    assert.equal(completed[0].isError, true);
    assert.match(completed[0].output, /city/);
  } finally {
    await client.stop();
  }
});

test("permission handler decides tool access", { skip: !existsSync(cli) }, async () => {
  const { dir, client, base } = setup([
    { toolCalls: [{ name: "Write", input: { file_path: "PLACEHOLDER", content: "from ts" } }] },
    { text: "written" },
  ]);
  const target = join(dir, "out.txt");
  const script = base.providers.mock.script;
  writeFileSync(script, readFileSync(script, "utf8").replace("PLACEHOLDER", target.replaceAll("\\", "\\\\")));
  try {
    const asked = [];
    const session = await client.createSession({
      ...base,
      onPermissionRequest: (req) => { asked.push(req.toolName); return PermissionDecision.approveOnce(); },
    });
    const idle = new Promise((r) => session.on("turn.completed", r));
    await session.send({ prompt: "write a file" });
    const done = await idle;
    assert.equal(done.resultText, "written");
    assert.deepEqual(asked, ["Write"]);
    assert.ok(existsSync(target));
  } finally {
    await client.stop();
  }
});
