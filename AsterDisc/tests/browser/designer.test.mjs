// The full-size Menu Designer in real browsers (Chromium, Firefox, WebKit): the editor page in a window of its own. The server builds it from the editor's
// own page (DesignerPage.cs); this builds it the same way, serves it with a scripted server, and checks the layout fills the window, the sign-in is read
// from jellyfin-web's saved credentials, a missing or refused sign-in is explained, and arranging works in it exactly as in the dashboard.
import fs from "node:fs";
import { createRequire } from "node:module";
import { chromium, firefox, webkit } from "playwright";
const require = createRequire(import.meta.url);
const ROOT = new URL("../../", import.meta.url).pathname;
const ED = ROOT + "Jellyfin.Plugin.DiscMenus/Web/editor/";
const WEB = ROOT + "Jellyfin.Plugin.DiscMenus/Web/";
const editorHtml = fs.readFileSync(ROOT + "Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html", "utf8");
const template = fs.readFileSync(WEB + "designer.html", "utf8");
// the same substitution DesignerPage.Build does
const open = editorHtml.search(/<body>/i), close = editorHtml.toLowerCase().lastIndexOf("</body>");
const designerHtml = template.replace("@@BODY@@", () => editorHtml.slice(open + "<body>".length, close));
const previewHtml = fs.readFileSync(WEB + "preview.html", "utf8").replace("@@VERSION@@", "test");
const rendererJs = fs.readFileSync(WEB + "discmenus.js", "utf8");
const { toRenderable } = require(ROOT + "tools/preview/menu-to-renderable.js");
fs.mkdirSync(new URL("./shots/", import.meta.url).pathname, { recursive: true });

const menu = (title, x) => JSON.stringify({
  schemaVersion: 1, menuId: "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44", revision: 1, meta: { notes: title },
  match: { itemType: "Movie", providerIds: { Tmdb: "603" } }, extras: {}, root: "main",
  menus: { main: { title, background: { source: "color", color: "#101820" }, entries: [
    { action: "playFeature", label: "Play Movie", position: { x, y: 60, anchor: "left" } },
    { action: "back", label: "Other", position: { x: 70, y: 60, anchor: "center" } }] } },
}, null, 2) + "\n";
const FILES = { "a.menu.json": menu("File A", 20), "b.menu.json": menu("File B", 25) };
const TOKEN = "SECRET-TOKEN";
const creds = JSON.stringify({ Servers: [{ Id: "s", Name: "Server", ManualAddress: "http://x.test", AccessToken: TOKEN, UserId: "u", DateLastAccessed: 5 }] });

let failures = 0;
const check = (name, ok, extra) => { if (!ok) failures++; console.log((ok ? "  PASS " : "  FAIL ") + name + (extra ? "  " + extra : "")); };
const near = (a, b, tol = 0.6) => Math.abs(a - b) <= tol;

async function openDesigner(browser, { credentials = creds, query = "?file=b.menu.json", status = 200, width = 1600, height = 900 } = {}) {
  const page = await browser.newPage({ viewport: { width, height } });
  const seen = { auth: [], urls: [] };
  page.on("dialog", (dlg) => dlg.accept()); // "discard unsaved changes?" after the drag
  page.on("pageerror", (e) => { failures++; console.log("  PAGE ERROR " + e.message); });
  if (credentials !== null) await page.addInitScript((c) => { localStorage.setItem("jellyfin_credentials", c); }, credentials);
  await page.route("http://x.test/**", async (route) => {
    const req = route.request(); const u = new URL(req.url()); const p = u.pathname;
    const send = (b, t, st = 200) => route.fulfill({ status: st, contentType: t, body: typeof b === "string" ? b : JSON.stringify(b) });
    if (p === "/DiscMenus/web/designer.html") return send(designerHtml, "text/html");
    if (p === "/DiscMenus/web/preview.html") return send(previewHtml, "text/html");
    if (p === "/DiscMenus/web/discmenus.js") return send(rendererJs, "text/javascript");
    const m = /^\/DiscMenus\/web\/editor\/([a-z-]+)\.js$/.exec(p);
    if (m) return send(fs.readFileSync(ED + m[1] + ".js", "utf8"), "text/javascript");
    if (p.startsWith("/DiscMenus/Editor/")) {
      const auth = req.headers()["authorization"]; seen.auth.push(auth); seen.urls.push(p + u.search);
      if (status !== 200) return send({ Error: "no" }, "application/json", status);
      if (auth !== `MediaBrowser Token="${TOKEN}"`) return send("", "text/plain", 401);
      if (p === "/DiscMenus/Editor/Files") return send(Object.keys(FILES).map((f) => ({ File: f, Version: "v1", State: "bound" })), "application/json");
      if (p === "/DiscMenus/Editor/File") return send({ File: u.searchParams.get("name"), Json: FILES[u.searchParams.get("name")], Version: "v1" }, "application/json");
      if (p === "/DiscMenus/Editor/Preview") { let d; try { d = JSON.parse(req.postData()); } catch (e) { return send({ Errors: [{ Message: "bad" }] }, "application/json", 422); } return send({ Document: toRenderable(d), ParentItemId: null, Bound: false }, "application/json"); }
    }
    return route.fulfill({ status: 404, body: "" });
  });
  await page.goto("http://x.test/DiscMenus/web/designer.html" + query);
  return { page, seen };
}

for (const [name, type] of [["chromium", chromium], ["firefox", firefox], ["webkit", webkit]]) {
  console.log(name);
  const browser = await type.launch();

  // ---- signed in: the page fills the window
  let { page, seen } = await openDesigner(browser);
  let frame; for (let i = 0; i < 80 && !(frame = page.frames().find((f) => f.url().includes("preview.html"))); i++) await page.waitForTimeout(100);
  await frame.waitForSelector('[data-edit="entry"]', { timeout: 15000 });
  check("it opens the file named in ?file=", (await page.inputValue("#discEdFile")) === "b.menu.json" && /File B/.test(await page.inputValue("#discEdText")));
  check("every call carries the saved sign-in as Jellyfin 12 wants it", seen.auth.length > 0 && seen.auth.every((a) => a === `MediaBrowser Token="${TOKEN}"`));
  check("the selects have real labels (no dashboard to draw them)", (await page.textContent("label.dsLabel")).length > 3);
  const m = await page.evaluate(() => ({ sh: document.documentElement.scrollHeight, ih: innerHeight, sw: document.documentElement.scrollWidth, iw: innerWidth,
    outline: document.querySelector("#discEdOutline").getBoundingClientRect().toJSON(), form: document.querySelector("#discEdInspector").getBoundingClientRect().toJSON(),
    stage: document.querySelector("#discEdStage").getBoundingClientRect().toJSON(), openFull: getComputedStyle(document.querySelector("#discEdOpenFull")).display }));
  check("the page itself does not scroll (the window is the workspace)", m.sh <= m.ih + 1 && m.sw <= m.iw + 1, JSON.stringify({ sh: m.sh, ih: m.ih, sw: m.sw, iw: m.iw }));
  check("the outline and the form are a sidebar on the left, the preview takes the rest", m.outline.right <= m.form.left + 1 && m.form.right <= m.stage.left + 1, JSON.stringify([m.outline.x, m.form.x, m.stage.x].map(Math.round)));
  check("the preview is much larger than in the dashboard (over 800px wide at this window size)", m.stage.width > 800, "width " + Math.round(m.stage.width));
  check("the preview fits inside the window (16:9, nothing cut off)", m.stage.bottom <= m.ih + 1 && m.stage.right <= m.iw + 1 && near(m.stage.width / m.stage.height, 16 / 9, 0.02));
  check("the inspector uses the window's height", m.form.height > m.ih * 0.6, "height " + Math.round(m.form.height));
  check("\"Open full size\" is not offered inside the designer", m.openFull === "none");
  await page.screenshot({ path: new URL("./shots/" + name + "-designer-1.png", import.meta.url).pathname });

  // ---- arranging works here exactly as in the dashboard
  await page.click("#discEdArrange"); await page.waitForTimeout(300);
  const box = await page.locator("#discEdFrame").boundingBox();
  const r = await frame.evaluate(() => { const e = document.querySelector('[data-edit="entry"][data-index="0"]').getBoundingClientRect(); return { x: e.x + e.width / 2, y: e.y + e.height / 2 }; });
  const s = box.width / 1920;
  await page.mouse.move(box.x + r.x * s, box.y + r.y * s); await page.mouse.down();
  await page.mouse.move(box.x + r.x * s + 0.1 * box.width, box.y + r.y * s, { steps: 10 }); await page.mouse.up(); await page.waitForTimeout(500);
  const pos = JSON.parse(await page.inputValue("#discEdText")).menus.main.entries[0].position;
  check("dragging a button 10% of the (large) preview moves it 10% (x 25 to 35)", near(pos.x, 35) && near(pos.y, 60), JSON.stringify(pos));
  await page.screenshot({ path: new URL("./shots/" + name + "-designer-2.png", import.meta.url).pathname });

  // ---- the text is a drawer on the right, panels float over the page
  await page.click("#discEdToggleText"); await page.waitForTimeout(200);
  const d = await page.evaluate(() => ({ t: document.querySelector("#discEdTextPane").getBoundingClientRect().toJSON(), pos: getComputedStyle(document.querySelector("#discEdTextPane")).position, side: document.querySelector("#discEdOutline").getBoundingClientRect().toJSON(), stage: document.querySelector("#discEdStage").getBoundingClientRect().toJSON() }));
  check("Show text opens the text as a drawer on the right (the sidebar and preview stay where they were)", d.pos === "fixed" && d.t.left > d.side.right && near(d.stage.width, m.stage.width, 2), JSON.stringify({ pos: d.pos, left: Math.round(d.t.left) }));
  await page.click("#discEdToggleText");
  await page.click("#discEdNew"); await page.waitForTimeout(200);
  const pnl = await page.evaluate(() => ({ pos: getComputedStyle(document.querySelector("#discEdNewPanel")).position, hidden: document.querySelector("#discEdNewPanel").hidden, stage: document.querySelector("#discEdStage").getBoundingClientRect().toJSON() }));
  check("a panel (New...) floats over the page without moving the layout", pnl.pos === "fixed" && !pnl.hidden && near(pnl.stage.width, m.stage.width, 2));
  await page.click("#discEdNewClose");
  await page.close();

  // ---- the file named in ?file= is only a request
  ({ page } = await openDesigner(browser, { query: "?file=missing.menu.json" }));
  await page.waitForFunction(() => document.querySelector("#discEdFile") && document.querySelector("#discEdFile").value, null, { timeout: 15000 });
  check("a ?file= that does not exist falls back to the first file", (await page.inputValue("#discEdFile")) === "a.menu.json");
  await page.close();
  ({ page } = await openDesigner(browser, { query: "" }));
  await page.waitForFunction(() => document.querySelector("#discEdFile") && document.querySelector("#discEdFile").value, null, { timeout: 15000 });
  check("without ?file= it opens the first file", (await page.inputValue("#discEdFile")) === "a.menu.json");
  await page.close();

  // ---- no sign-in, or one the server refuses
  ({ page } = await openDesigner(browser, { credentials: null }));
  await page.waitForTimeout(600);
  const gate = await page.evaluate(() => ({ visible: !document.querySelector("#discDesignerGate").hidden, text: document.querySelector("#discDesignerGateText").textContent, href: document.querySelector("#discDesignerGateLink").getAttribute("href") }));
  check("with no saved sign-in the page says so and links to Jellyfin", gate.visible && /Sign in to Jellyfin/.test(gate.text) && gate.href === "/web/", gate.text.slice(0, 60));
  check("and the editor never starts (nothing was asked of the server)", (await page.evaluate(() => !window.ApiClient)));
  await page.screenshot({ path: new URL("./shots/" + name + "-designer-gate.png", import.meta.url).pathname });
  await page.close();
  ({ page } = await openDesigner(browser, { credentials: JSON.stringify({ Servers: [{ ManualAddress: "http://x.test", AccessToken: "WRONG" }] }) }));
  await page.waitForTimeout(1500);
  check("a sign-in the server refuses (not an administrator, or expired) is explained", await page.evaluate(() => !document.querySelector("#discDesignerGate").hidden && /did not accept your sign-in/.test(document.querySelector("#discDesignerGateText").textContent)));
  await page.close();
  await browser.close();
}
console.log(failures ? failures + " FAILED" : "all passed"); process.exit(failures ? 1 : 0);
