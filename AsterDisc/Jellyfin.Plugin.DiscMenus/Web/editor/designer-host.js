/*
 * What the full-size Menu Designer page (Web/designer.html) needs around the editor's own code. The editor page is written for the Jellyfin dashboard,
 * which supplies a global ApiClient (server address + the signed-in user's token) and styled form controls. The designer is a plain page of the
 * plugin's own, so this stands in for those: it reads the sign-in that jellyfin-web keeps in the browser (localStorage "jellyfin_credentials"),
 * offers the few ApiClient calls the editor uses, labels the controls, and says plainly when there is no usable sign-in.
 *
 *   pickCredentials(raw, origin) -> { token } or null     choose the saved server sign-in for this address
 *   prefixOf(pathname)           -> "/jellyfin" (the base URL the server runs under) or ""
 *   makeClient(opts)             -> the ApiClient stand-in: getUrl(path, query), ajax(request), accessToken()
 *   labelControls(doc)           -> puts a label before each emby-select / emby-input that carries a label attribute
 *   install(win, doc)            -> defines window.ApiClient (or shows the sign-in message); reads ?file=
 *   ready(win, doc)              -> labels the controls and starts the editor (dispatches "pageshow" on its page)
 *
 * Works in a browser (global DiscMenusDesignerHost) and in Node (module.exports).
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusDesignerHost = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    var PAGE_SUFFIX = /\/DiscMenus\/web\/designer\.html.*$/i;

    function originOf(address) {
        try {
            return new URL(address).origin;
        } catch (e) {
            return null;
        }
    }

    function pickCredentials(raw, origin) {
        var data;
        try {
            data = JSON.parse(raw);
        } catch (e) {
            return null;
        }

        var servers = data && Array.isArray(data.Servers) ? data.Servers : [];
        var usable = servers.filter(function (s) { return s && typeof s.AccessToken === 'string' && s.AccessToken.length > 0; });
        if (usable.length === 0) {
            return null;
        }

        var here = usable.filter(function (s) { return originOf(s.ManualAddress) === origin || originOf(s.LocalAddress) === origin || originOf(s.RemoteAddress) === origin; });
        var pool = here.length ? here : usable;
        pool.sort(function (a, b) { return (Number(b.DateLastAccessed) || 0) - (Number(a.DateLastAccessed) || 0); });
        return { token: pool[0].AccessToken, userId: typeof pool[0].UserId === 'string' ? pool[0].UserId : null };
    }

    function prefixOf(pathname) {
        var at = String(pathname || '').search(PAGE_SUFFIX);
        var prefix = at > 0 ? String(pathname).slice(0, at) : '';
        return /^(\/(?!\.{1,2}(\/|$))[A-Za-z0-9._~-]+)*$/.test(prefix) ? prefix : '';
    }

    function makeClient(opts) {
        var origin = opts.origin;
        var prefix = opts.prefix || '';
        var token = opts.token;
        var doFetch = opts.fetch;
        var onUnauthorized = opts.onUnauthorized || function () {};
        return {
            accessToken: function () { return token; },
            getUrl: function (path, query) {
                var pairs = [];
                Object.keys(query || {}).forEach(function (k) {
                    if (query[k] !== undefined && query[k] !== null && query[k] !== '') {
                        pairs.push(encodeURIComponent(k) + '=' + encodeURIComponent(query[k]));
                    }
                });
                return origin + prefix + '/' + String(path).replace(/^\//, '') + (pairs.length ? '?' + pairs.join('&') : '');
            },
            // Like jellyfin-web\x27s: resolves with the parsed body (or text), rejects with the response itself when the server says no.
            ajax: function (req) {
                var headers = { Authorization: 'MediaBrowser Token="' + token + '"' };
                if (req.contentType) {
                    headers['Content-Type'] = req.contentType;
                }

                if (req.dataType === 'json') {
                    headers.Accept = 'application/json';
                }

                return doFetch(req.url, { method: req.type || 'GET', headers: headers, body: req.data, credentials: 'same-origin' }).then(function (res) {
                    if (!res.ok) {
                        if (res.status === 401) {
                            onUnauthorized();
                        }

                        return Promise.reject(res);
                    }

                    if (res.status === 204) {
                        return null;
                    }

                    return res.text().then(function (text) {
                        if (req.dataType !== 'json') {
                            return text;
                        }

                        return text ? JSON.parse(text) : null;
                    });
                });
            }
        };
    }

    function labelControls(doc) {
        var controls = doc.querySelectorAll('[is="emby-select"][label], [is="emby-input"][label], [is="emby-textarea"][label]');
        Array.prototype.forEach.call(controls, function (c) {
            var text = c.getAttribute('label');
            if (!text || (c.previousElementSibling && c.previousElementSibling.classList.contains('dsLabel'))) {
                return;
            }

            var label = doc.createElement('label');
            label.className = 'dsLabel';
            label.textContent = text;
            if (c.id) {
                label.setAttribute('for', c.id);
            }

            c.parentNode.insertBefore(label, c);
        });
    }

    function showGate(win, doc, message) {
        var gate = doc.getElementById('discDesignerGate');
        var text = doc.getElementById('discDesignerGateText');
        var link = doc.getElementById('discDesignerGateLink');
        if (!gate) {
            return;
        }

        text.textContent = message;
        link.setAttribute('href', prefixOf(win.location.pathname) + '/web/');
        gate.hidden = false;
    }

    function install(win, doc) {
        var origin = win.location.origin;
        var creds = null;
        try {
            creds = pickCredentials(win.localStorage.getItem('jellyfin_credentials'), origin);
        } catch (e) {
            creds = null;
        }

        var file = null;
        try {
            file = new URL(win.location.href).searchParams.get('file');
        } catch (e) {
            file = null;
        }

        win.__discMenusPreferredFile = file;
        if (!creds) {
            win.__discDesignerNoSignIn = true;
            return false;
        }

        win.ApiClient = makeClient({
            origin: origin,
            prefix: prefixOf(win.location.pathname),
            token: creds.token,
            fetch: function (u, init) { return win.fetch(u, init); },
            onUnauthorized: function () {
                showGate(win, doc, 'Jellyfin did not accept your sign-in for this page. Sign in as an administrator in Jellyfin, then reload this page.');
            }
        });
        return true;
    }

    function ready(win, doc) {
        labelControls(doc);
        var page = doc.getElementById('DiscMenusEditorPage');
        if (win.__discDesignerNoSignIn) {
            showGate(win, doc, 'The Menu Designer uses your Jellyfin sign-in, and this browser does not have one for this server. Sign in to Jellyfin (as an administrator) in this browser, then reload this page.');
            return;
        }

        if (page) {
            page.dispatchEvent(new win.CustomEvent('pageshow'));
        }
    }

    return { pickCredentials: pickCredentials, prefixOf: prefixOf, makeClient: makeClient, labelControls: labelControls, install: install, ready: ready };
});
