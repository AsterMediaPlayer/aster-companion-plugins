// What the full-size designer needs around the editor: reading jellyfin-web's saved sign-in, the ApiClient stand-in, labelling the controls, and saying plainly
// when there is no usable sign-in.
const path = require('path'); const fs = require('fs');
const { JSDOM } = require('jsdom');
const ROOT = path.resolve(__dirname, '..', '..');
const H = require(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/designer-host.js'));
let fail = 0; const check = (ok, m, e) => { if (!ok) fail++; console.log(ok ? 'PASS' : 'FAIL', m, e === undefined ? '' : e); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const ORIGIN = 'https://jf.example:8920';
const creds = servers => JSON.stringify({ Servers: servers });

console.log('--- the saved sign-in');
check(H.pickCredentials(creds([{ AccessToken: 'T1', ManualAddress: ORIGIN }]), ORIGIN).token === 'T1', 'a saved sign-in for this address is used');
check(H.pickCredentials(creds([{ AccessToken: 'OTHER', ManualAddress: 'https://other.example' }, { AccessToken: 'MINE', LocalAddress: ORIGIN + '/' }]), ORIGIN).token === 'MINE', 'the one for THIS address wins over another server\'s (the address is compared as an origin)');
check(H.pickCredentials(creds([{ AccessToken: 'OLD', ManualAddress: ORIGIN, DateLastAccessed: 1 }, { AccessToken: 'NEW', ManualAddress: ORIGIN, DateLastAccessed: 9 }]), ORIGIN).token === 'NEW', 'of several for this address, the most recently used');
check(H.pickCredentials(creds([{ AccessToken: 'ONLY', ManualAddress: 'http://192.168.1.5:8096' }]), ORIGIN).token === 'ONLY', 'with none matching, the only saved sign-in is used (a server reached by another name)');
for (const bad of [null, undefined, '', 'not json', '{}', '[]', creds([]), creds([{ ManualAddress: ORIGIN }]), creds([{ AccessToken: '', ManualAddress: ORIGIN }]), creds([{ AccessToken: 5 }]), creds([null, 'x']), '{"Servers":"no"}']) {
  check(H.pickCredentials(bad, ORIGIN) === null, 'no sign-in from ' + String(bad).slice(0, 40));
}

console.log('--- the base URL');
check(H.prefixOf('/DiscMenus/web/designer.html') === '' && H.prefixOf('/jellyfin/DiscMenus/web/designer.html') === '/jellyfin' && H.prefixOf('/a/b/DiscMenus/web/designer.html') === '/a/b', 'a configured base URL is found from the page address');
check(H.prefixOf('/%3Cscript%3E/DiscMenus/web/designer.html') === '' && H.prefixOf('//evil.example/DiscMenus/web/designer.html') === '' && H.prefixOf('/../x/DiscMenus/web/designer.html') === '' && H.prefixOf('') === '' && H.prefixOf(null) === '', 'an odd path gives no prefix rather than a bad one');

console.log('--- the ApiClient stand-in');
const calls = [];
const fakeFetch = (reply) => (u, init) => { calls.push({ u, init }); return Promise.resolve(reply(u, init)); };
const resp = (status, body) => ({ ok: status >= 200 && status < 300, status, text: () => Promise.resolve(body === undefined ? '' : (typeof body === 'string' ? body : JSON.stringify(body))), json: () => Promise.resolve(body) });
(async () => {
  let unauthorized = 0;
  const c = H.makeClient({ origin: ORIGIN, prefix: '/jellyfin', token: 'TOK', fetch: fakeFetch((u) => /Files/.test(u) ? resp(200, [{ File: 'a' }]) : /Empty/.test(u) ? resp(204) : /Text/.test(u) ? resp(200, 'plain') : /Nope/.test(u) ? resp(403, { Error: 'no' }) : /Auth/.test(u) ? resp(401, '') : resp(200, '')), onUnauthorized: () => unauthorized++ });
  check(c.accessToken() === 'TOK', 'accessToken() is the signed-in token');
  check(c.getUrl('DiscMenus/Editor/Files') === ORIGIN + '/jellyfin/DiscMenus/Editor/Files', 'getUrl puts the server address and base URL in front');
  check(c.getUrl('/DiscMenus/x', { name: 'a b&c.menu.json', empty: '', none: null, gone: undefined, n: 0 }) === ORIGIN + '/jellyfin/DiscMenus/x?name=a%20b%26c.menu.json&n=0', 'query values are encoded and empty ones left out', c.getUrl('/DiscMenus/x', { name: 'a b&c.menu.json', empty: '', none: null, n: 0 }));
  const data = await c.ajax({ type: 'GET', url: c.getUrl('DiscMenus/Editor/Files'), dataType: 'json' });
  check(JSON.stringify(data) === '[{"File":"a"}]', 'ajax resolves with the parsed JSON');
  check(calls[0].init.headers.Authorization === 'MediaBrowser Token="TOK"' && calls[0].init.headers.Accept === 'application/json' && calls[0].init.method === 'GET', 'it sends the token the way Jellyfin 12 reads it (the Authorization header), asks for JSON');
  check(!('X-Emby-Token' in calls[0].init.headers), 'and not the header Jellyfin 12 refuses');
  await c.ajax({ type: 'PUT', url: c.getUrl('DiscMenus/Editor/File', { name: 'a' }), dataType: 'json', data: '{"x":1}', contentType: 'text/plain' });
  const put = calls[calls.length - 1].init;
  check(put.method === 'PUT' && put.body === '{"x":1}' && put.headers['Content-Type'] === 'text/plain', 'a request body and its content type are passed through exactly');
  check((await c.ajax({ type: 'DELETE', url: 'http://x/Empty', dataType: 'json' })) === null, 'an empty answer (204) is null');
  check((await c.ajax({ type: 'POST', url: 'http://x/Other', dataType: 'json' })) === null, 'an empty JSON body is null, not an error');
  check((await c.ajax({ type: 'GET', url: 'http://x/Text' })) === 'plain', 'without dataType json the text is returned');
  let rejected = null; await c.ajax({ type: 'GET', url: 'http://x/Nope', dataType: 'json' }).catch(r => { rejected = r; });
  check(rejected && rejected.status === 403 && (await rejected.json()).Error === 'no', 'a refusal rejects with the response itself, as jellyfin-web\'s does (the editor reads its message)');
  await c.ajax({ type: 'GET', url: 'http://x/Auth', dataType: 'json' }).catch(() => {});
  check(unauthorized === 1, 'a 401 is reported so the page can say the sign-in did not work');
  const bad = H.makeClient({ origin: ORIGIN, prefix: '', token: 't', fetch: fakeFetch(() => ({ ok: true, status: 200, text: () => Promise.resolve('{ not json') })) });
  let parseErr = null; await bad.ajax({ type: 'GET', url: 'http://x/', dataType: 'json' }).catch(e => { parseErr = e; });
  check(parseErr instanceof Error, 'a body that is not JSON rejects instead of returning garbage');

  console.log('--- labelling the controls');
  const dom = new JSDOM('<body><div><select id="a" is="emby-select" label="Menu file"></select></div><div><input id="b" is="emby-input" label="Find a title"/></div><button is="emby-button" label="x"></button><select id="c" is="emby-select"></select></body>');
  H.labelControls(dom.window.document); H.labelControls(dom.window.document);
  const labels = [...dom.window.document.querySelectorAll('label.dsLabel')];
  check(labels.length === 2 && labels[0].textContent === 'Menu file' && labels[0].getAttribute('for') === 'a' && labels[0].nextElementSibling.id === 'a', 'each labelled emby-select and emby-input gets a real label before it (once, even if run twice)');
  const isLabel=e=>!!e&&e.classList.contains('dsLabel');
  check(!isLabel(dom.window.document.querySelector('button').previousElementSibling) && !isLabel(dom.window.document.querySelector('#c').previousElementSibling), 'buttons and controls without a label are left alone');

  console.log('--- install and ready');
  const page = (url, storage) => { const d = new JSDOM('<body><div id="discDesignerGate" hidden><p id="discDesignerGateText"></p><a id="discDesignerGateLink"></a></div><div id="DiscMenusEditorPage"></div></body>', { url, runScripts: 'outside-only' }); const w = d.window; if (storage !== undefined) w.localStorage.setItem('jellyfin_credentials', storage); w.fetch = () => Promise.resolve(resp(200, '[]')); return w; };
  let w = page(ORIGIN + '/jellyfin/DiscMenus/web/designer.html?file=thor%20ragnarok.menu.json', creds([{ AccessToken: 'TT', ManualAddress: ORIGIN }]));
  check(H.install(w, w.document) === true && w.ApiClient && w.ApiClient.accessToken() === 'TT', 'with a saved sign-in install defines ApiClient');
  check(w.ApiClient.getUrl('DiscMenus/web/preview.html') === ORIGIN + '/jellyfin/DiscMenus/web/preview.html', 'with the base URL taken from the page address');
  check(w.__discMenusPreferredFile === 'thor ragnarok.menu.json', 'the file in ?file= is remembered for the editor to open');
  let shown = 0; w.document.getElementById('DiscMenusEditorPage').addEventListener('pageshow', () => shown++);
  H.ready(w, w.document); check(shown === 1 && w.document.getElementById('discDesignerGate').hidden === true, 'ready starts the editor (pageshow) and shows no message');
  w = page(ORIGIN + '/DiscMenus/web/designer.html', creds([]));
  check(H.install(w, w.document) === false && w.ApiClient === undefined && w.__discMenusPreferredFile === null, 'with no sign-in install defines nothing');
  shown = 0; w.document.getElementById('DiscMenusEditorPage').addEventListener('pageshow', () => shown++);
  H.ready(w, w.document);
  check(shown === 0 && w.document.getElementById('discDesignerGate').hidden === false && /Sign in to Jellyfin/.test(w.document.getElementById('discDesignerGateText').textContent) && w.document.getElementById('discDesignerGateLink').getAttribute('href') === '/web/', 'it says so plainly, with a link to Jellyfin, and does not start the editor');
  w = page(ORIGIN + '/jellyfin/DiscMenus/web/designer.html');
  H.install(w, w.document); H.ready(w, w.document);
  check(w.document.getElementById('discDesignerGateLink').getAttribute('href') === '/jellyfin/web/', 'the link follows the base URL');
  w = page(ORIGIN + '/DiscMenus/web/designer.html', creds([{ AccessToken: 'TT', ManualAddress: ORIGIN }]));
  Object.defineProperty(w, 'localStorage', { get() { throw new Error('storage blocked'); } });
  check(H.install(w, w.document) === false, 'blocked browser storage is handled like no sign-in');
  w = page(ORIGIN + '/DiscMenus/web/designer.html', creds([{ AccessToken: 'TT', ManualAddress: ORIGIN }]));
  H.install(w, w.document);
  w.fetch = () => Promise.resolve(resp(401, ''));
  await w.ApiClient.ajax({ type: 'GET', url: w.ApiClient.getUrl('DiscMenus/Editor/Files') }).catch(() => {});
  check(w.document.getElementById('discDesignerGate').hidden === false && /did not accept your sign-in/.test(w.document.getElementById('discDesignerGateText').textContent), 'if the server turns the token down, the page says so (so a non-admin or expired sign-in is explained)');
  console.log('failures: ' + fail); process.exit(fail ? 1 : 0);
})();
