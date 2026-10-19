// Real-browser check (Chromium, Firefox, WebKit) of the claim the Menu Editor rests on: a programmatic edit applied through text-adapter.js is exactly
// ONE step of the browser's own undo history, typing stays undoable around it, and nothing is lost. jsdom cannot prove this (it has no execCommand).
// Run: docker compose run --rm browser     (or: cd tests/browser && npm ci && npx playwright install --with-deps && npm test)
import fs from "node:fs";
import { chromium, firefox, webkit } from "playwright";
const W = new URL("../../Jellyfin.Plugin.DiscMenus/Web/editor/", import.meta.url).pathname;
const jsonText = fs.readFileSync(W + "json-text.js", "utf8"), adapterSrc = fs.readFileSync(W + "text-adapter.js", "utf8");
const menu = fs.readFileSync(new URL("../../examples/static-backdrop.menu.json", import.meta.url), "utf8");
let failures = 0;
const check = (name, ok, extra) => { if (!ok) failures++; console.log((ok ? "  PASS " : "  FAIL ") + name + (extra ? "  " + extra : "")); };
for (const [name, type] of [["chromium", chromium], ["firefox", firefox], ["webkit", webkit]]) {
  console.log(name);
  const browser = await type.launch();
  const page = await browser.newPage();
  await page.setContent('<button id="other">other</button><textarea id="t" style="width:600px;height:240px"></textarea>');
  await page.addScriptTag({ content: jsonText });
  await page.addScriptTag({ content: adapterSrc });
  await page.evaluate(() => { window.t = document.getElementById("t"); window.a = DiscMenusTextAdapter.create(window.t); window.inputs = 0; window.t.addEventListener("input", () => window.inputs++); });
  const undo = async () => { await page.focus("#t"); await page.keyboard.press("Control+z"); };
  const redo = async () => { await page.focus("#t"); await page.keyboard.press("Control+Shift+z"); };
  const val = () => page.evaluate(() => window.t.value);

  // typed text, then a programmatic edit, then undo
  await page.focus("#t"); await page.keyboard.type("hello");
  const r1 = await page.evaluate(() => { const ok = window.a.apply("hello world"); return { ok, native: window.a.usesNative, v: window.t.value }; });
  check("applies the edit", r1.ok && r1.v === "hello world");
  check("through the browser's own editing path (native)", r1.native === true);
  await undo(); check("one Ctrl+Z undoes exactly the programmatic edit", (await val()) === "hello", JSON.stringify(await val()));
  await undo(); check("the next Ctrl+Z undoes the typing (history is intact)", (await val()) === "", JSON.stringify(await val()));
  await redo(); await redo();
  check("redo brings both back, in order", (await val()) === "hello world", JSON.stringify(await val()));

  // a realistic edit: patch one property of a whole menu file, undo restores the file byte for byte
  await page.evaluate((m) => { window.t.value = ""; window.t.focus(); document.execCommand("insertText", false, m); }, menu);
  const patched = await page.evaluate((m) => { const J = DiscMenusJsonText; return J.set(m, ["revision"], "7"); }, menu);
  const r2 = await page.evaluate((p) => { window.inputs = 0; return { ok: window.a.apply(p), native: window.a.usesNative, v: window.t.value === p, inputs: window.inputs }; }, patched);
  check("a JSON patch to a whole menu file applies natively", r2.ok && r2.native && r2.v, "inputs=" + r2.inputs);
  await undo(); check("and one Ctrl+Z restores the file exactly", (await val()) === menu);
  await redo(); check("redo re-applies it", (await val()) === patched);

  // removal and insertion in the middle, a big insertion, unicode
  await undo();
  const big = await page.evaluate((m) => { const J = DiscMenusJsonText; return J.set(m, ["meta", "notes"], JSON.stringify("é😀 ".repeat(2000))); }, menu);
  const r3 = await page.evaluate((p) => ({ ok: window.a.apply(p), native: window.a.usesNative, same: window.t.value === p }), big);
  check("a large edit with non-ASCII text applies natively and exactly", r3.ok && r3.native && r3.same);
  await undo(); check("and undoes in one step", (await val()) === menu);

  // focus goes back to what had it, the caret lands after the change
  await page.focus("#other");
  const r4 = await page.evaluate(() => { window.a.apply(window.t.value + " "); return document.activeElement.id; });
  check("focus returns to the control that had it (a button in a form panel)", r4 === "other", r4);
  await page.evaluate(() => { window.t.value = "abcdef"; });
  const r5 = await page.evaluate(() => { window.a.apply("abXYef", 4); return [window.t.selectionStart, window.t.selectionEnd, window.t.value]; });
  check("a caret position can be given, and the text is right", r5[0] === 4 && r5[1] === 4 && r5[2] === "abXYef", JSON.stringify(r5));

  // the adapter's own undo() drives the native one
  await page.evaluate(() => { window.t.value = ""; });
  await page.focus("#t"); await page.keyboard.type("x");
  await page.evaluate(() => { window.a.apply("xy"); });
  const u = await page.evaluate(() => ({ did: window.a.undo(), v: window.t.value }));
  check("adapter.undo() performs a native undo", u.did === true && u.v === "x", JSON.stringify(u));
  await browser.close();
}
console.log(failures ? failures + " FAILED" : "all passed"); process.exit(failures ? 1 : 0);
