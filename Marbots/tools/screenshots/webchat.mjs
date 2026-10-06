// Drives the web chat widget with a real question and captures the conversation.
import { chromium } from "playwright";
const [url, out, question] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 420, height: 720 } });
await page.goto(url, { waitUntil: "networkidle" });
await page.fill("#t", question);
await page.click("#b");
await page.waitForFunction(() => document.querySelectorAll(".m.assistant").length >= 2, null, { timeout: 240000 });
await page.waitForTimeout(800);
await page.screenshot({ path: out });
console.log(await page.$$eval(".m.assistant", els => els.at(-1).textContent.slice(0, 300)));
await browser.close();
