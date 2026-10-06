// Screenshots of artifacts produced by bots during the trial run (served from the workspace file API).
// Usage: node artifacts.mjs <baseUrl> <outDir> <webAppThread> <landingThread> <docThread>
import { chromium } from "playwright";

const [base, out, webApp, landing, doc] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 860 } });
const file = (thread, path) => `${base}/api/v1/threads/${thread}/files/${path}`;

// Kas Warung: add two transactions so the screenshot shows real behaviour.
await page.goto(file(webApp, "apps/kas-warung/index.html"), { waitUntil: "networkidle" });
const fill = async (selectors, value) => {
  for (const s of selectors) {
    const el = await page.$(s);
    if (el) { await el.fill(String(value)); return true; }
  }
  return false;
};
for (const [desc, cat, amount, kind] of [["Penjualan kopi susu", "Penjualan", 185000, "masuk"], ["Beli gula & susu", "Bahan baku", 64000, "keluar"], ["Penjualan gorengan", "Penjualan", 72000, "masuk"]]) {
  await fill(["#description", "#desc", "#keterangan", "input[name=description]", "input[name=keterangan]"], desc);
  await fill(["#category", "#kategori", "input[name=category]", "input[name=kategori]"], cat);
  await fill(["#amount", "#nominal", "input[name=amount]", "input[name=nominal]", "input[type=number]"], amount);
  const select = await page.$("#type, #jenis, select[name=type], select[name=jenis]");
  if (select) {
    const options = await select.$$eval("option", os => os.map(o => o.value));
    const match = options.find(v => kind === "masuk" ? /in|masuk|income/i.test(v) : /out|keluar|expense/i.test(v));
    if (match) await select.selectOption(match);
  } else {
    const radio = await page.$(kind === "masuk" ? "input[value*=masuk i], input[value*=in i]" : "input[value*=keluar i], input[value*=out i]");
    await radio?.check();
  }
  const submit = await page.$("form button[type=submit], form button:not([type]), #addBtn, #add-btn");
  await submit?.click();
  await page.waitForTimeout(400);
}
await page.screenshot({ path: `${out}/trial-kas-warung.png`, fullPage: false });
await page.setViewportSize({ width: 390, height: 844 });
await page.screenshot({ path: `${out}/trial-kas-warung-mobile.png` });

await page.setViewportSize({ width: 1280, height: 860 });
await page.goto(file(landing, "apps/kopi-kilat/index.html"), { waitUntil: "networkidle" });
await page.screenshot({ path: `${out}/trial-kopi-kilat.png` });

await page.goto(file(doc, "docs/onboarding.html"), { waitUntil: "networkidle" });
await page.screenshot({ path: `${out}/trial-onboarding-html.png` });

await browser.close();
console.log("artifact screenshots saved");
