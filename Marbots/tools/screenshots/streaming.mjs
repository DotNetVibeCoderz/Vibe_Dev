// Sends a chat message in the web UI and captures the reply while it is still streaming (live text with caret).
import { chromium } from "playwright";
const [base, path, out, question] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
await page.goto(base + path, { waitUntil: "networkidle" });
await page.waitForTimeout(1500);
await page.fill("textarea", question);
await page.click("button.btn.primary:has-text('Send'), button.btn.primary:has-text('Kirim')");
await page.waitForSelector(".md.streaming", { timeout: 180000 });
await page.waitForFunction(() => (document.querySelector(".md.streaming")?.textContent?.length ?? 0) > 180, null, { timeout: 180000 });
await page.screenshot({ path: out });
console.log("saved", out);
await browser.close();
