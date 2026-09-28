import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";

/** Finds the dotcode CLI: setting → DOTCODE_CLI_PATH → PATH → the installers' default locations. */
export function findCli(): string | undefined {
  const configured = vscode.workspace.getConfiguration("dotcode").get<string>("cliPath")?.trim();
  if (configured) return expandHome(configured);
  if (process.env.DOTCODE_CLI_PATH) return process.env.DOTCODE_CLI_PATH;

  const exe = process.platform === "win32" ? "dotcode.exe" : "dotcode";
  for (const dir of (process.env.PATH ?? "").split(path.delimiter)) {
    if (!dir) continue;
    const candidate = path.join(dir, exe);
    if (isFile(candidate)) return candidate;
  }
  const defaults =
    process.platform === "win32"
      ? [path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "Programs", "DotCode", exe), path.join(os.homedir(), ".dotnet", "tools", exe)]
      : [path.join(os.homedir(), ".local", "bin", exe), "/usr/local/bin/dotcode", "/opt/homebrew/bin/dotcode", path.join(os.homedir(), ".dotnet", "tools", exe)];
  return defaults.find(isFile);
}

function isFile(p: string): boolean {
  try {
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
}

function expandHome(p: string): string {
  return p.startsWith("~") ? path.join(os.homedir(), p.slice(1)) : p;
}

/** Offers to install the CLI with the official installer in a terminal. */
export async function offerInstall(): Promise<void> {
  const choice = await vscode.window.showErrorMessage(
    "DotCode CLI not found. Install it (one command, no admin rights), or set 'dotcode.cliPath'.",
    "Install in Terminal",
    "Open Settings",
    "Docs",
  );
  if (choice === "Install in Terminal") {
    const terminal = vscode.window.createTerminal({ name: "Install DotCode" });
    terminal.show();
    terminal.sendText(
      process.platform === "win32"
        ? "irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/DotCode/install/install.ps1 | iex"
        : "curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/DotCode/install/install.sh | sh",
    );
  } else if (choice === "Open Settings") {
    await vscode.commands.executeCommand("workbench.action.openSettings", "dotcode.cliPath");
  } else if (choice === "Docs") {
    await vscode.env.openExternal(vscode.Uri.parse("https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/getting-started.md"));
  }
}
