// DotCode chat webview. Receives engine events from the extension host and renders them; no framework, no remote code.
(function () {
  const vscode = typeof acquireVsCodeApi === "function" ? acquireVsCodeApi() : { postMessage: (m) => window.parent?.postMessage?.(m, "*") };
  const $ = (id) => document.getElementById(id);
  const messages = $("messages");
  const prompt = $("prompt");
  const sendButton = $("send");
  const stopButton = $("stop");
  const status = $("status");
  let assistant = null;      // current streaming assistant bubble
  let busy = false;
  let startedAt = 0;
  let timer = null;
  const tools = new Map();   // toolUseId -> element

  // ---------------------------------------------------------------- markdown (escaped first, then formatted)
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  }
  function inline(s) {
    return s
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[^*])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>")
      .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2">$1</a>');
  }
  function markdown(text) {
    const out = [];
    const lines = String(text).replace(/\r/g, "").split("\n");
    let inCode = false, code = [], lang = "", list = null;
    const closeList = () => { if (list) { out.push(`</${list}>`); list = null; } };
    for (const raw of lines) {
      const fence = raw.match(/^\s*```(\w*)/);
      if (fence) {
        if (inCode) { out.push(`<pre><code class="lang-${escapeHtml(lang)}">${escapeHtml(code.join("\n"))}</code></pre>`); code = []; inCode = false; }
        else { closeList(); inCode = true; lang = fence[1]; }
        continue;
      }
      if (inCode) { code.push(raw); continue; }
      const line = escapeHtml(raw);
      const heading = line.match(/^(#{1,4})\s+(.*)$/);
      const bullet = line.match(/^\s*[-*]\s+(.*)$/);
      const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);
      if (heading) { closeList(); out.push(`<h${heading[1].length + 2}>${inline(heading[2])}</h${heading[1].length + 2}>`); }
      else if (bullet || numbered) {
        const kind = bullet ? "ul" : "ol";
        if (list !== kind) { closeList(); out.push(`<${kind}>`); list = kind; }
        out.push(`<li>${inline((bullet || numbered)[1])}</li>`);
      }
      else if (line.trim() === "") { closeList(); }
      else { closeList(); out.push(`<p>${inline(line)}</p>`); }
    }
    if (inCode) out.push(`<pre><code>${escapeHtml(code.join("\n"))}</code></pre>`);
    closeList();
    return out.join("");
  }

  function diffHtml(diff) {
    return String(diff).split("\n").slice(0, 400).map((l) => {
      const cls = l.startsWith("+") && !l.startsWith("+++") ? "add" : l.startsWith("-") && !l.startsWith("---") ? "del" : l.startsWith("@@") ? "hunk" : "";
      return `<div class="dl ${cls}">${escapeHtml(l) || "&nbsp;"}</div>`;
    }).join("");
  }

  // ---------------------------------------------------------------- rendering helpers
  function add(el) {
    $("welcome")?.remove();
    messages.appendChild(el);
    messages.scrollTop = messages.scrollHeight;
    return el;
  }
  function div(cls, html) {
    const el = document.createElement("div");
    el.className = cls;
    if (html !== undefined) el.innerHTML = html;
    return el;
  }
  function endAssistant() { assistant = null; }

  function toolRow(e) {
    const row = div("tool running" + (e.parentToolUseId ? " nested" : ""));
    const name = escapeHtml(e.displayName || e.name);
    row.innerHTML = `<div class="tool-head"><span class="dot"></span><span class="tool-name">${name}</span><span class="tool-summary"></span></div><div class="tool-body"></div>`;
    tools.set(e.toolUseId, row);
    const parent = e.parentToolUseId && tools.get(e.parentToolUseId);
    if (parent) { parent.querySelector(".tool-body").appendChild(row); return row; }
    return add(row);
  }

  function completeTool(e) {
    const row = tools.get(e.toolUseId) || toolRow({ ...e, displayName: e.name });
    row.classList.remove("running");
    row.classList.add(e.rejected ? "rejected" : e.isError ? "error" : "ok");
    row.querySelector(".tool-summary").textContent = e.rejected ? "rejected" : e.summary || "";
    const body = row.querySelector(".tool-body");
    if (e.diff) {
      const diff = div("diff", diffHtml(e.diff));
      body.appendChild(diff);
      const file = (e.diff.match(/^\+\+\+ (?:b\/)?(.+)$/m) || [])[1];
      if (file) {
        const open = div("open-file", `<a href="#">Open ${escapeHtml(file)}</a>`);
        open.querySelector("a").addEventListener("click", (ev) => { ev.preventDefault(); vscode.postMessage({ type: "openFile", path: file }); });
        body.appendChild(open);
      }
    } else if (e.output && e.name !== "TodoWrite") {
      const details = document.createElement("details");
      details.innerHTML = `<summary>output</summary><pre>${escapeHtml(e.output.slice(0, 20000))}</pre>`;
      if (e.isError) details.open = true;
      body.appendChild(details);
    }
  }

  function todos(list) {
    let panel = $("todos");
    if (!panel) { panel = div("todos"); panel.id = "todos"; add(panel); }
    const icon = { completed: "☒", in_progress: "◼", pending: "☐" };
    panel.innerHTML = "<div class=\"todos-title\">Todos</div>" + list.map((t) =>
      `<div class="todo ${escapeHtml(t.status)}">${icon[t.status] || "☐"} ${escapeHtml(t.status === "in_progress" && t.activeForm ? t.activeForm : t.content)}</div>`).join("");
  }

  function setBusy(value) {
    busy = value;
    sendButton.hidden = value;
    stopButton.hidden = !value;
    clearInterval(timer);
    if (value) {
      startedAt = Date.now();
      const verbs = ["Thinking", "Pondering", "Working", "Cogitating", "Brewing", "Crafting", "Reticulating"];
      const verb = verbs[Math.floor(Math.random() * verbs.length)];
      const tick = () => { status.innerHTML = `<span class="spinner"></span>${verb}… <span class="dim">(${Math.round((Date.now() - startedAt) / 1000)}s · esc/Stop to interrupt)</span>`; };
      tick();
      timer = setInterval(tick, 1000);
    } else status.textContent = "";
  }

  // ---------------------------------------------------------------- events from the host
  function onEvent(e) {
    switch (e.type) {
      case "assistant.text.delta":
        if (e.parentToolUseId) return;
        if (!assistant) assistant = add(div("msg assistant"));
        assistant.dataset.raw = (assistant.dataset.raw || "") + e.text;
        assistant.textContent = assistant.dataset.raw;
        messages.scrollTop = messages.scrollHeight;
        break;
      case "assistant.message":
        if (e.parentToolUseId) return;
        if (e.text && e.text.trim()) {
          if (!assistant) assistant = add(div("msg assistant"));
          assistant.innerHTML = markdown(e.text);
        } else if (assistant) assistant.remove();
        endAssistant();
        break;
      case "tool.started": endAssistant(); toolRow(e); break;
      case "tool.progress": {
        const row = tools.get(e.toolUseId);
        if (row) row.querySelector(".tool-summary").textContent = String(e.text).split("\n").pop();
        break;
      }
      case "tool.completed": completeTool(e); break;
      case "todo.updated": todos(e.todos); break;
      case "notice": add(div("notice " + String(e.level).toLowerCase(), escapeHtml(e.text))); break;
      case "error": add(div("notice error", "API error: " + escapeHtml(e.message))); break;
      case "model.fallback": add(div("notice warning", `${escapeHtml(e.from)} unavailable — switched to ${escapeHtml(e.to)}`)); break;
      case "context.compacted": add(div("notice info", `Conversation compacted (${e.tokensBefore} → ${e.tokensAfter} tokens)`)); break;
      case "usage.updated":
        if (e.contextWindow) $("context").textContent = `${Math.round((e.contextTokens / e.contextWindow) * 100)}% context · $${Number(e.sessionCostUsd).toFixed(4)}`;
        break;
      case "turn.completed":
        if (e.parentToolUseId) return;
        endAssistant();
        add(div("turn-stats", `${e.stopReason === "Aborted" ? "Interrupted · " : ""}${e.numModelCalls} model call${e.numModelCalls === 1 ? "" : "s"} · ${(e.durationMs / 1000).toFixed(1)}s · $${Number(e.costUsd).toFixed(4)}`));
        break;
    }
  }

  function permission(m) {
    endAssistant();
    const box = div("permission");
    box.dataset.id = m.id;
    box.innerHTML = `<div class="perm-title">${escapeHtml(m.title || m.displayName)}</div>
      <div class="perm-target">${escapeHtml(m.displayName)}</div>
      ${m.detail ? `<div class="perm-detail">${escapeHtml(m.detail)}</div>` : ""}
      ${m.diff ? `<div class="diff">${diffHtml(m.diff)}</div>` : ""}
      <div class="perm-question">${m.isEdit ? "Apply this edit?" : "Do you want to proceed?"}</div>
      <div class="perm-buttons">
        <button data-d="allow">Yes</button>
        ${m.canAlways ? `<button data-d="allow_always" class="secondary">${m.isEdit ? "Yes, allow all edits this session" : "Yes, don't ask again"}</button>` : ""}
        <button data-d="deny" class="secondary">No</button>
      </div>
      <input class="perm-feedback" placeholder="Tell DotCode what to do instead (optional, with No)">`;
    box.querySelectorAll("button").forEach((b) => b.addEventListener("click", () => {
      const feedback = box.querySelector(".perm-feedback").value.trim();
      vscode.postMessage({ type: "permission", id: m.id, decision: b.dataset.d, feedback: b.dataset.d === "deny" && feedback ? feedback : undefined });
    }));
    add(box);
    box.querySelector("button").focus();
  }

  window.addEventListener("message", (ev) => {
    const m = ev.data;
    switch (m.type) {
      case "user": endAssistant(); add(div("msg user")).textContent = m.text; break;
      case "event": onEvent(m.event); break;
      case "permission": permission(m); break;
      case "permissionResolved": {
        const box = messages.querySelector(`.permission[data-id="${CSS.escape(m.id)}"]`);
        if (box) { box.classList.add("resolved"); box.querySelector(".perm-buttons").innerHTML = `<span class="decision ${m.decision === "deny" ? "no" : "yes"}">${m.decision === "deny" ? "Denied" : "Allowed"}</span>`; box.querySelector(".perm-feedback")?.remove(); }
        break;
      }
      case "state":
        setBusy(m.busy);
        $("model").textContent = m.model || "";
        $("mode").textContent = m.mode && m.mode !== "default" ? m.mode : "";
        $("mode").className = "mode-" + (m.mode || "default");
        break;
      case "error": add(div("notice error", escapeHtml(m.text))); break;
      case "reset": messages.innerHTML = ""; tools.clear(); endAssistant(); $("context").textContent = ""; break;
      case "prefill": prompt.value = m.text + (prompt.value ? "\n" + prompt.value : ""); prompt.focus(); prompt.setSelectionRange(prompt.value.length, prompt.value.length); break;
    }
  });

  // ---------------------------------------------------------------- input
  function send() {
    const text = prompt.value.trim();
    if (!text || busy) return;
    vscode.postMessage({ type: "send", text });
    prompt.value = "";
  }
  sendButton.addEventListener("click", send);
  stopButton.addEventListener("click", () => vscode.postMessage({ type: "stop" }));
  prompt.addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); send(); }
    else if (ev.key === "Escape" && busy) { ev.preventDefault(); vscode.postMessage({ type: "stop" }); }
  });
  document.querySelectorAll("[data-command]").forEach((a) => a.addEventListener("click", (ev) => { ev.preventDefault(); vscode.postMessage({ type: "command", command: a.dataset.command }); }));
  messages.addEventListener("click", (ev) => {
    const a = ev.target.closest("a[href^='http']");
    if (a) { ev.preventDefault(); vscode.postMessage({ type: "command", command: "vscode.open", url: a.href }); }
  });
  vscode.postMessage({ type: "ready" });
})();
