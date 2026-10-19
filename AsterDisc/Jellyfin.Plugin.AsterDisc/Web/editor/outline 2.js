/*
 * The Menu Editor's outline: a flat, indented list of the parts of a menu (document, extras, menus, each menu's buttons and layers), built from
 * the text, with the problems the server reported attached to the part they are about. Also finds the text of any part so the editor can
 * select it.
 *
 *   build(text, errors)         -> rows [{ path, depth, kind, label, errors, hasErrors }], or null when the text is not valid JSON
 *                                  errors: [{ Message, Path }] where Path is a JSON Pointer ("/menus/main/entries/0/label")
 *   pointerToPath(pointer, doc) -> ['menus','main','entries',0,'label'] (numbers where doc has an array)
 *   resolve(text, path)         -> { path, start, end, line, column, exact } for the deepest part of the path that exists in the text, or null
 *   same(a, b)                  -> whether two paths are the same
 *
 * Works in a browser (global DiscMenusOutline, needs DiscMenusJsonText) and in Node (module.exports). Never throws on bad input.
 */
(function (root, factory) {
    var api = factory(typeof module === 'object' && module.exports ? require('./json-text.js') : root.DiscMenusJsonText);
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusOutline = api;
    }
})(typeof self !== 'undefined' ? self : this, function (JsonText) {
    'use strict';

    var ACTIONS = {
        playFeature: 'Play feature', playExtra: 'Play extra', playSequence: 'Play several', submenu: 'Open menu',
        chapters: 'Scenes', back: 'Back', home: 'Home'
    };

    function isObject(v) {
        return v !== null && typeof v === 'object' && !Array.isArray(v);
    }

    function same(a, b) {
        if (a.length !== b.length) {
            return false;
        }

        for (var i = 0; i < a.length; i++) {
            if (a[i] !== b[i]) {
                return false;
            }
        }

        return true;
    }

    function startsWith(path, prefix) {
        return prefix.length <= path.length && same(path.slice(0, prefix.length), prefix);
    }

    function pointerToPath(pointer, doc) {
        if (typeof pointer !== 'string' || (pointer !== '' && pointer.charAt(0) !== '/')) {
            return null;
        }

        var path = [];
        var node = doc;
        var parts = pointer === '' ? [] : pointer.slice(1).split('/');
        for (var i = 0; i < parts.length; i++) {
            var seg = parts[i].replace(/~1/g, '/').replace(/~0/g, '~');
            if (Array.isArray(node) && /^\d+$/.test(seg)) {
                path.push(Number(seg));
                node = node[Number(seg)];
            } else {
                path.push(seg);
                node = isObject(node) && Object.prototype.hasOwnProperty.call(node, seg) ? node[seg] : undefined;
            }
        }

        return path;
    }

    function clip(text, n) {
        text = typeof text === 'string' ? text : '';
        return text.length > n ? text.slice(0, n - 1) + '…' : text;
    }

    function layerRows(rows, layers, base, depth) {
        if (!Array.isArray(layers)) {
            return;
        }

        layers.forEach(function (layer, i) {
            var type = isObject(layer) && typeof layer.type === 'string' ? layer.type : 'layer';
            rows.push({ path: base.concat(['layout', 'layers', i]), depth: depth, kind: 'layer', label: 'Decoration ' + (i + 1) + ': ' + clip(type, 12) });
        });
    }

    function build(text, errors) {
        var doc;
        try {
            doc = JSON.parse(text);
        } catch (e) {
            return null;
        }

        if (!isObject(doc)) {
            return null;
        }

        // Pages first (that is what people come to change), then extras, then the settings that apply to the whole menu.
        var rows = [];
        if (isObject(doc.menus)) {
            var menuKeys = Object.keys(doc.menus);
            rows.push({ path: ['menus'], depth: 0, kind: 'group', label: 'Pages (' + menuKeys.length + ')' });
            menuKeys.forEach(function (k) {
                var m = doc.menus[k];
                var title = isObject(m) && typeof m.title === 'string' ? m.title : '';
                rows.push({ path: ['menus', k], depth: 1, kind: 'menu', label: clip(title || k, 40) + (k === doc.root ? ' (first)' : '') + ' [' + clip(k, 20) + ']' });
                if (!isObject(m)) {
                    return;
                }

                if (Array.isArray(m.entries)) {
                    m.entries.forEach(function (e, i) {
                        var action = isObject(e) && typeof e.action === 'string' ? e.action : '?';
                        var label = isObject(e) && typeof e.label === 'string' ? e.label : '';
                        rows.push({
                            path: ['menus', k, 'entries', i], depth: 2, kind: 'entry',
                            label: clip(label || '(no label)', 40) + ' - ' + (ACTIONS[action] || clip(action, 14))
                        });
                    });
                }

                layerRows(rows, isObject(m.layout) ? m.layout.layers : null, ['menus', k], 2);
            });
        }

        if (isObject(doc.extras)) {
            var extraKeys = Object.keys(doc.extras);
            rows.push({ path: ['extras'], depth: 0, kind: 'group', label: 'Extras (' + extraKeys.length + ')' });
            extraKeys.forEach(function (k) {
                var x = doc.extras[k];
                var detail = isObject(x) ? [x.type, typeof x.durationSec === 'number' ? Math.round(x.durationSec) + 's' : null].filter(Boolean).join(', ') : '';
                rows.push({ path: ['extras', k], depth: 1, kind: 'extra', label: clip(k, 30) + (detail ? ' - ' + detail : '') });
            });
        }

        rows.push({ path: [], depth: 0, kind: 'document', label: 'Settings for the whole menu' });
        layerRows(rows, isObject(doc.layout) ? doc.layout.layers : null, [], 1);

        rows.forEach(function (r) { r.errors = 0; r.hasErrors = false; });
        (errors || []).forEach(function (err) {
            var path = err && typeof err.Path === 'string' ? pointerToPath(err.Path, doc) : null;
            if (!path) {
                return;
            }

            // The deepest row the problem is inside is where it is counted; every row above it is marked as containing a problem.
            var best = null;
            rows.forEach(function (r) {
                if (startsWith(path, r.path) && (best === null || r.path.length >= best.path.length)) {
                    best = r;
                }
            });
            rows.forEach(function (r) {
                if (best && startsWith(best.path, r.path)) {
                    r.hasErrors = true;
                }
            });
            if (best) {
                best.errors++;
            }
        });

        return rows;
    }

    function resolve(text, path) {
        if (!Array.isArray(path)) {
            return null;
        }

        for (var n = path.length; n >= 0; n--) {
            var loc = null;
            try {
                loc = JsonText.locate(text, path.slice(0, n));
            } catch (e) {
                loc = null;
            }

            if (loc) {
                return { path: path.slice(0, n), start: loc.keyStart != null ? loc.keyStart : loc.start, end: loc.end, line: loc.line, column: loc.column, exact: n === path.length };
            }
        }

        return null;
    }

    return { build: build, pointerToPath: pointerToPath, resolve: resolve, same: same, startsWith: startsWith };
});
