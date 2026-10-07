// Captures screenshots of a running Marbots instance for README and docs.
// Usage: node shoot.mjs [baseUrl] [outDir] [page1,page2,...]
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

const base = process.argv[2] ?? "http://localhost:5170";
const out = process.argv[3] ?? "../../docs/images";
const only = process.argv[4]?.split(",");
mkdirSync(out, { recursive: true });

const pages = [
  { name: "chat", path: "/" },
  { name: "team", path: "/team" },
  { name: "templates", path: "/templates" },
  { name: "bot-editor", path: "/team/new?template=data-analyst" },
  { name: "office", path: "/office" },
  { name: "tasks", path: "/tasks" },
  { name: "approvals", path: "/approvals" },
  { name: "skills", path: "/skills" },
  { name: "mcp", path: "/mcp" },
  { name: "schedules", path: "/schedules" },
  { name: "memory", path: "/memory" },
  { name: "dashboard", path: "/dashboard" },
  { name: "settings", path: "/settings" },
  { name: "about", path: "/about" },
];

// Extra pages: EXTRA="name=/path;name2=/path2"
for (const pair of (process.env.EXTRA ?? "").split(";").filter(Boolean)) {
  const [name, path] = pair.split("=");
  pages.push({ name, path });
}
const darkPages = (process.env.DARK ?? "chat,office,team").split(",");

const browser = await chromium.launch();
for (const theme of ["light", "dark"]) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, colorScheme: theme });
  const page = await ctx.newPage();
  for (const p of pages) {
    if (only && !only.includes(p.name)) continue;
    if (theme === "dark" && !darkPages.includes(p.name)) continue;
    await page.goto(base + p.path, { waitUntil: "networkidle" });
    await page.waitForTimeout(Number(process.env.WAIT ?? 1500));
    // Hide private details (user names, LAN addresses) before capturing docs screenshots.
    await page.evaluate(() => {
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      while (walker.nextNode()) {
        const n = walker.currentNode;
        n.nodeValue = n.nodeValue.replace(/ssh \S+@[\d.]+/g, "ssh user@dev2.lan").replace(/192\.168\.\d+\.\d+/g, "192.168.1.20");
      }
    });
    const file = `${out}/${p.name}${theme === "dark" ? "-dark" : ""}.png`;
    await page.screenshot({ path: file });
    console.log("saved", file);
  }
  await ctx.close();
}
await browser.close();
