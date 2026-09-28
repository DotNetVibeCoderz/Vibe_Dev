import * as path from "path";
import * as vscode from "vscode";
import { ChatViewProvider } from "./chatView";
import { Controller } from "./controller";
import { findCli, offerInstall } from "./cli";

/** Public API (used by the integration tests; also handy for other extensions). */
export interface DotCodeApi {
  controller: Controller;
}

export function activate(context: vscode.ExtensionContext): DotCodeApi {
  const controller = new Controller(context);
  const chat = new ChatViewProvider(context.extensionUri, controller);
  context.subscriptions.push(
    controller,
    chat,
    vscode.window.registerWebviewViewProvider(ChatViewProvider.viewId, chat, { webviewOptions: { retainContextWhenHidden: true } }),

    vscode.commands.registerCommand("dotcode.openChat", () => vscode.commands.executeCommand("dotcode.chat.focus")),
    vscode.commands.registerCommand("dotcode.newSession", () => controller.newSession()),
    vscode.commands.registerCommand("dotcode.stop", () => controller.stop()),
    vscode.commands.registerCommand("dotcode.selectModel", () => controller.selectModel()),
    vscode.commands.registerCommand("dotcode.setPermissionMode", () => controller.setPermissionMode()),

    vscode.commands.registerCommand("dotcode.askAboutSelection", async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor || editor.selection.isEmpty) return;
      const doc = editor.document;
      const cwd = controller.cwd;
      const file = cwd ? path.relative(cwd, doc.uri.fsPath).replace(/\\/g, "/") : doc.uri.fsPath;
      const { start, end } = editor.selection;
      const code = doc.getText(editor.selection);
      await chat.prefill(`In @${file} (lines ${start.line + 1}-${end.line + 1}):\n\`\`\`${doc.languageId}\n${code}\n\`\`\`\n`);
    }),

    vscode.commands.registerCommand("dotcode.openInTerminal", async () => {
      const cli = findCli();
      if (!cli) return offerInstall();
      const terminal = vscode.window.createTerminal({ name: "DotCode", cwd: controller.cwd, iconPath: new vscode.ThemeIcon("sparkle") });
      terminal.show();
      terminal.sendText(cli.toLowerCase().endsWith(".dll") ? `dotnet "${cli}"` : `"${cli}"`);
    }),

    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("dotcode.model") || e.affectsConfiguration("dotcode.cliPath") || e.affectsConfiguration("dotcode.settings"))
        void vscode.window.showInformationMessage("DotCode settings changed. Start a new session to apply them.", "New Session").then((c) => {
          if (c) void controller.newSession();
        });
    }),
  );
  return { controller };
}

export function deactivate() {
  /* disposables handle cleanup */
}
