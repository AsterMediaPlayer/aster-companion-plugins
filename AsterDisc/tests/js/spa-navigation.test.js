// jellyfin-web moves between pages without loading one: its router (createHashRouter, src/RootAppRouter.tsx) calls history.pushState, which
// never fires 'hashchange'. Until 0.7.3 the renderer only listened for 'hashchange', so a title reached by clicking had no Disc Menu button
// until the page was reloaded. Runs the real renderer script in jsdom; jsdom, like a browser, fires no 'hashchange' for pushState.
const path = require('path'); const fs = require('fs');
const { JSDOM } = require('jsdom');
const ROOT = path.resolve(__dirname, '..', '..');
const src = fs.readFileSync(process.argv[2] || path.join(ROOT, 'Jellyfin.Plugin.AsterDisc/Web/discmenus.js'), 'utf8');
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const A = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const B = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const NONE = 'cccccccccccccccccccccccccccccccc';
const doc = { Root: 'main', Menus: { main: { Title: 'Main', Entries: [{ Action: 'playFeature', Label: 'Play' }] } } };
const ROW = '<div class="mainDetailButtons"><button class="btnPlay"></button><button class="btnReplay"></button></div>';

function boot(hash) {
  const dom = new JSDOM('<body class="libraryDocument"><div class="mainAnimatedPages"></div></body>', { url: 'http://x/web/' + hash, runScripts: 'outside-only', pretendToBeVisual: true });
  const w = dom.window; w.console.warn = () => {}; w.console.log = () => {};
  const asked = [];
  w.ApiClient = {
    getJSON: (u) => { asked.push(u); return /\/(aaaa|bbbb)/.test(u) ? Promise.resolve(doc) : Promise.reject({ status: 404 }); },
    getUrl: u => u, getImageUrl: () => 'x', deviceId: () => 'd',
  };
  w.eval(src);
  return { w, asked };
}

// What jellyfin-web does on a click: the router pushes the new address, then draws the page and dispatches 'viewshow' on it (bubbling).
function showPage(w, id, { viewshow = true } = {}) {
  const pages = w.document.querySelector('.mainAnimatedPages');
  pages.querySelectorAll('.page').forEach(p => p.classList.add('hide'));
  const page = w.document.createElement('div'); page.className = 'page itemDetailPage'; page.innerHTML = ROW;
  pages.appendChild(page);
  if (viewshow) { page.dispatchEvent(new w.CustomEvent('viewshow', { bubbles: true, detail: {} })); }
  return page;
}
const buttonIn = (page) => !!page.querySelector('#discMenusButton');

(async () => {
  // Start on Home, then click a title with a menu.
  let { w, asked } = boot('#/home');
  await sleep(100);
  w.history.pushState({}, '', '#/details?id=' + A);
  let page = showPage(w, A, { viewshow: false });
  await sleep(300);
  check(asked.some(u => u.indexOf(A) >= 0), 'a title reached by history.pushState (no hashchange) is asked about its menu', asked.join());
  check(buttonIn(page), 'and gets its Disc Menu button without a reload');

  // The same through jellyfin-web's own page event alone (a router that changed the address in some other way).
  ({ w, asked } = boot('#/home'));
  await sleep(100);
  w.history.replaceState({}, '', '#/details?id=' + B);
  page = showPage(w, B);
  await sleep(300);
  check(buttonIn(page), 'replaceState plus viewshow also gets the button');

  // Title to title: the old page is still on screen while the new one is drawn, so the button can land in the old row first.
  // When the new page is shown it must move there, and there must only ever be one.
  ({ w, asked } = boot('#/details?id=' + A));
  const first = showPage(w, A, { viewshow: false });
  await sleep(300);
  check(buttonIn(first), 'the first title has the button');
  w.history.pushState({}, '', '#/details?id=' + B);
  await sleep(300); // the old row is the only one there yet
  const second = showPage(w, B);
  await sleep(300);
  check(buttonIn(second) && !buttonIn(first), 'moving to another title with a menu: the button is on the page now shown, not the hidden one');
  check(w.document.querySelectorAll('#discMenusButton').length === 1, 'exactly one button', w.document.querySelectorAll('#discMenusButton').length);

  // To a title without a menu: no button left behind.
  w.history.pushState({}, '', '#/details?id=' + NONE);
  const third = showPage(w, NONE);
  await sleep(300);
  check(w.document.querySelectorAll('#discMenusButton').length === 0, 'a title without a menu has no button, and none is left on the others');

  // Back (popstate) to a title with a menu: a cached page coming back gets its button again.
  w.document.querySelectorAll('.page').forEach(p => p.classList.add('hide'));
  second.classList.remove('hide');
  w.history.back(); await sleep(50);
  second.dispatchEvent(new w.CustomEvent('viewshow', { bubbles: true, detail: {} }));
  await sleep(300);
  check(buttonIn(second), 'going Back to a title with a menu brings its button back', w.location.hash);

  // Moving within the same title (a viewshow with no new address) never adds a second button.
  second.dispatchEvent(new w.CustomEvent('viewshow', { bubbles: true, detail: {} }));
  await sleep(300);
  check(w.document.querySelectorAll('#discMenusButton').length === 1, 'a repeated viewshow for the same title keeps one button');

  console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
})();
