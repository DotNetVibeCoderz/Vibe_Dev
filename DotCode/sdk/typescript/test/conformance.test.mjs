// SDK conformance test: runs against a real `dotcode serve` process with the deterministic scripted provider.
import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { DotCodeClient, tool } from "../dist/index.js";

const cli = process.env.DOTCODE_CLI_PATH ?? resolve("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll");

test("custom tool round-trip and streaming", { skip: !existsSync(cli) }, async () => {
  const dir = mkdtempSync(join(tmpdir(), "dc-ts-"));
  const script = join(dir, "s.json");
  writeFileSync(script, JSON.stringify({ responses: [
    { text: "Checking the weather.", toolCalls: [{ name: "get_weather", input: { city: "Bogor" } }] },
    { text: "It is rainy in Bogor." },
  ] }));
  const client = new DotCodeClient({ cliPath: cli, cwd: dir, env: { DOTCODE_CONFIG_DIR: join(dir, ".cfg") } });
  try {
    const session = await client.createSession({
      model: "mock:scripted",
      settings: { providers: { mock: { type: "mock", script } } },
      persistSession: false,
      noMcp: true,
      tools: [tool({
        name: "get_weather",
        description: "Weather for a city",
        inputSchema: { type: "object", properties: { city: { type: "string" } }, required: ["city"] },
        readOnly: true,
        handler: async ({ city }) => `${city}: rainy, 24°C`,
      })],
    });
    const events = [];
    for await (const e of session.stream("weather?")) events.push(e);
    const last = events.at(-1);
    assert.equal(last.type, "turn.completed");
    assert.equal(last.resultText, "It is rainy in Bogor.");
    const toolDone = events.find((e) => e.type === "tool.completed");
    assert.equal(toolDone.name, "get_weather");
    assert.match(toolDone.output, /rainy/);
    const { messages } = await session.messages();
    assert.equal(messages.length, 4);
  } finally {
    await client.close();
  }
});

test("permission handler decides tool access", { skip: !existsSync(cli) }, async () => {
  const dir = mkdtempSync(join(tmpdir(), "dc-ts-"));
  const script = join(dir, "s.json");
  const target = join(dir, "out.txt");
  writeFileSync(script, JSON.stringify({ responses: [
    { toolCalls: [{ name: "Write", input: { file_path: target, content: "from ts" } }] },
    { text: "written" },
  ] }));
  const client = new DotCodeClient({ cliPath: cli, cwd: dir, env: { DOTCODE_CONFIG_DIR: join(dir, ".cfg") } });
  try {
    const asked = [];
    const session = await client.createSession({
      model: "mock:scripted",
      settings: { providers: { mock: { type: "mock", script } } },
      persistSession: false,
      noMcp: true,
      onPermissionRequest: async (req) => { asked.push(req.toolName); return { decision: "allow" }; },
    });
    const result = await session.send("write a file");
    assert.equal(result.result, "written");
    assert.deepEqual(asked, ["Write"]);
    assert.ok(existsSync(target));
  } finally {
    await client.close();
  }
});
