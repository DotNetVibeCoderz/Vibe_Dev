using System.Net;
using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Server.Api;

/// <summary>The embeddable web chat page (/webchat/{channelId}). Embed with an iframe.</summary>
public static class WebChatPage
{
    public static string Html(ChannelConfig c)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var title = E(ChannelContext.Setting(c, "title", c.Name));
        var welcome = E(ChannelContext.Setting(c, "welcome", "Hi! How can we help you today?"));
        var color = E(ChannelContext.Setting(c, "color", "#2C3BA3"));
        var id = E(c.Id);
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{title}}</title>
<style>
  :root { --c: {{color}}; --bg:#EEF1F6; --panel:#fff; --ink:#161C34; --muted:#5B6482; --line:#D9DFEA; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0E1224; --panel:#151B32; --ink:#E7EBF7; --muted:#9AA3C2; --line:#2A3356; } }
  * { box-sizing: border-box; }
  html, body { margin: 0; height: 100%; background: var(--bg); color: var(--ink); font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
  .wrap { display: flex; flex-direction: column; height: 100%; max-width: 720px; margin: 0 auto; }
  header { display: flex; gap: 10px; align-items: center; padding: 12px 16px; border-bottom: 1px solid var(--line); background: var(--panel); }
  .marble { width: 30px; height: 30px; border-radius: 50%; flex: none;
    background: radial-gradient(circle at 32% 26%, rgba(255,255,255,.95) 0 7%, rgba(255,255,255,0) 22%),
                radial-gradient(circle at 40% 35%, color-mix(in oklab, var(--c) 45%, white) 0%, var(--c) 55%, color-mix(in oklab, var(--c) 55%, black) 100%);
    box-shadow: inset -3px -5px 9px rgba(0,0,0,.25); }
  header b { font-size: 1rem; } header small { display: block; color: var(--muted); font-size: .75rem; }
  #log { flex: 1; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 10px; }
  .m { max-width: 85%; padding: 9px 13px; border-radius: 14px; white-space: pre-wrap; overflow-wrap: anywhere; }
  .assistant { background: var(--panel); border: 1px solid var(--line); align-self: flex-start; border-bottom-left-radius: 4px; }
  .user { background: var(--c); color: #fff; align-self: flex-end; border-bottom-right-radius: 4px; }
  .typing { color: var(--muted); font-size: .85rem; padding: 0 16px 6px; min-height: 1.4em; }
  form { display: flex; gap: 8px; padding: 12px 16px 16px; }
  input { flex: 1; border: 1px solid var(--line); border-radius: 12px; padding: 10px 12px; font: inherit; background: var(--panel); color: var(--ink); }
  button { border: 0; border-radius: 12px; padding: 0 18px; background: var(--c); color: #fff; font: 600 .95rem inherit; cursor: pointer; }
  button:disabled { opacity: .5; }
  footer { text-align: center; color: var(--muted); font-size: .7rem; padding-bottom: 8px; }
</style>
</head>
<body>
<div class="wrap">
  <header><span class="marble"></span><div><b>{{title}}</b><small>Powered by Marbots</small></div></header>
  <div id="log" aria-live="polite"><div class="m assistant">{{welcome}}</div></div>
  <div class="typing" id="typing"></div>
  <form id="f"><input id="t" autocomplete="off" placeholder="Type a message…" aria-label="Message" maxlength="4000"><button id="b">Send</button></form>
  <footer>Created by Gravicode Studios, led by Kang Fadhil</footer>
</div>
<script>
(() => {
  const ch = "{{id}}", key = "marbots.webchat." + ch;
  let cid; try { cid = localStorage.getItem(key); } catch {}
  if (!cid) { cid = crypto.randomUUID(); try { localStorage.setItem(key, cid); } catch {} }
  const log = document.getElementById("log"), typing = document.getElementById("typing");
  let after = 0, seen = new Set();
  function add(role, text) {
    const d = document.createElement("div"); d.className = "m " + role; d.textContent = text;
    log.appendChild(d); log.scrollTop = log.scrollHeight;
  }
  async function poll() {
    try {
      const r = await fetch(`/webchat/${ch}/messages?conversationId=${cid}&after=${after}`);
      if (r.ok) {
        const j = await r.json();
        for (const m of j.messages) { if (!seen.has(m.seq)) { seen.add(m.seq); add(m.role, m.text); } after = Math.max(after, m.seq); }
        typing.textContent = j.working ? "Typing…" : "";
      }
    } catch {}
    setTimeout(poll, 1500);
  }
  document.getElementById("f").addEventListener("submit", async (e) => {
    e.preventDefault();
    const t = document.getElementById("t"), text = t.value.trim();
    if (!text) return;
    t.value = ""; typing.textContent = "Sending…";
    await fetch(`/webchat/${ch}/messages`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ conversationId: cid, text }) });
  });
  poll();
})();
</script>
</body>
</html>
""";
    }
}
