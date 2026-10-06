// Screenshot with the UI language set to Bahasa Indonesia.
import { chromium } from "playwright";
const [base = "http://localhost:5170", out = "../../docs/images"] = process.argv.slice(2);
const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await ctx.addInitScript(() => localStorage.setItem("marbots.lang", "id"));
const page = await ctx.newPage();
for (const [name, path] of [["chat-id", "/"], ["templates-id", "/templates"]]) {
  await page.goto(base + path, { waitUntil: "networkidle" });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: `${out}/${name}.png` });
}
await browser.close();
console.log("saved");
