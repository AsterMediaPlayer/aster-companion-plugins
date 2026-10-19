// Arranging on the preview in real browsers (Chromium, Firefox, WebKit): the real editor page with the real preview iframe, which the editor scales to
// fit, driven with a real mouse and keyboard. jsdom can't check the part that matters most here: pointer positions inside a scaled iframe turning into the
// right percentages, and the preview not stealing the keyboard from the editor's own fields.
import fs from "node:fs";
import { createRequire } from "node:module";
import { chromium, firefox, webkit } from "playwright";
const ROOT = new URL("../../", import.meta.url).pathname;
const ED = ROOT + "Jellyfin.Plugin.DiscMenus/Web/editor/";
const MODULES = ["json-text", "text-adapter", "outline", "schema-hints", "inspector", "pictures"];
const html = fs.readFileSync(ROOT + "Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html", "utf8");
const body = /<body>([\s\S]*)<\/body>/.exec(html)[1].replace(/<script type="text\/javascript">[\s\S]*?<\/script>/, "");
const editorScript = /<script type="text\/javascript">([\s\S]*?)<\/script>/.exec(html)[1];
const previewHtml = fs.readFileSync(ROOT + "Jellyfin.Plugin.DiscMenus/Web/preview.html", "utf8").replace("@@VERSION@@", "test");
const rendererJs = fs.readFileSync(ROOT + "Jellyfin.Plugin.DiscMenus/Web/discmenus.js", "utf8");
const converterJs = fs.readFileSync(ROOT + "tools/preview/menu-to-renderable.js", "utf8");
fs.mkdirSync(new URL("./shots/", import.meta.url).pathname, { recursive: true });

const MENU = JSON.stringify({
  schemaVersion: 1, menuId: "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44", revision: 1,
  match: { itemType: "Movie", providerIds: { Tmdb: "603" } }, extras: {}, root: "main",
  layout: { titlePosition: { x: 50, y: 8, anchor: "top" }, layers: [{ type: "panel", fill: "#223355", opacity: 0.8, position: { x: 0, y: 78, w: 100, h: 22 } }] },
  menus: {
    main: { title: "Main", background: { source: "color", color: "#101820" }, entries: [
      { action: "playFeature", label: "Play Movie", position: { x: 20, y: 60, anchor: "left" } },
      { action: "submenu", label: "More", menu: "plain", position: { x: 70, y: 60, w: 20, h: 10, anchor: "center" } }] },
    plain: { title: "Plain", entries: [{ action: "playFeature", label: "A" }, { action: "back", label: "Back" }] },
    grid: { title: "Grid", layout: { flow: { region: { x: 50, y: 50, w: 80, h: 30, anchor: "center" }, columns: 3, rows: 1 } }, entries: [{ action: "playFeature", label: "G1" }, { action: "playFeature", label: "G2" }, { action: "back", label: "Back" }] },
    pages: { title: "Pages", layout: { flow: { region: { x: 50, y: 50, w: 80, h: 30, anchor: "center" }, columns: 2, rows: 1 } }, entries: [{ action: "playFeature", label: "P1" }, { action: "playFeature", label: "P2" }, { action: "playFeature", label: "P3" }, { action: "playFeature", label: "P4" }] },
  },
}, null, 2) + "\n";

const page0 = `<!doctype html><meta charset="utf-8"><style>button[is="emby-button"] { display: inline-flex; }</style>
<body style="background:#101010;color:#eee;font-family:sans-serif">${body}
<script src="/m/menu-to-renderable.js"></script>
${MODULES.map((n) => `<script src="/DiscMenus/web/editor/${n}.js"></script>`).join("\n")}
<script>
window.__discMenusEditorModulesPreloaded = true;
window.ApiClient = {
  getUrl: (p, q) => "http://x.test/" + p,
  ajax: (req) => {
    const p = new URL(req.url).pathname.slice(1);
    const ok = (b) => Promise.resolve(b);
    if (p === "DiscMenus/Editor/Files") return ok([{ File: "m.menu.json", Version: "v1", State: "bound" }]);
    if (p === "DiscMenus/Editor/File") return ok({ File: "m.menu.json", Json: window.__MENU, Version: "v1" });
    if (p === "DiscMenus/Editor/Preview") {
      let doc; try { doc = JSON.parse(req.data); } catch (e) { return Promise.reject({ status: 422, json: () => Promise.resolve({ Errors: [{ Message: "bad json" }] }) }); }
      return ok({ Document: MenuToRenderable.toRenderable(doc), ParentItemId: null, Bound: false });
    }
    return Promise.reject({ status: 500, json: () => Promise.resolve(null) });
  }
};
</script>
<script>${editorScript}</script></body>`;

let failures = 0;
const check = (name, ok, extra) => { if (!ok) failures++; console.log((ok ? "  PASS " : "  FAIL ") + name + (extra ? "  " + extra : "")); };
const near = (a, b, tol = 0.01) => Math.abs(a - b) <= tol;

for (const [name, type] of [["chromium", chromium], ["firefox", firefox], ["webkit", webkit]]) {
  console.log(name);
  const browser = await type.launch();
  const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
  page.on("pageerror", (e) => { failures++; console.log("  PAGE ERROR " + e.message); });
  await page.route("http://x.test/**", async (route) => {
    const u = new URL(route.request().url()).pathname;
    const send = (b, t) => route.fulfill({ status: 200, contentType: t, body: b });
    if (u === "/editor.html") return send(page0.replace("window.__discMenusEditorModulesPreloaded", "window.__MENU = " + JSON.stringify(MENU) + "; window.__discMenusEditorModulesPreloaded"), "text/html");
    if (u === "/DiscMenus/web/preview.html") return send(previewHtml, "text/html");
    if (u === "/DiscMenus/web/discmenus.js") return send(rendererJs, "text/javascript");
    if (u === "/m/menu-to-renderable.js") return send(converterJs, "text/javascript");
    const m = /^\/DiscMenus\/web\/editor\/([a-z-]+)\.js$/.exec(u);
    if (m) return send(fs.readFileSync(ED + m[1] + ".js", "utf8"), "text/javascript");
    return route.fulfill({ status: 404, body: "" });
  });
  await page.goto("http://x.test/editor.html");
  await page.evaluate(() => document.querySelector("#DiscMenusEditorPage").dispatchEvent(new CustomEvent("pageshow")));
  await page.waitForSelector("#discEdFrame");
  let frame;
  for (let i = 0; i < 60 && !(frame = page.frames().find((f) => f.url().includes("preview.html"))); i++) await page.waitForTimeout(100);
  await frame.waitForSelector('[data-edit="entry"]', { timeout: 15000 });
  await page.click("#discEdArrange");
  await page.waitForTimeout(200);

  const text = async () => JSON.parse(await page.inputValue("#discEdText"));
  const rect = async (sel) => frame.evaluate((q) => { const r = document.querySelector(q).getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; }, sel);
  const box = async () => page.locator("#discEdFrame").boundingBox();
  const at = async (fx, fy) => { const b = await box(); const s = b.width / 1920; return { x: b.x + fx * s, y: b.y + fy * s, s, b }; };
  const dragBy = async (sel, dxPct, dyPct, mods) => {
    const r = await rect(sel); const c = await at(r.x + r.w / 2, r.y + r.h / 2);
    await page.mouse.move(c.x, c.y); await page.mouse.down();
    await page.mouse.move(c.x + dxPct / 100 * c.b.width, c.y + dyPct / 100 * c.b.height, { steps: 10 });
    await page.mouse.up();
    await page.waitForTimeout(300);
  };
  const stageScale = (await box()).width / 1920;
  check("the preview iframe is really scaled down in the editor", stageScale > 0.2 && stageScale < 0.9, "scale " + stageScale.toFixed(3));

  // dragging a button across a scaled iframe moves it by the right percentage
  await dragBy('[data-edit="entry"][data-index="0"]', 10, 0);
  let t = await text(); let p = t.menus.main.entries[0].position;
  check("dragging a button 10% of the preview's width moves it 10% (x 20 to 30)", near(p.x, 30, 0.5) && near(p.y, 60, 0.5), JSON.stringify(p));
  check("its anchor and the rest of the file are untouched", p.anchor === "left" && t.menus.main.entries[1].position.x === 70 && t.layout.layers[0].position.y === 78);
  check("the preview shows the new place", (await frame.evaluate(() => document.querySelector('[data-edit="entry"][data-index="0"]').style.left)) === p.x + "%");
  check("and the outline and form follow the selection", /Button: Play Movie/.test(await page.textContent("#discEdInspector")));
  await page.screenshot({ path: new URL("./shots/" + name + "-arrange-1.png", import.meta.url).pathname });

  // one undo takes the whole drag back
  await page.click("#discEdUndo"); await page.waitForTimeout(300);
  t = await text();
  check("one Undo takes the drag back", t.menus.main.entries[0].position.x === 20);
  await page.click("#discEdRedo"); await page.waitForTimeout(300);
  check("and Redo puts it back", near((await text()).menus.main.entries[0].position.x, 30, 0.5));

  // vertical, and a sized, centre-anchored button
  await dragBy('[data-edit="entry"][data-index="1"]', 0, -10);
  p = (await text()).menus.main.entries[1].position;
  check("a sized, centre-anchored button moves by its anchor point and keeps its size", near(p.y, 50 - 0 + 0, 100) && near(p.y, 50, 1.0) && p.w === 20 && p.h === 10 && near(p.x, 70, 0.5), JSON.stringify(p));

  // the keyboard (the preview gets keys once it has been clicked)
  const er = await rect('[data-edit="entry"][data-index="0"]'); const ec = await at(er.x + er.w / 2, er.y + er.h / 2);
  await page.mouse.click(ec.x, ec.y); await page.waitForTimeout(200);
  const x0 = (await text()).menus.main.entries[0].position.x;
  const focusInfo = await frame.evaluate(() => ({ focus: document.hasFocus(), active: document.activeElement && document.activeElement.tagName, sel: !!document.querySelector(".discMenusSelBox") && document.querySelector(".discMenusSelBox").style.display }));
  await page.keyboard.press("ArrowRight"); await page.waitForTimeout(300);
  const x1 = (await text()).menus.main.entries[0].position.x;
  check("an arrow key nudges the selection by half a percent", near(x1, x0 + 0.5), JSON.stringify({ x0, x1, focusInfo }));
  await page.keyboard.press("Shift+ArrowDown"); await page.waitForTimeout(300);
  check("Shift+arrow nudges by five", near((await text()).menus.main.entries[0].position.y, 65, 1.0));

  // a decoration, from the whole menu's layout
  const lr = await rect('[data-edit="layer"]'); const lc = await at(lr.x + lr.w / 2, lr.y + lr.h * 0.1);
  await page.mouse.move(lc.x, lc.y); await page.mouse.down(); await page.mouse.move(lc.x, lc.y - 0.05 * lc.b.height, { steps: 6 }); await page.mouse.up(); await page.waitForTimeout(300);
  t = await text();
  check("a decorative layer drags too, in the layout it belongs to", near(t.layout.layers[0].position.y, 73, 0.6) && t.layout.layers[0].position.h === 22, JSON.stringify(t.layout.layers[0].position));

  // resizing with the handle
  await page.waitForTimeout(200);
  const handle = await frame.evaluate(() => { const h = document.querySelector('[data-handle="s"]'); const r = h.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2, shown: h.style.display }; });
  if (handle.shown === "block") {
    const hc = await at(handle.x, handle.y);
    await page.mouse.move(hc.x, hc.y); await page.mouse.down(); await page.mouse.move(hc.x, hc.y - 0.05 * hc.b.height, { steps: 6 }); await page.mouse.up(); await page.waitForTimeout(300);
    const h2 = (await text()).layout.layers[0].position.h;
    check("dragging the bottom handle up makes the layer shorter", h2 < 22 && h2 >= 15, "h=" + h2 + " before=" + JSON.stringify(t.layout.layers[0].position) + " handle=" + JSON.stringify(handle));
  } else {
    check("the layer shows a resize handle", false, "no handle shown");
  }

  // the title: it takes its place from the whole menu's layout, so the page gets its own copy
  await dragBy('[data-edit="title"]', 0, 10);
  t = await text();
  check("moving the title gives that page its own title position and leaves the whole menu's alone", t.menus.main.layout && near(t.menus.main.layout.titlePosition.y, 18, 1) && t.layout.titlePosition.y === 8 && t.menus.main.layout.titlePosition.anchor === "top", JSON.stringify(t.menus.main.layout && t.menus.main.layout.titlePosition));

  // clicking a button while arranging does not press it
  const before = await page.inputValue("#discEdMenu");
  const br = await rect('[data-edit="entry"][data-index="1"]'); const bc = await at(br.x + br.w / 2, br.y + br.h / 2);
  await page.mouse.click(bc.x, bc.y); await page.waitForTimeout(300);
  check("clicking a button while arranging selects it and does not open its page", (await page.inputValue("#discEdMenu")) === before);

  // a page that arranges itself
  await page.selectOption("#discEdMenu", "plain"); await page.waitForTimeout(400);
  check("a page that lays its buttons out itself offers to place them freely", await page.isVisible("#discEdFreePlace"));
  await page.click("#discEdFreePlace"); await page.waitForTimeout(500);
  t = await text();
  check("which gives every button the place it has", t.menus.plain.entries.every((e) => e.position && e.position.x >= 0 && e.position.y >= 0 && e.position.y <= 100), JSON.stringify(t.menus.plain.entries.map((e) => e.position)));
  await page.screenshot({ path: new URL("./shots/" + name + "-arrange-2.png", import.meta.url).pathname });
  await frame.waitForSelector('[data-edit="entry"]');
  const pr = await frame.evaluate(() => [...document.querySelectorAll('[data-edit="entry"]')].map((el) => { const r = el.getBoundingClientRect(); return [Math.round(r.x), Math.round(r.y)]; }));
  const expected = t.menus.plain.entries.map((e) => [Math.round(e.position.x / 100 * 1920), Math.round(e.position.y / 100 * 1080)]);
  check("and they stay where they were drawn (within a pixel or two of the half-percent grid)", pr.every((p2, i) => Math.abs(p2[0] - expected[i][0]) <= 12 && Math.abs(p2[1] - expected[i][1]) <= 8), JSON.stringify([pr, expected]));

  // a grid page (as in a menu whose Special Features page lays its buttons out in a row)
  await page.selectOption("#discEdMenu", "grid"); await page.waitForTimeout(500);
  const gridNote = await page.textContent("#discEdArrangeText");
  check("a grid page says it is an automatic grid and offers to place its buttons freely", /automatic grid/.test(gridNote) && await page.isVisible("#discEdFreePlace"), gridNote.slice(0, 70));
  const gridBefore = await frame.evaluate(() => [...document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map((e) => { const r = e.getBoundingClientRect(); return [e.getAttribute("aria-label"), Math.round(r.x + r.width / 2), Math.round(r.y + r.height / 2)]; }));
  check("its buttons are marked by their place in the file", gridBefore.length === 3 && (await frame.evaluate(() => [...document.querySelectorAll('[data-edit="entry"]')].map((e) => e.dataset.index).sort().join())) === "0,1,2");
  await page.click("#discEdFreePlace"); await page.waitForTimeout(700);
  t = await text();
  check("placing freely centres each button where its cell had it and removes the grid", (!t.menus.grid.layout || !t.menus.grid.layout.flow) && t.menus.grid.entries.every((e) => e.position && e.position.anchor === "center" && e.position.w > 3), JSON.stringify(t.menus.grid.entries.map((e) => e.position)));
  const gridAfter = await frame.evaluate(() => [...document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map((e) => { const r = e.getBoundingClientRect(); return [e.getAttribute("aria-label"), Math.round(r.x + r.width / 2), Math.round(r.y + r.height / 2)]; }));
  check("and they stay (nearly) where they were", gridAfter.every((g, i) => Math.abs(g[1] - gridBefore[i][1]) <= 14 && Math.abs(g[2] - gridBefore[i][2]) <= 10), JSON.stringify([gridBefore, gridAfter]));
  const overlap = await frame.evaluate(() => { const r = [...document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map((e) => e.getBoundingClientRect()); for (let i = 0; i < r.length; i++) for (let j = i + 1; j < r.length; j++) if (r[i].left < r[j].right - 1 && r[j].left < r[i].right - 1 && r[i].top < r[j].bottom - 1 && r[j].top < r[i].bottom - 1) return true; return false; });
  check("the freed buttons do not overlap each other", overlap === false);
  await dragBy('[data-edit="entry"][data-index="1"]', 0, 20);
  check("after that a button drags like any other", near((await text()).menus.grid.entries[1].position.y - t.menus.grid.entries[1].position.y, 20, 1.0));
  await page.selectOption("#discEdMenu", "pages"); await page.waitForTimeout(500);
  const pagesNote = await page.textContent("#discEdArrangeText");
  check("a grid that pages says so and does not offer to place freely (even with jellyfin-web's button styling)", /pages/.test(pagesNote) && !(await page.isVisible("#discEdFreePlace")), pagesNote.slice(0, 80));
  await page.selectOption("#discEdMenu", "main"); await page.waitForTimeout(400);
  check("a page whose buttons are positioned does not show the offer either", !(await page.isVisible("#discEdFreePlace")));
  await page.selectOption("#discEdMenu", "pages"); await page.waitForTimeout(400);
  const idx = () => frame.evaluate(() => [...document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map((e) => e.dataset.index).join());
  const firstPage = await idx();
  await frame.evaluate(() => [...document.querySelectorAll(".discMenuEntry")].find((b) => b.getAttribute("aria-label") === "More").click());
  await page.waitForTimeout(400);
  const secondPage = await idx();
  check("paging still works while arranging", firstPage !== secondPage && secondPage.length > 0, firstPage + " -> " + secondPage);

  // the explanation names this page's numbers, and the buttons do the next step
  await page.selectOption("#discEdMenu", "pages"); await page.waitForTimeout(500);
  const say2 = await page.textContent("#discEdArrangeText");
  check("the note gives this page's real numbers (2 across by 1 down, 4 buttons)", /2 across by 1 down \(2 spots\)/.test(say2) && /4 buttons/.test(say2), say2.slice(0, 120));
  await page.click("#discEdGridSettings"); await page.waitForTimeout(400);
  check("\"Show this page's grid settings\" opens the grid section with its columns and rows visible", await page.isVisible("#discEdInspector [data-key='menus/pages/layout/flow/columns']") && await page.isVisible("#discEdInspector [data-key='menus/pages/layout/flow/rows']"));
  await page.click("#discEdFitFree"); await page.waitForTimeout(1500);
  t = await text();
  check("\"Make it big enough and place freely\" enlarges the grid, then places every button, in one click", !(t.menus.pages.layout && t.menus.pages.layout.flow) && t.menus.pages.entries.every((e) => e.position && e.position.anchor === "center"), JSON.stringify(t.menus.pages.entries.map((e) => e.position)));
  check("and the buttons are then draggable", await frame.evaluate(() => document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]').length) === 4);

  // the preview does not steal the keyboard from the editor's own fields
  await page.click("#discEdArrange"); await page.waitForTimeout(200);
  await page.selectOption("#discEdMenu", "main"); await page.waitForTimeout(300);
  await page.locator("#discEdOutline button", { hasText: "Play Movie - Play feature" }).first().click();
  const labelKey = "#discEdInspector [data-key='menus/main/entries/0/label']";
  await page.waitForSelector(labelKey);
  await page.click(labelKey);
  await page.keyboard.type(" X");
  await page.keyboard.press("Enter"); // commits the change and the preview redraws
  await page.waitForTimeout(800);
  const active = await page.evaluate(() => document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.key : null);
  check("after a form edit the preview redraws without taking the keyboard from the field", active === "menus/main/entries/0/label", String(active));
  await page.keyboard.type("Y");
  check("so typing carries on in the same field", (await page.inputValue(labelKey)).endsWith("Y"));
  await browser.close();
}
console.log(failures ? failures + " FAILED" : "all passed"); process.exit(failures ? 1 : 0);
