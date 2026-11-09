// Aster for Plex's settings page: sign in with Plex, set the container up, the owner's settings, and each person's
// tracking accounts (through the same /AsterTracking API the apps use).
"use strict";

const SERVICES = [
  { id: "simkl", key: "Simkl", name: "Simkl", group: "Films and Shows", verb: "watch" },
  { id: "anilist", key: "AniList", name: "AniList", group: "Anime", verb: "watch" },
  { id: "myanimelist", key: "MyAnimeList", name: "MyAnimeList", group: "Anime", verb: "watch" },
  { id: "lastfm", key: "LastFm", name: "Last.fm", group: "Music", verb: "listen to" },
  { id: "listenbrainz", key: "ListenBrainz", name: "ListenBrainz", group: "Music", verb: "listen to" },
  { id: "hardcover", key: "Hardcover", name: "Hardcover", group: "Audiobooks", verb: "listen to" },
];
const SERVICE_NAMES = Object.fromEntries(SERVICES.map(s => [s.id, s.name]));

const main = document.getElementById("main");
const state = { me: null, person: null };

// ---- Helpers --------------------------------------------------------------------------------------------------

function el(html) {
  const t = document.createElement("template");
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}

function esc(text) {
  return String(text ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let data = null;
  const text = await response.text();
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }
  // The container answers in Jellyfin's PascalCase, as the plugin does for the apps. The page's own calls are read
  // in camelCase; /AsterTracking's are read as the apps read them.
  if (!path.startsWith("/AsterTracking")) data = camel(data);
  return { status: response.status, ok: response.ok, data };
}

function camel(value) {
  if (Array.isArray(value)) return value.map(camel);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), camel(v)]));
  }
  return value;
}

function problem(result, fallback) {
  return (result.data && (result.data.detail || result.data.title)) || fallback || `The container answered ${result.status}.`;
}

function when(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

function dialog(title, body, extraActions) {
  const d = document.getElementById("dialog");
  document.getElementById("dialog-title").textContent = title;
  const holder = document.getElementById("dialog-body");
  holder.replaceChildren(body);
  const actions = document.getElementById("dialog-actions");
  actions.querySelectorAll("[data-extra]").forEach(b => b.remove());
  for (const button of extraActions || []) {
    button.dataset.extra = "1";
    actions.prepend(button);
  }
  if (!d.open) d.showModal();
  return d;
}

function closeDialog() {
  const d = document.getElementById("dialog");
  if (d.open) d.close();
}

// ---- Start ----------------------------------------------------------------------------------------------------

async function load() {
  const me = await api("GET", "api/me");
  state.me = me.data;
  const line = document.getElementById("server-line");
  line.textContent = state.me.configured
    ? `${state.me.serverName || "Plex"}${state.me.reachable === false ? " · not reachable" : ""} · version ${state.me.version}`
    : `version ${state.me.version}`;

  const who = document.getElementById("who");
  who.hidden = !state.me.signedIn;
  document.getElementById("who-name").textContent = state.me.name || "";

  if (!state.me.signedIn) return showSignIn();
  if (!state.me.configured) return showSetup();
  if (!state.me.onServer) {
    main.replaceChildren(el(`<section class="panel hero"><h2>Not on this server</h2>
      <p>${esc(state.me.name)} doesn't have access to ${esc(state.me.serverName || "this Plex server")}. Ask its owner to share it with you, then sign in again.</p></section>`));
    return;
  }

  state.person = state.person || state.me.userId;
  main.replaceChildren();
  if (state.me.owner) await showOwner();
  await showTracking();
}

document.getElementById("sign-out").addEventListener("click", async () => {
  await api("POST", "api/signout");
  state.person = null;
  load();
});

// ---- Signing in -----------------------------------------------------------------------------------------------

function showSignIn() {
  const panel = el(`<section class="panel hero">
    <h2>Sign in with Plex</h2>
    <p>Aster for Plex sends what each person on this Plex server watches and listens to, in any Plex app, to the
      tracking accounts they link: Simkl, AniList, MyAnimeList, Last.fm, ListenBrainz and Hardcover.</p>
    <button id="sign-in">Sign in with Plex</button>
    <p class="message" id="sign-in-message"></p>
  </section>`);
  main.replaceChildren(panel);
  panel.querySelector("#sign-in").addEventListener("click", () => signIn(panel.querySelector("#sign-in-message")));
  const pending = sessionStorage.getItem("aster-pin");
  if (pending) {
    const { id, code } = JSON.parse(pending);
    waitForSignIn(id, code, panel.querySelector("#sign-in-message"));
  }
}

async function signIn(message) {
  message.className = "message";
  message.textContent = "Opening Plex…";
  const start = await api("POST", "api/signin", { returnUrl: location.href });
  if (!start.ok) {
    message.className = "message bad";
    message.textContent = problem(start, "plex.tv couldn't be reached.");
    return;
  }

  sessionStorage.setItem("aster-pin", JSON.stringify({ id: start.data.id, code: start.data.code }));
  const popup = window.open(start.data.url, "plex-sign-in", "width=520,height=720");
  if (!popup) {
    // A blocked pop-up: go to Plex in this tab, which comes back here afterwards.
    location.href = start.data.url;
    return;
  }
  waitForSignIn(start.data.id, start.data.code, message, popup);
}

async function waitForSignIn(id, code, message, popup) {
  message.className = "message";
  message.textContent = "Waiting for Plex…";
  for (let i = 0; i < 180; i++) {
    const result = await api("POST", `api/signin/${id}?code=${encodeURIComponent(code)}`);
    if (result.status === 200) {
      sessionStorage.removeItem("aster-pin");
      if (popup && !popup.closed) popup.close();
      return load();
    }
    if (result.status === 410) break;
    await sleep(2000);
  }
  sessionStorage.removeItem("aster-pin");
  message.className = "message bad";
  message.textContent = "The sign-in ran out. Try again.";
}

// ---- Setting up -----------------------------------------------------------------------------------------------

async function showSetup() {
  const panel = el(`<section class="panel"><h2>Set up</h2>
    <p class="muted">Pick the Plex server this container works for. It has to be one of your own: as its owner, the
      container reads its sessions and history for everyone, and knows who's who.</p>
    <div id="servers"><p class="muted">Looking for your servers…</p></div></section>`);
  main.replaceChildren(panel);
  const result = await api("GET", "api/servers");
  const holder = panel.querySelector("#servers");
  if (!result.ok) {
    holder.replaceChildren(el(`<p class="message bad">${esc(problem(result))}</p>`));
    return;
  }
  if (result.data.length === 0) {
    holder.replaceChildren(el(`<p class="message bad">Your Plex account doesn't own a server. Sign in as the server's owner.</p>`));
    return;
  }

  holder.replaceChildren();
  for (const server of result.data) {
    const options = server.addresses.map(a => `<option value="${esc(a.url)}">${esc(a.url)}${a.local ? " (home network)" : ""}</option>`).join("");
    const form = el(`<form class="row">
      <div class="what" style="flex:1">
        <strong>${esc(server.name)}</strong>
        <label class="field"><span>Where this container reaches it</span>
          <select name="pick">${options}<option value="">Another address…</option></select></label>
        <label class="field" hidden><span>Address</span><input type="url" name="other" placeholder="http://plex:32400"></label>
        <p class="message"></p>
      </div>
      <div class="do"><button>Use this server</button></div>
    </form>`);
    const pick = form.querySelector("[name=pick]");
    const other = form.querySelector("[name=other]");
    pick.addEventListener("change", () => { other.closest("label").hidden = pick.value !== ""; });
    form.addEventListener("submit", async e => {
      e.preventDefault();
      const message = form.querySelector(".message");
      const button = form.querySelector("button");
      button.disabled = true;
      message.className = "message";
      message.textContent = "Checking…";
      const setup = await api("POST", "api/setup", { machineId: server.machineId, plexUrl: pick.value || other.value });
      button.disabled = false;
      if (!setup.ok) {
        message.className = "message bad";
        message.textContent = problem(setup);
        return;
      }
      load();
    });
    holder.append(form);
  }
}

// ---- The owner's settings -------------------------------------------------------------------------------------

async function showOwner() {
  const result = await api("GET", "api/settings");
  if (!result.ok) return;
  const s = result.data;
  const published = s.publicUrl && s.customConnections.some(c => c.replace(/\/$/, "").toLowerCase() === s.publicUrl.toLowerCase());
  const chips = s.customConnections.length
    ? s.customConnections.map(c => `<span class="chip ${s.publicUrl && c.toLowerCase().startsWith(s.publicUrl.toLowerCase()) ? "mine" : ""}">${esc(c)}</span>`).join("")
    : `<span class="muted">none</span>`;
  const libraries = s.musicLibraries.length
    ? s.musicLibraries.map(l => `<label class="check"><input type="checkbox" name="books" value="${esc(l.title)}" ${l.audiobooks ? "checked" : ""}>
        <span>${esc(l.title)}</span></label>`).join("")
    : `<p class="muted">This server has no music libraries.</p>`;
  const missing = s.services.filter(x => !x.available).map(x => SERVICE_NAMES[x.service] || x.service);

  const panel = el(`<section class="panel">
    <h2>Server</h2>
    ${s.problem ? `<p class="message bad">${esc(s.problem)}</p>` : ""}
    <form id="settings">
      <label class="field"><span>Where this container reaches Plex</span>
        <input type="url" name="plexUrl" value="${esc(s.plexUrl)}" required></label>

      <label class="field"><span>The address the Aster apps reach this container at, from home and away</span>
        <input type="url" name="publicUrl" value="${esc(s.publicUrl)}" placeholder="https://aster.example.com"></label>
      <label class="check"><input type="checkbox" name="publish" ${s.publishToPlex ? "checked" : ""}>
        <span>Add it to Plex's Custom server access URLs, so the apps find this container with nothing to set up.
          ${published ? `<span class="status good">Added.</span>` : ""}</span></label>
      <div class="muted">Plex's Custom server access URLs now:</div>
      <div class="chips">${chips}</div>

      <h3>Audiobooks</h3>
      <p class="muted">Music libraries that hold audiobooks: their progress goes to Hardcover, not to Last.fm and ListenBrainz as songs.</p>
      ${libraries}

      <details ${missing.length ? "open" : ""}>
        <summary>Developer apps${missing.length ? ` (needed for ${esc(missing.join(", "))})` : ""}</summary>
        <p class="muted">Aster's own apps on each service come built in. Fill these in only to use this server's own instead.</p>
        <label class="field"><span>Simkl client id</span><input type="text" name="simklClientId" value="${esc(s.simklClientId)}" autocomplete="off"></label>
        <label class="field"><span>AniList client id</span><input type="text" name="aniListClientId" value="${esc(s.aniListClientId)}" autocomplete="off"></label>
        <label class="field"><span>MyAnimeList client id</span><input type="text" name="myAnimeListClientId" value="${esc(s.myAnimeListClientId)}" autocomplete="off"></label>
        <label class="field"><span>Hardcover client id</span><input type="text" name="hardcoverClientId" value="${esc(s.hardcoverClientId)}" autocomplete="off"></label>
        <label class="field"><span>Last.fm API key</span><input type="text" name="lastFmApiKey" value="${esc(s.lastFmApiKey)}" autocomplete="off"></label>
        <label class="field"><span>Last.fm shared secret</span><input type="password" name="lastFmSharedSecret" placeholder="${s.lastFmSharedSecretSet ? "set (leave blank to keep)" : ""}" autocomplete="new-password"></label>
      </details>

      <div class="actions"><button>Save</button></div>
      <p class="message" id="settings-message"></p>
    </form>

    <h3>People</h3>
    <p class="muted">Everyone this server is shared with. Pick someone to look after their tracking.</p>
    <div id="people"></div>
  </section>`);

  const form = panel.querySelector("#settings");
  form.addEventListener("submit", async e => {
    e.preventDefault();
    const f = new FormData(form);
    const message = panel.querySelector("#settings-message");
    message.className = "message";
    message.textContent = "Saving…";
    const saved = await api("PUT", "api/settings", {
      plexUrl: f.get("plexUrl"),
      publicUrl: f.get("publicUrl"),
      publishToPlex: f.get("publish") === "on",
      audiobookLibraries: f.getAll("books"),
      simklClientId: f.get("simklClientId"),
      aniListClientId: f.get("aniListClientId"),
      myAnimeListClientId: f.get("myAnimeListClientId"),
      hardcoverClientId: f.get("hardcoverClientId"),
      lastFmApiKey: f.get("lastFmApiKey"),
      lastFmSharedSecret: f.get("lastFmSharedSecret"),
    });
    if (!saved.ok) {
      message.className = "message bad";
      message.textContent = problem(saved);
      return;
    }
    load();
  });

  const people = panel.querySelector("#people");
  for (const p of s.people) {
    const row = el(`<div class="row"><div class="what"><strong>${esc(p.name)}</strong><span>${p.owner ? "Owner" : "Shared with"}</span></div>
      <div class="do"><button class="secondary" ${p.id === state.person ? "disabled" : ""}>${p.id === state.person ? "Showing" : "Tracking"}</button></div></div>`);
    row.querySelector("button").addEventListener("click", () => { state.person = p.id; load(); });
    people.append(row);
  }

  main.append(panel);
}

// ---- Tracking -------------------------------------------------------------------------------------------------

function forPerson(path) {
  if (!state.person || state.person === state.me.userId) return path;
  return path + (path.includes("?") ? "&" : "?") + "userId=" + encodeURIComponent(state.person);
}

async function showTracking() {
  const [features, accounts] = await Promise.all([api("GET", "/AsterTracking/Features"), api("GET", forPerson("/AsterTracking/Accounts"))]);
  const someoneElse = state.person && state.person !== state.me.userId;
  const panel = el(`<section class="panel">
    <h2>${someoneElse ? "Their tracking" : "Your tracking"}</h2>
    <p class="muted">${someoneElse ? "Accounts linked for this person." : "Link your accounts once here or in the Aster app's Settings › Tracking."}
      What's played in any Plex app is sent to them.</p>
    <div id="services"></div>
    <h3>Recent sends</h3>
    <div id="log"></div>
  </section>`);
  main.append(panel);

  if (!accounts.ok || !features.ok) {
    panel.querySelector("#services").replaceChildren(el(`<p class="message bad">${esc(problem(accounts.ok ? features : accounts))}</p>`));
    return;
  }

  const offered = new Set((features.data.Services || []).map(s => s.toLowerCase()));
  const holder = panel.querySelector("#services");
  let group = null;
  for (const service of SERVICES) {
    const account = accounts.data[service.key] || {};
    if (!offered.has(service.id) && !account.Linked) continue;
    if (service.group !== group) {
      group = service.group;
      holder.append(el(`<p class="group">${esc(group)}</p>`));
    }
    holder.append(serviceRow(service, account));
  }

  const log = panel.querySelector("#log");
  const entries = accounts.data.Log || [];
  if (entries.length === 0) {
    log.replaceChildren(el(`<p class="muted">Nothing sent yet.</p>`));
  } else {
    const table = el(`<table class="log"><tbody></tbody></table>`);
    for (const e of entries.slice(0, 15)) {
      table.tBodies[0].append(el(`<tr><td><strong>${esc(e.Title)}</strong><br><span class="muted">${esc(SERVICE_NAMES[e.Service] || e.Service)} · ${esc(e.Result)}</span></td><td>${esc(when(e.At))}</td></tr>`));
    }
    log.replaceChildren(table);
  }
  if (accounts.data.Waiting) {
    log.append(el(`<p class="muted">${accounts.data.Waiting} waiting to be tried again.</p>`));
  }
}

function serviceRow(service, account) {
  if (!account.Linked) {
    const row = el(`<div class="row"><div class="what"><strong>${esc(service.name)}</strong><span>Not linked</span></div>
      <div class="do"><button>Link</button></div></div>`);
    row.querySelector("button").addEventListener("click", () => link(service));
    return row;
  }

  const expires = account.Expires ? ` · until ${new Date(account.Expires).toLocaleDateString()}` : "";
  const row = el(`<div class="row">
    <div class="what"><strong>${esc(service.name)}</strong><span>Linked as ${esc(account.Name || "your account")}${esc(expires)}</span></div>
    <div class="do">
      <label class="switch" title="Send what's ${service.verb === "watch" ? "watched" : "listened to"}"><input type="checkbox" ${account.Scrobble ? "checked" : ""}><span></span></label>
      <button class="danger">Unlink</button>
    </div></div>`);
  row.querySelector("input").addEventListener("change", async e => {
    const result = await api("PUT", forPerson(`/AsterTracking/Accounts/${service.id}/Settings`), { Scrobble: e.target.checked });
    if (!result.ok) e.target.checked = !e.target.checked;
  });
  row.querySelector(".danger").addEventListener("click", async () => {
    if (!confirm(`Unlink ${service.name}? Nothing more is sent there until it's linked again.`)) return;
    await api("DELETE", forPerson(`/AsterTracking/Accounts/${service.id}`));
    load();
  });
  return row;
}

async function link(service) {
  const body = el(`<div><p class="muted">Starting…</p></div>`);
  dialog(`Link ${service.name}`, body);
  const start = await api("POST", forPerson(`/AsterTracking/Accounts/${service.id}/Link`));
  if (!start.ok) {
    body.replaceChildren(el(`<p class="message bad">${esc(problem(start))}</p>`));
    return;
  }

  const l = start.data;
  if (l.Paste) {
    const form = el(`<div>
      <p>Sign in at <a href="${esc(l.Url)}" target="_blank" rel="noopener">${esc(new URL(l.Url).host)}</a>, then paste what it gives you here.</p>
      <label class="field"><span>${service.id === "listenbrainz" ? "Your user token" : "The code or token"}</span><input type="text" name="code" autocomplete="off"></label>
      <p class="message"></p></div>`);
    body.replaceChildren(form);
    const submit = el(`<button type="button">Link</button>`);
    dialog(`Link ${service.name}`, form, [submit]);
    submit.addEventListener("click", async () => {
      const message = form.querySelector(".message");
      submit.disabled = true;
      const done = await api("POST", forPerson(`/AsterTracking/Accounts/${service.id}/Link/Complete`), { Code: form.querySelector("input").value });
      submit.disabled = false;
      if (done.status === 200) { closeDialog(); load(); return; }
      message.className = "message bad";
      message.textContent = done.status === 410 ? "That ran out. Close this and link again." : problem(done, "That didn't work. Check what you pasted.");
    });
    return;
  }

  const target = l.CompleteUrl || l.Url;
  body.replaceChildren(el(`<div>
    ${l.Code
      ? `<p>Go to <a href="${esc(target)}" target="_blank" rel="noopener">${esc(l.Url.replace(/^https?:\/\//, ""))}</a> and enter</p><div class="code">${esc(l.Code)}</div>`
      : `<p><a href="${esc(target)}" target="_blank" rel="noopener">Approve Aster on ${esc(service.name)}</a>, then come back here.</p>`}
    <p class="muted" id="link-wait">Waiting for ${esc(service.name)}…</p></div>`));

  const d = document.getElementById("dialog");
  const until = Date.now() + (l.ExpiresIn || 900) * 1000;
  const every = Math.max(3, l.Interval || 5) * 1000;
  while (d.open && Date.now() < until) {
    await sleep(every);
    if (!d.open) return;
    const done = await api("POST", forPerson(`/AsterTracking/Accounts/${service.id}/Link/Complete`), {});
    if (done.status === 200) { closeDialog(); load(); return; }
    if (done.status === 410) break;
    if (done.status !== 202) {
      body.querySelector("#link-wait").textContent = problem(done);
      return;
    }
  }
  if (d.open) body.querySelector("#link-wait").textContent = "The code ran out. Close this and link again.";
}

load();
