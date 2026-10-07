// Creates a bot through the web UI (bot editor), the way a person would, and captures the editor.
// Usage: node create-bot-ui.mjs <baseUrl> <outDir>
import { chromium } from "playwright";
const [base, out] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1500 } });
await page.goto(base + "/team/new?template=qa-engineer", { waitUntil: "networkidle" });
await page.waitForTimeout(1200);
const field = label => page.locator("label.field", { has: page.locator("span", { hasText: label }) }).first();
await field("Name").locator("input").fill("Rina");
await field("Role").locator("input").fill("QA and computer-use tester");
await field("Description").locator("input").fill("Tests apps on the build PC by using its desktop like a person: screenshots, clicks and typing.");
// Tool packs: add desktop (computer use) and shell.
for (const pack of ["desktop", "shell", "files"]) {
  const box = page.locator("label", { has: page.locator("span", { hasText: new RegExp("^" + pack) }) }).locator("input[type=checkbox]").first();
  if (!(await box.isChecked())) await box.check();
}
for (const skill of ["webapp-testing"]) {
  const box = page.locator("label", { has: page.locator("span", { hasText: new RegExp("^" + skill) }) }).locator("input[type=checkbox]").first();
  if (await box.count() && !(await box.isChecked())) await box.check();
}
await field("Permission profile").locator("select").selectOption("autonomous");
const host = field("Host").locator("select");
const dev2 = await host.locator("option", { hasText: "DEV2" }).first().getAttribute("value");
await host.selectOption(dev2);
await page.waitForTimeout(500);
await page.screenshot({ path: `${out}/bot-editor-remote-host.png`, fullPage: false, clip: { x: 0, y: 0, width: 1440, height: 1500 } });
await page.locator("button.btn.primary", { hasText: /Save|Simpan/ }).first().click();
await page.waitForTimeout(2000);
console.log("saved; now at", page.url());
await browser.close();
