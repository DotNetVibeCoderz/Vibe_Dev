// DotCode TypeScript SDK sample. Run: node samples/sdk/typescript/example.mjs azure:gpt-5-mini
import { DotCodeClient, defineTool, s, PermissionDecision } from "../../../sdk/typescript/dist/index.js";

const getExchangeRate = defineTool("get_exchange_rate", {
  description: "Get the exchange rate between two currencies",
  parameters: s.object({
    from: s.string().describe("ISO currency code, e.g. USD"),
    to: s.string().describe("ISO currency code, e.g. IDR"),
  }),
  readOnly: true,
  handler: ({ from, to }) => `1 ${from} = ${to === "IDR" ? "16,250" : "0.92"} ${to} (demo data)`,
});

const client = new DotCodeClient();
await client.start();
const session = await client.createSession({
  model: process.argv[2],
  persistSession: false,
  tools: [getExchangeRate],
  onPermissionRequest: (req) => {
    console.log(`  [permission] ${req.displayName} → allowed`);
    return PermissionDecision.approveOnce();
  },
});

session.on("assistant.text.delta", (e) => process.stdout.write(e.text));
session.on("tool.started", (e) => console.log(`● ${e.displayName}`));
session.on("tool.completed", (e) => console.log(`  ⎿  ${e.output}`));

console.log(`DotCode SDK (TypeScript) · model ${session.model}\n`);
const result = await session.sendAndWait("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.");
console.log(`\n\n✔ ${result.numModelCalls} model calls · $${result.costUsd.toFixed(4)} · ${result.durationMs} ms`);
await session.disconnect();
await client.stop();
