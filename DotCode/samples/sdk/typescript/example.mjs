// DotCode TypeScript SDK sample. Run: node samples/sdk/typescript/example.mjs azure:gpt-5-mini
import { DotCodeClient, tool } from "../../../sdk/typescript/dist/index.js";

const client = new DotCodeClient();
const session = await client.createSession({
  model: process.argv[2],
  persistSession: false,
  tools: [tool({
    name: "get_exchange_rate",
    description: "Get the exchange rate between two currencies",
    inputSchema: { type: "object", properties: { from: { type: "string" }, to: { type: "string" } }, required: ["from", "to"] },
    readOnly: true,
    handler: ({ from, to }) => `1 ${from} = ${to === "IDR" ? "16,250" : "0.92"} ${to} (demo data)`,
  })],
  onPermissionRequest: (req) => (console.log(`  [permission] ${req.displayName} → allowed`), { decision: "allow" }),
});

console.log(`DotCode SDK (TypeScript) · model ${session.model}\n`);
for await (const e of session.stream("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.")) {
  if (e.type === "assistant.text.delta") process.stdout.write(e.text);
  else if (e.type === "tool.started") console.log(`● ${e.displayName}`);
  else if (e.type === "tool.completed") console.log(`  ⎿  ${e.output}`);
  else if (e.type === "turn.completed") console.log(`\n\n✔ ${e.numModelCalls} model calls · $${e.costUsd.toFixed(4)} · ${e.durationMs} ms`);
}
await client.close();
