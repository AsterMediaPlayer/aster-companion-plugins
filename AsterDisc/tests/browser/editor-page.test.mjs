// The real Menu Editor page in real browsers (Chromium, Firefox, WebKit): choosing a part, editing it through its form, and the browser's own
// undo taking that form edit back in ONE step. Also leaves screenshots in tests/browser/shots/ (not committed) for a human to look at.
import fs from "node:fs";
import { chromium, firefox, webkit } from "playwright";
const ROOT = new URL("../../", import.meta.url).pathname;
const ED = ROOT + "Jellyfin.Plugin.DiscMenus/Web/editor/";
const MODULES = ["json-text", "text-adapter", "outline", "schema-hints", "inspector", "pictures"].map((n) => fs.readFileSync(ED + n + ".js", "utf8"));
const html = fs.readFileSync(ROOT + "Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html", "utf8");
const body = /<body>([\s\S]*)<\/body>/.exec(html)[1].replace(/<script type="text\/javascript">[\s\S]*?<\/script>/, "");
const script = /<script type="text\/javascript">([\s\S]*?)<\/script>/.exec(html)[1];
const menu = fs.readFileSync(ROOT + "examples/example.menu.json", "utf8");
fs.mkdirSync(new URL("./shots/", import.meta.url).pathname, { recursive: true });
let failures = 0;
const check = (name, ok, extra) => { if (!ok) failures++; console.log((ok ? "  PASS " : "  FAIL ") + name + (extra ? "  " + extra : "")); };

for (const [name, type] of [["chromium", chromium], ["firefox", firefox], ["webkit", webkit]]) {
  console.log(name);
  const browser = await type.launch();
  const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
  page.on("pageerror", (e) => { failures++; console.log("  PAGE ERROR " + e.message); });
  await page.setContent("<!doctype html><meta charset=utf-8><body style='background:#101010;color:#eee;font-family:sans-serif'>" + body + "</body>");
  await page.evaluate((menuText) => {
    window.ApiClient = {
      getUrl: (p) => (p.endsWith("preview.html") ? "about:blank" : "http://x.test/" + p),
      ajax: (req) => {
        const p = new URL(req.url).pathname.slice(1);
        const ok = (b) => Promise.resolve(b);
        if (p === "DiscMenus/Editor/Files") return ok([{ File: "m.menu.json", Version: "v1", State: "bound" }]);
        if (p === "DiscMenus/Editor/File") return ok({ File: "m.menu.json", Json: menuText, Version: "v1" });
        if (p === "DiscMenus/Editor/Preview") return ok({ Document: { Root: "main", Menus: { main: { Title: "Main" } } }, ParentItemId: null, Bound: false });
        return Promise.reject({ status: 500, json: () => Promise.resolve(null) });
      }
    };
    window.__discMenusEditorModulesPreloaded = true;
  }, menu);
  for (const m of MODULES) await page.addScriptTag({ content: m });
  await page.addScriptTag({ content: script });
  await page.evaluate(() => document.querySelector("#DiscMenusEditorPage").dispatchEvent(new CustomEvent("pageshow")));
  await page.waitForSelector("#discEdInspector [data-key='menus/main/title']");
  check("the first page's form appears, and the raw text starts hidden", !(await page.evaluate(() => { const r = document.querySelector("#discEdText").getBoundingClientRect(); return r.right > 0 && r.left < innerWidth; })));
  await page.screenshot({ path: new URL("./shots/" + name + "-1-whole.png", import.meta.url).pathname });

  const outline = page.locator("#discEdOutline button", { hasText: "Play Movie - Play feature" }).first();
  await outline.click();
  const labelKey = "[data-key='menus/main/entries/0/label']";
  await page.waitForSelector("#discEdInspector " + labelKey);
  const original = await page.inputValue("#discEdText");
  await page.fill("#discEdInspector " + labelKey, "Play it now");
  await page.keyboard.press("Tab"); // the change event
  await page.waitForFunction(() => document.querySelector("#discEdText").value.includes("Play it now"));
  const edited = await page.inputValue("#discEdText");
  check("the form edit changed only the label in the text box", edited === original.replace('"label": "Play Movie"', '"label": "Play it now"'), "");
  check("the form was redrawn from the text", (await page.inputValue("#discEdInspector " + labelKey)) === "Play it now");
  await page.screenshot({ path: new URL("./shots/" + name + "-2-entry.png", import.meta.url).pathname });

  // the text is hidden, so undo is a button
  await page.click("#discEdUndo");
  check("the Undo button takes the whole form edit back (text hidden)", (await page.inputValue("#discEdText")) === original);
  check("and the form follows", (await page.inputValue("#discEdInspector " + labelKey)) === "Play Movie");
  await page.click("#discEdRedo");
  check("Redo puts it back", (await page.inputValue("#discEdText")) === edited);
  await page.focus("#discEdInspector [data-key='menus/main/entries/0/style']");
  await page.keyboard.press("Control+z");
  check("Ctrl+Z with a choice focused undoes it too", (await page.inputValue("#discEdText")) === original);
  await page.keyboard.press("Control+Shift+z");

  // with the text shown, edits go through the browser's own undo
  await page.click("#discEdToggleText");
  await page.screenshot({ path: new URL("./shots/" + name + "-3-with-text.png", import.meta.url).pathname });
  await page.locator("#discEdOutline button", { hasText: "Play it now" }).first().click();
  const shownBefore = await page.inputValue("#discEdText");
  await page.fill("#discEdInspector " + labelKey, "Shown edit");
  await page.keyboard.press("Tab");
  await page.waitForFunction(() => document.querySelector("#discEdText").value.includes("Shown edit"));
  await page.focus("#discEdText");
  await page.keyboard.press("Control+z");
  check("with the text shown, one Ctrl+Z in the text box takes the form edit back", (await page.inputValue("#discEdText")) === shownBefore);
  await page.keyboard.press("Control+Shift+z");
  check("and redo puts it back", (await page.inputValue("#discEdText")).includes("Shown edit"));

  // a second kind of control: a choice, then undo
  await page.locator("#discEdOutline button", { hasText: "Shown edit" }).first().click();
  await page.selectOption("#discEdInspector [data-key='menus/main/entries/0/style']", "glow");
  await page.waitForFunction(() => document.querySelector("#discEdText").value.includes('"style": "glow"'));
  check("a choice writes the property", true);
  await page.focus("#discEdText"); await page.keyboard.press("Control+z");
  check("and undoes in one step", !(await page.inputValue("#discEdText")).includes('"style": "glow"'));

  // typing in the text box still works and redraws the form
  await page.evaluate(() => { const t = document.querySelector("#discEdText"); t.focus(); t.setSelectionRange(0, 0); });
  await page.keyboard.type(" ");
  check("typing in the text box still works", (await page.inputValue("#discEdText")).startsWith(" "));
  await browser.close();
}
console.log(failures ? failures + " FAILED" : "all passed"); process.exit(failures ? 1 : 0);
