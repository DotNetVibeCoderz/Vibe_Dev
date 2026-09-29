import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import type { DotCodeApi } from "../../extension";
import type { HostMessage } from "../../controller";

const workspace = process.env.DOTCODE_TEST_WORKSPACE!;

async function api(): Promise<DotCodeApi> {
  const ext = vscode.extensions.getExtension<DotCodeApi>("GravicodeStudios.dotcode-vscode");
  assert.ok(ext, "extension is installed");
  return ext.activate();
}

describe("DotCode extension", () => {
  it("registers its commands and chat view", async () => {
    await api();
    const commands = await vscode.commands.getCommands(true);
    for (const c of ["dotcode.openChat", "dotcode.newSession", "dotcode.askAboutSelection", "dotcode.stop", "dotcode.selectModel", "dotcode.setPermissionMode", "dotcode.openInTerminal"])
      assert.ok(commands.includes(c), `${c} registered`);
    await vscode.commands.executeCommand("dotcode.openChat");
  });

  it("runs a turn against dotcode serve, asks permission and applies the edit", async () => {
    const { controller } = await api();
    const asked: string[] = [];
    controller.autoPermission = (request) => {
      asked.push(`${request.toolName}:${request.displayName}`);
      return { decision: "allow" };
    };
    const seen: HostMessage[] = [];
    const sub = controller.onMessage((m) => seen.push(m));
    const result = await controller.send("please create the greeting file");
    sub.dispose();

    assert.ok(result, "turn completed");
    assert.strictEqual(result!.result, "Done: hello.txt created.");
    assert.deepStrictEqual(asked.map((a) => a.split(":")[0]), ["Write"]);
    assert.strictEqual(fs.readFileSync(path.join(workspace, "hello.txt"), "utf8"), "hello from vscode\n");
    const events = seen.filter((m): m is Extract<HostMessage, { type: "event" }> => m.type === "event").map((m) => m.event);
    const completed = events.find((e) => e.type === "tool.completed");
    assert.ok(completed && completed.type === "tool.completed" && completed.name === "Write" && !completed.isError, "Write completed");
    assert.ok(completed.diff?.includes("+hello from vscode"), "diff streamed to the view");
    assert.ok(events.some((e) => e.type === "assistant.text.delta"), "text streamed");
    assert.ok(events.some((e) => e.type === "turn.completed"), "turn.completed delivered");
    assert.ok(seen.some((m) => m.type === "state" && m.busy) && seen.some((m) => m.type === "state" && !m.busy), "busy state toggled");
  });

  it("reports a denied edit back to the model and leaves the file untouched", async () => {
    const { controller } = await api();
    controller.autoPermission = () => ({ decision: "deny", feedback: "Not in this test" });
    const result = await controller.send("now try an edit");
    assert.strictEqual(result!.result, "Understood, I will not write it.");
    assert.ok(!fs.existsSync(path.join(workspace, "denied.txt")), "file not written");
  });

  it("starts a fresh session", async () => {
    const { controller } = await api();
    await controller.newSession();
    assert.strictEqual(controller.transcript.length, 0);
    assert.strictEqual(controller.isBusy, false);
  });
});
