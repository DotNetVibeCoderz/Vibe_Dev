// Small helpers for the Blazor UI. Everything degrades gracefully when storage is blocked.
window.marbots = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch { } },
  setTheme(theme) {
    if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme;
    this.set("marbots.theme", theme || "");
  },
  scrollToEnd(el, force) {
    if (!el) return;
    const nearEnd = el.scrollHeight - el.scrollTop - el.clientHeight < 160;
    if (force || nearEnd) el.scrollTop = el.scrollHeight;
  },
  bindComposer(textarea, button) {
    if (!textarea || textarea.dataset.bound) return;
    textarea.dataset.bound = "1";
    const resize = () => { textarea.style.height = "auto"; textarea.style.height = Math.min(textarea.scrollHeight, 220) + "px"; };
    textarea.addEventListener("input", resize);
    textarea.addEventListener("keydown", e => {
      if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); button?.click(); setTimeout(resize, 0); }
    });
  },
  focus(el) { el?.focus(); }
};
