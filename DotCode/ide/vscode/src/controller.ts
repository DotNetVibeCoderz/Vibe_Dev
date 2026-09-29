import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { DotCodeClient, PermissionDecision, type DotCodeSession } from "dotcode-sdk";
import type { ExitPlanModeResult, PermissionMode, PermissionRequest, SendResult, SessionEvent, UserInputRequest, UserQuestionAnswer } from "dotcode-sdk";
import { findCli, offerInstall } from "./cli";

/** Messages from the extension host to the chat webview. */
export type HostMessage =
  | { type: "user"; text: string }
  | { type: "event"; event: SessionEvent }
  | { type: "permission"; id: string; toolName: string; displayName: string; title: string; detail?: string; diff?: string; canAlways: boolean; isEdit: boolean }
  | { type: "permissionResolved"; id: string; decision: ViewDecision }
  | { type: "state"; busy: boolean; model?: string; mode?: string; cwd?: string }
  | { type: "error"; text: string }
  | { type: "reset" };

/** Messages from the webview to the host. */
export type ViewMessage =
  | { type: "ready" }
  | { type: "send"; text: string }
  | { type: "stop" }
  | { type: "permission"; id: string; decision: ViewDecision; feedback?: string }
  | { type: "openFile"; path: string; line?: number }
  | { type: "command"; command: string };

/** The chat view's permission buttons. */
export type ViewDecision = "allow" | "allow_always" | "deny";

const PROPOSED_SCHEME = "dotcode-proposed";

/** Owns the `dotcode serve` client and the current session, and relays everything to the chat view. */
export class Controller implements vscode.Disposable {
  private client?: DotCodeClient;
  private session?: DotCodeSession;
  private starting?: Promise<DotCodeSession | undefined>;
  private busy = false;
  private readonly history: HostMessage[] = [];
  private readonly pendingPermissions = new Map<string, { resolve: (d: PermissionDecision) => void; request: PermissionRequest }>();
  private readonly proposed = new Map<string, string>();
  private readonly listeners = new Set<(m: HostMessage) => void>();
  private readonly statusBar: vscode.StatusBarItem;
  private readonly disposables: vscode.Disposable[] = [];
  private mode: PermissionMode;

  /** Test hook: answer permission requests without the UI. */
  autoPermission?: (request: PermissionRequest) => PermissionDecision;

  constructor(private readonly context: vscode.ExtensionContext) {
    this.mode = vscode.workspace.getConfiguration("dotcode").get<PermissionMode>("permissionMode") ?? "default";
    this.statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
    this.statusBar.command = "dotcode.openChat";
    this.updateStatus();
    this.statusBar.show();
    this.disposables.push(
      this.statusBar,
      vscode.workspace.registerTextDocumentContentProvider(PROPOSED_SCHEME, { provideTextDocumentContent: (uri) => this.proposed.get(uri.toString()) ?? "" }),
    );
  }

  // ------------------------------------------------------------ view plumbing

  onMessage(listener: (m: HostMessage) => void): vscode.Disposable {
    this.listeners.add(listener);
    return new vscode.Disposable(() => this.listeners.delete(listener));
  }

  /** Everything posted so far (replayed when the view is re-created). */
  get transcript(): readonly HostMessage[] {
    return this.history;
  }

  private post(message: HostMessage) {
    if (message.type !== "state") this.history.push(message);
    for (const l of this.listeners) l(message);
  }

  private postState() {
    this.post({ type: "state", busy: this.busy, model: this.session?.model, mode: this.mode, cwd: this.cwd });
    this.updateStatus();
  }

  private updateStatus() {
    const model = this.session?.model ?? vscode.workspace.getConfiguration("dotcode").get<string>("model") ?? "";
    this.statusBar.text = `$(${this.busy ? "loading~spin" : "sparkle"}) DotCode${model ? " · " + model.split(":").pop() : ""}${this.mode !== "default" ? " · " + this.mode : ""}`;
    this.statusBar.tooltip = "DotCode — open chat (Ctrl/Cmd+Esc)";
  }

  async handleViewMessage(m: ViewMessage) {
    switch (m.type) {
      case "ready":
        this.postState();
        break;
      case "send":
        await this.send(m.text);
        break;
      case "stop":
        await this.stop();
        break;
      case "permission":
        this.resolvePermission(m.id, m.decision, m.feedback);
        break;
      case "openFile":
        await this.openFile(m.path, m.line);
        break;
      case "command":
        await vscode.commands.executeCommand(m.command);
        break;
    }
  }

  // ------------------------------------------------------------ session

  get cwd(): string | undefined {
    const active = vscode.window.activeTextEditor?.document.uri;
    const folder = (active && vscode.workspace.getWorkspaceFolder(active)) ?? vscode.workspace.workspaceFolders?.[0];
    return folder?.uri.fsPath;
  }

  get isBusy() {
    return this.busy;
  }

  private async ensureSession(): Promise<DotCodeSession | undefined> {
    if (this.session) return this.session;
    this.starting ??= this.startSession().finally(() => (this.starting = undefined));
    return this.starting;
  }

  private async startSession(): Promise<DotCodeSession | undefined> {
    const cli = findCli();
    if (!cli) {
      await offerInstall();
      return undefined;
    }
    const config = vscode.workspace.getConfiguration("dotcode");
    const cwd = this.cwd;
    try {
      this.client ??= new DotCodeClient({ cliPath: cli, cwd });
      const settings = config.get<Record<string, unknown>>("settings");
      this.session = await this.client.createSession({
        workingDirectory: cwd,
        model: config.get<string>("model") || undefined,
        permissionMode: this.mode,
        settings: settings && Object.keys(settings).length > 0 ? settings : undefined,
        onPermissionRequest: (request) => this.askPermission(request),
        onUserInputRequest: (request) => this.askQuestions(request),
        onExitPlanMode: ({ plan }) => this.reviewPlan(plan),
      });
      this.session.on((event) => this.onEvent(event));
      this.postState();
      return this.session;
    } catch (e: any) {
      const message = `DotCode could not start: ${e?.message ?? e}`;
      this.post({ type: "error", text: message });
      void vscode.window.showErrorMessage(message);
      await this.disposeClient();
      return undefined;
    }
  }

  private onEvent(event: SessionEvent) {
    if (event.type === "model.changed" || event.type === "mode.changed") {
      if (event.type === "mode.changed") this.mode = event.mode;
      this.postState();
    }
    this.post({ type: "event", event });
  }

  /** Sends a prompt; resolves with the turn result (undefined when no session could be started or busy). */
  async send(text: string): Promise<SendResult | undefined> {
    const prompt = text.trim();
    if (!prompt) return undefined;
    if (this.busy) {
      void vscode.window.showInformationMessage("DotCode is still working. Stop it first or wait for the answer.");
      return undefined;
    }
    this.busy = true;
    this.post({ type: "user", text: prompt });
    this.postState();
    try {
      const session = await this.ensureSession();
      if (!session) return undefined;
      return await session.sendAndWait({ prompt });
    } catch (e: any) {
      this.post({ type: "error", text: String(e?.message ?? e) });
      return undefined;
    } finally {
      this.busy = false;
      for (const [id] of this.pendingPermissions) this.resolvePermission(id, "deny", "The turn ended.");
      this.postState();
    }
  }

  async stop() {
    if (this.session && this.busy) await this.session.abort().catch(() => undefined);
  }

  async newSession() {
    await this.stop();
    await this.session?.disconnect().catch(() => undefined);
    this.session = undefined;
    this.history.length = 0;
    this.post({ type: "reset" });
    this.history.length = 0;
    this.postState();
  }

  async selectModel() {
    const session = await this.ensureSession();
    if (!session || !this.client) return;
    const { models } = await this.client.listModels();
    const pick = await vscode.window.showQuickPick(
      models.filter((m) => m.provider !== "mock").map((m) => ({ label: m.qualifiedId, description: m.qualifiedId === session.model ? "current" : undefined })),
      { placeHolder: `Model for this session (current: ${session.model})`, matchOnDescription: true },
    );
    if (!pick) return;
    await session.setModel(pick.label);
    this.postState();
  }

  async setPermissionMode() {
    const modes: { label: PermissionMode; detail: string }[] = [
      { label: "default", detail: "Ask before edits and commands" },
      { label: "acceptEdits", detail: "Apply file edits in the workspace without asking" },
      { label: "auto", detail: "A classifier model approves low-risk actions, asks for the rest" },
      { label: "plan", detail: "Read-only research; DotCode proposes a plan first" },
    ];
    const pick = await vscode.window.showQuickPick(modes, { placeHolder: `Permission mode (current: ${this.mode})` });
    if (!pick) return;
    this.mode = pick.label;
    await this.session?.setPermissionMode(pick.label).catch(() => undefined);
    this.postState();
  }

  // ------------------------------------------------------------ permissions, questions, plans

  private async askPermission(request: PermissionRequest): Promise<PermissionDecision> {
    if (this.autoPermission) return this.autoPermission(request);
    const isEdit = ["Edit", "Write", "NotebookEdit"].includes(request.toolName);
    if (isEdit && vscode.workspace.getConfiguration("dotcode").get<boolean>("showDiffOnPermission")) await this.showProposedDiff(request);
    return new Promise<PermissionDecision>((resolve) => {
      this.pendingPermissions.set(request.toolUseId, { resolve, request });
      this.post({
        type: "permission",
        id: request.toolUseId,
        toolName: request.toolName,
        displayName: request.displayName,
        title: request.title,
        detail: request.detail,
        diff: request.diff,
        canAlways: !!request.suggestedRule || isEdit,
        isEdit,
      });
      // The chat may be hidden: surface the request as a notification too.
      void vscode.window
        .showInformationMessage(`DotCode wants to run ${request.displayName}`, "Allow", "Deny", "Show Chat")
        .then((choice) => {
          if (choice === "Allow") this.resolvePermission(request.toolUseId, "allow");
          else if (choice === "Deny") this.resolvePermission(request.toolUseId, "deny");
          else if (choice === "Show Chat") void vscode.commands.executeCommand("dotcode.openChat");
        });
    });
  }

  private resolvePermission(id: string, choice: ViewDecision, feedback?: string) {
    const pending = this.pendingPermissions.get(id);
    if (!pending) return;
    this.pendingPermissions.delete(id);
    // "Always" for edits means "accept edits for this session"; for commands it saves the suggested rule.
    const isEdit = ["Edit", "Write", "NotebookEdit"].includes(pending.request.toolName);
    pending.resolve(
      choice === "deny" ? PermissionDecision.reject(feedback)
      : choice === "allow" ? PermissionDecision.approveOnce()
      : isEdit ? PermissionDecision.approveForSession()
      : PermissionDecision.approveAlways(pending.request.suggestedRule),
    );
    this.post({ type: "permissionResolved", id, decision: choice });
  }

  /** Opens the file next to its proposed content in VS Code's diff editor. */
  private async showProposedDiff(request: PermissionRequest) {
    const input = request.input as Record<string, any>;
    const file = input.file_path ?? input.notebook_path;
    if (typeof file !== "string") return;
    const absolute = path.isAbsolute(file) ? file : path.join(this.cwd ?? "", file);
    let current = "";
    try {
      current = fs.readFileSync(absolute, "utf8");
    } catch {
      /* new file */
    }
    let proposed: string | undefined;
    if (request.toolName === "Write") proposed = String(input.content ?? "");
    else if (request.toolName === "Edit" && typeof input.old_string === "string") {
      const oldString = input.old_string as string;
      const newString = String(input.new_string ?? "");
      proposed = oldString === "" ? newString : input.replace_all ? current.split(oldString).join(newString) : current.replace(oldString, () => newString);
    }
    if (proposed === undefined) return;
    const uri = vscode.Uri.parse(`${PROPOSED_SCHEME}:${absolute.replace(/\\/g, "/")}?${request.toolUseId}`);
    this.proposed.set(uri.toString(), proposed);
    const original = fs.existsSync(absolute) ? vscode.Uri.file(absolute) : vscode.Uri.parse(`${PROPOSED_SCHEME}:empty?${request.toolUseId}`);
    await vscode.commands.executeCommand("vscode.diff", original, uri, `${path.basename(absolute)} ↔ DotCode proposal`, { preview: true, preserveFocus: true });
  }

  private async askQuestions({ questions }: UserInputRequest): Promise<UserQuestionAnswer[]> {
    const answers: UserQuestionAnswer[] = [];
    for (const q of questions) {
      const items = q.options.map((o) => ({ label: o.label, detail: o.description }));
      const picked = await vscode.window.showQuickPick([...items, { label: "Other…", detail: "Type your own answer" }], {
        title: q.header,
        placeHolder: q.question,
        canPickMany: !!q.multiSelect,
        ignoreFocusOut: true,
      } as vscode.QuickPickOptions);
      const list = (Array.isArray(picked) ? picked : picked ? [picked] : []) as { label: string }[];
      let answer = list.filter((p) => p.label !== "Other…").map((p) => p.label).join(", ");
      if (list.some((p) => p.label === "Other…")) answer = [answer, (await vscode.window.showInputBox({ prompt: q.question, ignoreFocusOut: true })) ?? ""].filter(Boolean).join(", ");
      answers.push({ question: q.question, answer });
    }
    return answers;
  }

  private async reviewPlan(plan: string): Promise<ExitPlanModeResult> {
    const doc = await vscode.workspace.openTextDocument({ content: plan, language: "markdown" });
    await vscode.window.showTextDocument(doc, { preview: true, viewColumn: vscode.ViewColumn.Beside });
    const choice = await vscode.window.showInformationMessage("DotCode proposes this plan. Start implementing it?", { modal: true }, "Approve", "Keep planning");
    if (choice === "Approve") {
      this.mode = "default";
      this.postState();
      return { approved: true };
    }
    return { approved: false };
  }

  // ------------------------------------------------------------ files

  async openFile(file: string, line?: number) {
    const absolute = path.isAbsolute(file) ? file : path.join(this.cwd ?? "", file);
    const doc = await vscode.workspace.openTextDocument(absolute);
    const editor = await vscode.window.showTextDocument(doc, { preview: true });
    if (line && line > 0) {
      const pos = new vscode.Position(line - 1, 0);
      editor.selection = new vscode.Selection(pos, pos);
      editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
    }
  }

  private async disposeClient() {
    const client = this.client;
    this.client = undefined;
    this.session = undefined;
    await client?.stop().catch(() => undefined);
  }

  async dispose() {
    for (const [id] of this.pendingPermissions) this.resolvePermission(id, "deny");
    await this.disposeClient();
    for (const d of this.disposables) d.dispose();
  }
}
