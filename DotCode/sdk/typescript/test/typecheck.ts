// Compile-time checks (`npm test` runs `tsc` on this file): each @ts-expect-error line must fail to compile.
import { DotCodeClient, defineTool, s, approveAll, PermissionDecision, BuiltinTool, type Infer } from "../src/index.js";

const weather = defineTool("get_weather", {
  description: "Weather for a city",
  parameters: s.object({
    city: s.string().describe("City name"),
    unit: s.enum(["celsius", "fahrenheit"]).optional(),
    days: s.integer().min(1).max(7).default(1),
  }),
  handler: ({ city, unit, days }) => {
    const c: string = city;
    const u: "celsius" | "fahrenheit" | undefined = unit;
    const d: number | undefined = days;
    // @ts-expect-error misspelled argument
    void city.lenght;
    return `${c} ${u} ${d}`;
  },
});

defineTool("bad", {
  description: "typo in the handler",
  parameters: s.object({ city: s.string() }),
  // @ts-expect-error `cty` is not a parameter
  handler: ({ cty }) => cty,
});

const idParams = s.object({ id: s.string(), tags: s.array(s.string()).optional() });
type Params = Infer<typeof idParams>;
const p: Params = { id: "x" };
// @ts-expect-error missing required property
const q: Params = {};
void p; void q;

async function main() {
  const client = new DotCodeClient();
  const session = await client.createSession({
    model: "openai:gpt-5",
    tools: [weather],
    onPermissionRequest: approveAll,
    availableTools: [BuiltinTool.Read, "Grep"],
    allowedTools: ["Bash(npm test:*)", "mcp__github__list_issues"],
    providers: { local: { type: "ollama", baseUrl: "http://localhost:11434" } },
    systemMessage: { mode: "append", content: "Be brief." },
    // @ts-expect-error unknown permission mode
    permissionMode: "acceptEdit",
  });
  await client.createSession({
    // @ts-expect-error unknown built-in tool
    availableTools: ["Raed"],
  });
  await client.createSession({
    // @ts-expect-error unknown provider type
    providers: { x: { type: "open-ai" } },
  });
  await client.createSession({
    onPermissionRequest: (req) => (req.toolName === "Bash" ? PermissionDecision.reject("no shell") : PermissionDecision.approveOnce()),
  });
  session.on("tool.completed", (e) => {
    const out: string = e.output;
    // @ts-expect-error `text` is not a field of tool.completed
    void e.text;
    void out;
  });
  // @ts-expect-error unknown event type
  session.on("tool.complete", () => {});
  const r = await session.sendAndWait({ prompt: "hi" });
  const n: number = r.numModelCalls;
  void n;
}
void main;
