// Launches VS Code with the extension and runs the integration suite against a real `dotcode serve`
// (offline scripted model). Needs the CLI built: `dotnet build` in DotCode/ (or DOTCODE_CLI_PATH).
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runTests } from "@vscode/test-electron";

async function main() {
  const extensionDevelopmentPath = path.resolve(__dirname, "../..");
  const extensionTestsPath = path.resolve(__dirname, "./suite/index");
  const cli = process.env.DOTCODE_CLI_PATH ?? path.resolve(extensionDevelopmentPath, "../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll");
  if (!fs.existsSync(cli)) throw new Error(`DotCode CLI not found at ${cli}; run 'dotnet build' in DotCode/ first.`);

  // A throwaway workspace whose settings point the extension at the scripted mock model.
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), "dotcode-vscode-"));
  const script = path.join(workspace, ".dotcode-test-script.json");
  fs.writeFileSync(script, JSON.stringify({
    responses: [
      { match: "create the greeting", text: "Creating the file.", toolCalls: [{ name: "Write", input: { file_path: path.join(workspace, "hello.txt"), content: "hello from vscode\n" } }] },
      { match: "File created successfully", text: "Done: hello.txt created." },
      { match: "try an edit", toolCalls: [{ name: "Write", input: { file_path: path.join(workspace, "denied.txt"), content: "x" } }] },
      { match: "Not in this test", text: "Understood, I will not write it." },
    ],
  }));
  fs.mkdirSync(path.join(workspace, ".vscode"));
  fs.writeFileSync(path.join(workspace, ".vscode", "settings.json"), JSON.stringify({
    "dotcode.cliPath": cli,
    "dotcode.model": "mock:scripted",
    "dotcode.showDiffOnPermission": false,
    "dotcode.settings": { providers: { mock: { type: "mock", script } }, autoCompact: false },
  }, null, 2));

  await runTests({
    extensionDevelopmentPath,
    extensionTestsPath,
    version: process.env.VSCODE_TEST_VERSION ?? "stable",
    launchArgs: [workspace, "--disable-extensions", "--disable-workspace-trust"],
    extensionTestsEnv: { DOTCODE_CONFIG_DIR: path.join(workspace, ".dotcode-home"), DOTCODE_TEST_WORKSPACE: workspace },
  });
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
