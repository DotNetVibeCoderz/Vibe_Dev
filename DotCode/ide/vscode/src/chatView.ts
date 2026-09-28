import * as vscode from "vscode";
import type { Controller, ViewMessage } from "./controller";

/** The chat panel: a webview (media/chat.js + chat.css) talking to the controller through postMessage. */
export class ChatViewProvider implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewId = "dotcode.chat";
  private view?: vscode.WebviewView;
  private subscription?: vscode.Disposable;

  constructor(
    private readonly extensionUri: vscode.Uri,
    private readonly controller: Controller,
  ) {}

  resolveWebviewView(view: vscode.WebviewView): void {
    this.view = view;
    const media = vscode.Uri.joinPath(this.extensionUri, "media");
    view.webview.options = { enableScripts: true, localResourceRoots: [media] };
    view.webview.html = this.html(view.webview, media);
    this.subscription?.dispose();
    this.subscription = this.controller.onMessage((m) => void view.webview.postMessage(m));
    view.webview.onDidReceiveMessage((m: ViewMessage) => {
      if (m.type === "ready") {
        // Re-create the conversation after the view was hidden or reloaded.
        for (const message of this.controller.transcript) void view.webview.postMessage(message);
      }
      void this.controller.handleViewMessage(m);
    });
    view.onDidDispose(() => {
      this.subscription?.dispose();
      this.view = undefined;
    });
  }

  /** Puts text into the prompt box (e.g. a selection) and focuses it. */
  async prefill(text: string) {
    await vscode.commands.executeCommand("dotcode.chat.focus");
    // The view may still be resolving after focusing it.
    for (let i = 0; i < 20 && !this.view; i++) await new Promise((r) => setTimeout(r, 50));
    await this.view?.webview.postMessage({ type: "prefill", text });
  }

  private html(webview: vscode.Webview, media: vscode.Uri): string {
    const nonce = Array.from({ length: 32 }, () => "abcdefghijklmnopqrstuvwxyz0123456789"[Math.floor(Math.random() * 36)]).join("");
    const script = webview.asWebviewUri(vscode.Uri.joinPath(media, "chat.js"));
    const style = webview.asWebviewUri(vscode.Uri.joinPath(media, "chat.css"));
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource} data:;">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>DotCode</title>
</head>
<body>
<header id="header"><span id="model"></span><span id="mode"></span><span id="context"></span></header>
<main id="messages" aria-live="polite">
  <section id="welcome">
    <h2>DotCode</h2>
    <p>Agentic coding with any LLM. Ask about your code, request changes, or run tasks — DotCode asks before editing files or running commands.</p>
    <p class="hint">Tip: select code and press <kbd>Ctrl/Cmd+Alt+K</kbd> to ask about it · <kbd>Shift+Enter</kbd> for a new line</p>
    <p class="credit">Built by Gravicode Studios, led by Kang Fadhil</p>
  </section>
</main>
<footer>
  <div id="status"></div>
  <div class="composer">
    <textarea id="prompt" rows="3" placeholder="Ask DotCode… (Enter to send)"></textarea>
    <div class="buttons">
      <button id="send" title="Send (Enter)">Send</button>
      <button id="stop" title="Stop" class="secondary" hidden>Stop</button>
    </div>
  </div>
  <div class="links"><a href="#" data-command="dotcode.selectModel">model</a> · <a href="#" data-command="dotcode.setPermissionMode">mode</a> · <a href="#" data-command="dotcode.newSession">new session</a></div>
</footer>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }

  dispose() {
    this.subscription?.dispose();
  }
}
