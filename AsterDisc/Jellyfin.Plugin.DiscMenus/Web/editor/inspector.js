/*
 * The Menu Editor's forms. Shows the part of the menu that is selected (the whole menu, a page, a button, an extra, a decorative layer) as a
 * form built from the schema hints, and changes the menu TEXT in place: every control is one path-addressed edit (json-text.js), so the
 * file keeps the author's formatting and each change is one undo step. The text stays the only copy of the menu; the form is redrawn from it.
 *
 *   create({ container, hints, JsonText, getText, commit, select, actions, confirm })  -> inspector
 *     container   element the forms are drawn in
 *     hints       DiscMenusSchemaHints (what each part of a menu has in it)
 *     getText()   the menu text now
 *     commit(fn)  applies fn(text) -> newText as one edit; returns true if it was applied (the page uses its undo-preserving text adapter)
 *     select(path)            called when a list entry is chosen (the page selects it in the outline)
 *     actions.fanart(path)    optional: open the fanart.tv picker for the background at path
 *     actions.pick({type, current, write})  optional: open the picture / sound / TMDB picker; write(value) puts the choice in the field
 *     confirm(message)        optional (window.confirm by default)
 *   inspector.show(path)      draw the form for the part at path (an array like ['menus','main','entries',0])
 *   inspector.refresh()       redraw from the current text, keeping the focused control
 *   inspector.path()          the path being shown
 *   inspector.reveal(keys)    open collapsible sections so a setting is visible (for "show me the grid settings")
 *
 *   kindAt(path, doc)         { kind, variant } for a path, or null (also used by tests)
 *
 * Works in a browser (global DiscMenusInspector) and in Node with a DOM (module.exports). Never writes text that wouldn't parse: json-text
 * verifies every result, and a control that is out of range shows its reason instead of writing.
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusInspector = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    var KEY = /^[a-z0-9][a-z0-9_-]{0,63}$/;
    var ACTION_NAMES = {
        playFeature: 'Play the feature', playExtra: 'Play an extra', playSequence: 'Play several extras', submenu: 'Open another page',
        chapters: 'Scene selection', home: 'Go to the first page', back: 'Go back'
    };
    var EXTRA_TYPES = ['Clip', 'Trailer', 'BehindTheScenes', 'DeletedScene', 'Interview', 'Scene', 'Sample', 'ThemeSong', 'ThemeVideo', 'Featurette', 'Short', 'Unknown'];

    function isObject(v) {
        return v !== null && typeof v === 'object' && !Array.isArray(v);
    }

    function at(doc, path) {
        var node = doc;
        for (var i = 0; i < path.length; i++) {
            if (node === null || typeof node !== 'object' || !Object.prototype.hasOwnProperty.call(node, path[i])) {
                return undefined;
            }

            node = node[path[i]];
        }

        return node;
    }

    // Which kind of form a path gets, and (for buttons and layers) which variant its action or type picks.
    function kindAt(path, doc) {
        var node = at(doc, path);
        var n = path.length;
        if (n === 0) {
            return isObject(node) ? { kind: 'document' } : null;
        }

        if (!isObject(node)) {
            return null;
        }

        if (n === 2 && path[0] === 'menus') {
            return { kind: 'menu' };
        }

        if (n === 2 && path[0] === 'extras') {
            return { kind: 'extra' };
        }

        if (n === 4 && path[0] === 'menus' && path[2] === 'entries' && typeof path[3] === 'number') {
            return { kind: 'entry', variant: node.action };
        }

        if (path[n - 3] === 'layout' && path[n - 2] === 'layers' && typeof path[n - 1] === 'number') {
            return { kind: 'layer', variant: node.type };
        }

        return null;
    }

    function codePoints(s) {
        return Array.from(s).length;
    }

    function create(options) {
        var container = options.container;
        var hints = options.hints;
        var JT = options.JsonText;
        var doc = null;
        var current = [];
        var ask = options.confirm || function (m) { return window.confirm(m); };
        var message = null;
        var openGroups = {}; // which collapsible sections the author has opened, by path

        function el(tag, className, text) {
            var e = document.createElement(tag);
            if (className) {
                e.className = className;
            }

            if (text !== undefined) {
                e.textContent = text;
            }

            return e;
        }

        function kindDef(name) {
            return hints.kinds[name];
        }

        function labelOf(f) {
            return f.label || f.name;
        }

        // ---- writing ------------------------------------------------------------------
        function tell(text) {
            message = text || null;
            var box = container.querySelector('.discEdInspMsg');
            if (box) {
                box.textContent = message || '';
            }
        }

        function edit(fn, failure) {
            tell(null);
            var applied;
            try {
                applied = options.commit(fn);
            } catch (e) {
                tell(e && e.code === 'syntax' ? 'Fix the syntax problem in the text first.' : (failure || (e && e.message) || 'That change could not be made.'));
                return false;
            }

            if (!applied) {
                return false;
            }

            return true;
        }

        function anchors(kind, fieldName) {
            var names = kindDef(kind).fields.map(function (f) { return f.name; });
            return names.slice(0, names.indexOf(fieldName)).reverse();
        }

        function setField(path, kind, f, value) {
            return edit(function (text) {
                return JT.set(text, path.concat([f.name]), JSON.stringify(value), { after: anchors(kind, f.name) });
            });
        }

        function removeField(path, f) {
            return edit(function (text) {
                return JT.remove(text, path.concat([f.name]));
            });
        }

        // ---- one field ----------------------------------------------------------------
        function fieldRow(f, kind) {
            var row = el('div', 'discEdField');
            var id = 'discEdF' + Math.random().toString(36).slice(2, 9);
            var label = el('label', 'discEdFieldLabel', labelOf(f) + (f.required ? ' *' : ''));
            label.setAttribute('for', id);
            if (f.description) {
                label.title = f.description;
            }

            row.appendChild(label);
            row.dataset.id = id;
            return row;
        }

        function addHelp(row, f) {
            if (f.help) {
                row.appendChild(el('div', 'discEdFieldHelp', f.help));
            }
        }

        function problem(row, text) {
            var box = row.querySelector('.discEdFieldMsg');
            if (!box) {
                box = el('div', 'discEdFieldMsg');
                box.setAttribute('role', 'alert');
                row.appendChild(box);
            }

            box.textContent = text || '';
        }

        function keyOf(path, f) {
            return path.concat([f.name]).join('/');
        }

        function checkString(f, v) {
            if (f.minLength && codePoints(v) < f.minLength) {
                return 'Needs at least ' + f.minLength + ' character' + (f.minLength === 1 ? '' : 's') + '.';
            }

            if (f.maxLength && codePoints(v) > f.maxLength) {
                return 'At most ' + f.maxLength + ' characters.';
            }

            if (f.format === 'label' && /[<>]/.test(v)) {
                return 'Plain text only: no < or >.';
            }

            if (f.pattern && !new RegExp(f.pattern).test(v)) {
                return f.format === 'color' ? 'A colour like #ff8800.' : f.format === 'key' ? 'Lower-case letters, numbers, - and _ (up to 64).' : f.format === 'imageRef' || f.format === 'audioRef' ? 'An https address or an asset: reference.' : 'That is not in the form this needs.';
            }

            return null;
        }

        function checkNumber(f, n) {
            if (!isFinite(n)) {
                return 'Enter a number.';
            }

            if (f.type === 'integer' && Math.floor(n) !== n) {
                return 'Enter a whole number.';
            }

            if (f.minimum !== undefined && n < f.minimum) {
                return 'At least ' + f.minimum + '.';
            }

            if (f.maximum !== undefined && n > f.maximum) {
                return 'At most ' + f.maximum + '.';
            }

            if (f.exclusiveMinimum !== undefined && n <= f.exclusiveMinimum) {
                return 'More than ' + f.exclusiveMinimum + '.';
            }

            return null;
        }

        function renderEnum(path, kind, f, node, row) {
            var cur = node[f.name];
            var sel = el('select');
            sel.id = row.dataset.id;
            sel.dataset.key = keyOf(path, f);
            if (!f.required) {
                sel.appendChild(new Option('(default' + (f.default !== undefined ? ': ' + f.default : '') + ')', ''));
            }

            f.enum.forEach(function (v) {
                var o = new Option(kind === 'entry' && f.name === 'action' ? ACTION_NAMES[v] || v : v, v);
                var why = unavailable(kind, f, v);
                if (why) {
                    o.disabled = true;
                    o.textContent += ' (' + why + ')';
                }

                sel.appendChild(o);
            });
            if (cur !== undefined && f.enum.indexOf(cur) < 0) {
                sel.appendChild(new Option(String(cur) + ' (not allowed)', String(cur)));
            }

            sel.value = cur === undefined ? '' : String(cur);
            sel.addEventListener('change', function () {
                if (sel.value === (cur === undefined ? '' : String(cur))) {
                    return;
                }

                if (isDiscriminator(kind, f)) {
                    changeVariant(path, kind, f, sel.value);
                } else if (sel.value === '') {
                    removeField(path, f);
                } else {
                    setField(path, kind, f, sel.value);
                }
            });
            row.appendChild(sel);
        }

        function isDiscriminator(kind, f) {
            var k = kindDef(kind);
            return (k.discriminator === f.name) || (kind === 'background' && f.name === 'source');
        }

        function unavailable(kind, f, v) {
            if (kind !== 'entry' || f.name !== 'action') {
                return null;
            }

            var extras = doc && isObject(doc.extras) ? Object.keys(doc.extras).length : 0;
            var menus = doc && isObject(doc.menus) ? Object.keys(doc.menus).length : 0;
            if (v === 'playExtra' && extras < 1) {
                return 'add an extra first';
            }

            if (v === 'playSequence' && extras < 2) {
                return 'needs two extras';
            }

            if (v === 'submenu' && menus < 2) {
                return 'needs another page';
            }

            return null;
        }

        // Changing what a button does, what a layer is, or where a background comes from: set it, drop what no longer applies, add what is needed.
        function changeVariant(path, kind, f, value) {
            return edit(function (text) {
                var node = JT.getValue(text, path) || {};
                var out = JT.set(text, path.concat([f.name]), JSON.stringify(value), { after: anchors(kind, f.name) });
                var keep;
                var add = {};
                if (kind === 'background') {
                    Object.keys(node).forEach(function (k) {
                        var spec = kindDef('background').fields.filter(function (x) { return x.name === k; })[0];
                        var applies = spec && spec.appliesTo;
                        if (k !== 'source' && applies && applies.indexOf('*') < 0 && applies.indexOf(value) < 0) {
                            out = JT.remove(out, path.concat([k]));
                        }
                    });
                    if (value === 'color' && node.color === undefined) {
                        add.color = '#000000';
                    }

                    if (value === 'jellyfin' && node.imageType === undefined) {
                        add.imageType = 'Backdrop';
                    }
                } else {
                    var variants = kindDef(kind).variants || {};
                    keep = (variants[value] && variants[value].fields) || [];
                    Object.keys(node).forEach(function (k) {
                        if (k !== f.name && k !== 'label' && keep.indexOf(k) < 0) {
                            out = JT.remove(out, path.concat([k]));
                        }
                    });
                    var extraKeys = isObject(doc.extras) ? Object.keys(doc.extras) : [];
                    var pageKeys = isObject(doc.menus) ? Object.keys(doc.menus) : [];
                    var here = path[1];
                    if (value === 'playExtra' && node.extra === undefined) {
                        add.extra = extraKeys[0];
                    } else if (value === 'playSequence' && node.extras === undefined) {
                        add.extras = extraKeys.slice(0, 2);
                    } else if (value === 'submenu' && node.menu === undefined) {
                        add.menu = pageKeys.filter(function (k) { return k !== here; })[0] || pageKeys[0];
                    }
                }

                Object.keys(add).forEach(function (k) {
                    out = JT.set(out, path.concat([k]), JSON.stringify(add[k]), { after: anchors(kind, k) });
                });
                return out;
            });
        }

        function renderBoolean(path, kind, f, node, row) {
            var cur = node[f.name];
            var sel = el('select');
            sel.id = row.dataset.id;
            sel.dataset.key = keyOf(path, f);
            sel.appendChild(new Option('(default' + (f.default !== undefined ? ': ' + (f.default ? 'yes' : 'no') : '') + ')', ''));
            sel.appendChild(new Option('Yes', 'true'));
            sel.appendChild(new Option('No', 'false'));
            sel.value = cur === undefined ? '' : String(cur);
            sel.addEventListener('change', function () {
                if (sel.value === (cur === undefined ? '' : String(cur))) {
                    return;
                }

                if (sel.value === '') {
                    removeField(path, f);
                } else {
                    setField(path, kind, f, sel.value === 'true');
                }
            });
            row.appendChild(sel);
        }

        function renderNumber(path, kind, f, node, row) {
            var cur = node[f.name];
            var input = el('input');
            input.id = row.dataset.id;
            input.dataset.key = keyOf(path, f);
            input.type = 'number';
            if (f.minimum !== undefined) { input.min = f.minimum; }
            if (f.maximum !== undefined) { input.max = f.maximum; }
            input.step = f.step !== undefined ? f.step : (f.type === 'integer' ? 1 : 'any');
            input.value = typeof cur === 'number' ? String(cur) : '';
            if (f.default !== undefined) {
                input.placeholder = String(f.default);
            }

            input.addEventListener('change', function () {
                var raw = input.value.trim();
                if ((cur === undefined && raw === '') || (typeof cur === 'number' && raw !== '' && Number(raw) === cur)) {
                    return;
                }

                if (raw === '') {
                    if (f.required) {
                        problem(row, 'This is needed.');
                        return;
                    }

                    problem(row, '');
                    removeField(path, f);
                    return;
                }

                var n = Number(raw);
                var why = checkNumber(f, n);
                problem(row, why);
                if (!why) {
                    setField(path, kind, f, n);
                }
            });
            row.appendChild(input);
        }

        function renderText(path, kind, f, node, row) {
            var cur = node[f.name];
            var isRef = f.control === 'menuKey' || f.control === 'extraKey';
            if (isRef) {
                return renderReference(path, kind, f, node, row);
            }

            if (typeof cur === 'string' && /^data:image\//.test(cur) && (f.format === 'imageRef')) {
                var wrap = el('div', 'discEdEmbedded');
                var img = el('img');
                img.alt = '';
                img.src = cur;
                img.className = 'discEdThumb';
                wrap.appendChild(img);
                wrap.appendChild(el('span', null, 'Picture stored inside the menu (' + Math.max(1, Math.round(cur.length / 1024)) + ' KB)'));
                var clear = el('button', 'discEdSmall', 'Remove');
                clear.type = 'button';
                clear.addEventListener('click', function () { removeField(path, f); });
                wrap.appendChild(clear);
                row.appendChild(wrap);
                return null;
            }

            var input = el('input');
            input.id = row.dataset.id;
            input.dataset.key = keyOf(path, f);
            input.type = 'text';
            input.spellcheck = false;
            input.value = typeof cur === 'string' ? cur : '';
            if (f.placeholder || f.default !== undefined) {
                input.placeholder = f.placeholder || String(f.default);
            }

            var sw = null;
            if (f.format === 'color') {
                sw = el('input', 'discEdSwatch');
                sw.type = 'color';
                sw.value = /^#[0-9a-fA-F]{6}$/.test(input.value) ? input.value.toLowerCase() : (f.defaultValue || '#000000');
                sw.setAttribute('aria-label', labelOf(f) + ' (colour picker)');
                sw.addEventListener('change', function () {
                    input.value = sw.value;
                    input.dispatchEvent(new Event('change'));
                });
            }

            input.addEventListener('change', function () {
                var v = input.value.trim();
                if ((cur === undefined && v === '') || v === cur) {
                    return;
                }

                if (v === '') {
                    if (f.required) {
                        problem(row, 'This is needed.');
                        return;
                    }

                    problem(row, '');
                    removeField(path, f);
                    return;
                }

                var why = checkString(f, v);
                problem(row, why);
                if (!why) {
                    setField(path, kind, f, v);
                }
            });
            var line = el('div', 'discEdInline');
            line.appendChild(input);
            if (sw) {
                line.appendChild(sw);
            }

            if (f.control === 'fanart' && options.actions && options.actions.fanart) {
                var pick = el('button', 'discEdSmall', 'Pick from fanart.tv…');
                pick.type = 'button';
                pick.addEventListener('click', function () { options.actions.fanart(path); });
                line.appendChild(pick);
            }

            // Pictures and sounds can be chosen from this menu's uploaded files (or uploaded now); a TMDB backdrop from the title's list.
            var pickType = f.control === 'tmdb' ? 'tmdb' : f.format === 'imageRef' ? 'image' : f.format === 'audioRef' ? 'audio' : null;
            if (pickType && options.actions && options.actions.pick) {
                var choose = el('button', 'discEdSmall', pickType === 'tmdb' ? 'Pick a TMDB backdrop…' : pickType === 'image' ? 'Choose or upload…' : 'Choose or upload…');
                choose.type = 'button';
                choose.dataset.key = keyOf(path, f) + '/choose';
                choose.addEventListener('click', function () {
                    options.actions.pick({
                        type: pickType,
                        current: typeof cur === 'string' ? cur : null,
                        write: function (value) { return setField(path, kind, f, value); }
                    });
                });
                line.appendChild(choose);
            }

            row.appendChild(line);
            return null;
        }

        function renderReference(path, kind, f, node, row) {
            var cur = node[f.name];
            var keys = Object.keys((doc && isObject(doc[f.control === 'menuKey' ? 'menus' : 'extras'])) ? doc[f.control === 'menuKey' ? 'menus' : 'extras'] : {});
            var sel = el('select');
            sel.id = row.dataset.id;
            sel.dataset.key = keyOf(path, f);
            if (!f.required) {
                sel.appendChild(new Option('(none)', ''));
            }

            keys.forEach(function (k) {
                var label = k;
                if (f.control === 'menuKey' && isObject(doc.menus[k]) && typeof doc.menus[k].title === 'string') {
                    label = doc.menus[k].title + ' [' + k + ']';
                }

                sel.appendChild(new Option(label, k));
            });
            if (typeof cur === 'string' && keys.indexOf(cur) < 0) {
                sel.appendChild(new Option(cur + ' (does not exist)', cur));
            }

            sel.value = typeof cur === 'string' ? cur : '';
            sel.addEventListener('change', function () {
                if (sel.value === (typeof cur === 'string' ? cur : '')) {
                    return;
                }

                if (sel.value === '') {
                    removeField(path, f);
                } else {
                    setField(path, kind, f, sel.value);
                }
            });
            row.appendChild(sel);
            return null;
        }

        function renderKeyList(path, kind, f, node, row) {
            var list = Array.isArray(node[f.name]) ? node[f.name] : [];
            var keys = isObject(doc.extras) ? Object.keys(doc.extras) : [];
            var box = el('div', 'discEdList');
            list.forEach(function (k, i) {
                var line = el('div', 'discEdListRow');
                line.appendChild(el('span', 'discEdGrow', String(k) + (keys.indexOf(k) < 0 ? ' (does not exist)' : '')));
                line.appendChild(iconButton('↑', 'Move up', i === 0, function () { reorder(path.concat([f.name]), i, i - 1); }));
                line.appendChild(iconButton('↓', 'Move down', i === list.length - 1, function () { reorder(path.concat([f.name]), i, i + 1); }));
                line.appendChild(iconButton('✕', 'Remove', list.length <= (f.minItems || 0), function () {
                    edit(function (text) { return JT.remove(text, path.concat([f.name, i])); });
                }));
                box.appendChild(line);
            });
            var add = el('div', 'discEdListRow');
            var sel = el('select');
            sel.dataset.key = keyOf(path, f) + '/add';
            sel.setAttribute('aria-label', 'Extra to add');
            keys.forEach(function (k) { sel.appendChild(new Option(k, k)); });
            var btn = el('button', 'discEdSmall', 'Add');
            btn.type = 'button';
            btn.disabled = keys.length === 0;
            btn.addEventListener('click', function () {
                if (sel.value) {
                    edit(function (text) { return JT.insertInArray(text, path.concat([f.name]), list.length, JSON.stringify(sel.value)); });
                }
            });
            add.appendChild(sel);
            add.appendChild(btn);
            box.appendChild(add);
            row.appendChild(box);
        }

        function reorder(arrayPath, from, to) {
            return edit(function (text) { return JT.moveInArray(text, arrayPath, from, to); });
        }

        function iconButton(glyph, title, disabled, onClick) {
            var b = el('button', 'discEdIcon', glyph);
            b.type = 'button';
            b.title = title;
            b.setAttribute('aria-label', title);
            b.disabled = !!disabled;
            b.addEventListener('click', onClick);
            return b;
        }

        // ---- nested parts -------------------------------------------------------------
        // A section that is present is a collapsible block; one that is absent is a single line with an Add button.
        function renderGroup(path, kind, f, node, depthBox) {
            var cur = node[f.name];
            var key = keyOf(path, f);
            if (isObject(cur)) {
                var group = el('details', 'discEdGroup');
                group.open = !!openGroups[key];
                group.dataset.group = key;
                group.addEventListener('toggle', function () { openGroups[key] = group.open; });
                group.appendChild(el('summary', null, labelOf(f)));
                renderFields(group, f.kind, path.concat([f.name]), cur);
                var rm = el('button', 'discEdSmall', 'Remove this section');
                rm.type = 'button';
                rm.dataset.key = key + '/remove';
                rm.addEventListener('click', function () { removeField(path, f); });
                group.appendChild(rm);
                depthBox.appendChild(group);
            } else {
                var line = el('div', 'discEdListRow discEdAbsent');
                line.appendChild(el('span', 'discEdGrow', labelOf(f)));
                var add = el('button', 'discEdSmall', 'Add');
                add.type = 'button';
                add.dataset.key = key + '/add';
                add.addEventListener('click', function () {
                    openGroups[key] = true;
                    setField(path, kind, f, f.create !== undefined ? f.create : {});
                });
                line.appendChild(add);
                depthBox.appendChild(line);
            }
        }

        function entryText(e) {
            var label = isObject(e) && typeof e.label === 'string' ? e.label : '(no label)';
            var action = isObject(e) ? e.action : '';
            return label + ' - ' + (ACTION_NAMES[action] || action || '?');
        }

        function freshEntry(action, pageKey) {
            var extraKeys = isObject(doc.extras) ? Object.keys(doc.extras) : [];
            var pageKeys = isObject(doc.menus) ? Object.keys(doc.menus) : [];
            var labels = { playFeature: 'Play', playExtra: 'Extra', playSequence: 'Play all', submenu: 'More', chapters: 'Scenes', home: 'Main menu', back: 'Back' };
            var e = { action: action, label: labels[action] };
            if (action === 'playExtra') { e.extra = extraKeys[0]; }
            if (action === 'playSequence') { e.extras = extraKeys.slice(0, 2); }
            if (action === 'submenu') { e.menu = pageKeys.filter(function (k) { return k !== pageKey; })[0]; }
            return e;
        }

        function renderEntries(path, kind, f, node, depthBox) {
            var entries = Array.isArray(node.entries) ? node.entries : [];
            var arrayPath = path.concat(['entries']);
            var group = el('fieldset', 'discEdGroup');
            group.appendChild(el('legend', null, labelOf(f) + ' (' + entries.length + ')'));
            var box = el('div', 'discEdList');
            entries.forEach(function (e, i) {
                var line = el('div', 'discEdListRow');
                var open = el('button', 'discEdLink discEdGrow', entryText(e));
                open.type = 'button';
                open.addEventListener('click', function () { options.select(arrayPath.concat([i])); });
                line.appendChild(open);
                line.appendChild(iconButton('↑', 'Move up', i === 0, function () { reorder(arrayPath, i, i - 1); }));
                line.appendChild(iconButton('↓', 'Move down', i === entries.length - 1, function () { reorder(arrayPath, i, i + 1); }));
                line.appendChild(iconButton('✕', 'Remove this button', entries.length <= 1, function () {
                    edit(function (text) { return JT.remove(text, arrayPath.concat([i])); });
                }));
                box.appendChild(line);
            });
            var add = el('div', 'discEdListRow');
            var sel = el('select');
            sel.dataset.key = path.join('/') + '/entries/add';
            sel.setAttribute('aria-label', 'Kind of button to add');
            var entryKind = kindDef('entry');
            var actionField = entryKind.fields.filter(function (x) { return x.name === 'action'; })[0];
            actionField.enum.forEach(function (a) {
                var o = new Option(ACTION_NAMES[a] || a, a);
                var why = unavailable('entry', actionField, a);
                if (why) { o.disabled = true; o.textContent += ' (' + why + ')'; }
                sel.appendChild(o);
            });
            var btn = el('button', 'discEdSmall', 'Add a button');
            btn.type = 'button';
            btn.addEventListener('click', function () {
                if (edit(function (text) { return JT.insertInArray(text, arrayPath, entries.length, JSON.stringify(freshEntry(sel.value, path[1]))); })) {
                    return;
                }
            });
            add.appendChild(sel);
            add.appendChild(btn);
            box.appendChild(add);
            group.appendChild(box);
            depthBox.appendChild(group);
        }

        function renderLayers(path, kind, f, node, depthBox) {
            var layers = Array.isArray(node.layers) ? node.layers : [];
            var arrayPath = path.concat(['layers']);
            var group = el('fieldset', 'discEdGroup');
            group.appendChild(el('legend', null, labelOf(f) + ' (' + layers.length + ')'));
            var box = el('div', 'discEdList');
            layers.forEach(function (l, i) {
                var line = el('div', 'discEdListRow');
                var open = el('button', 'discEdLink discEdGrow', 'Layer ' + (i + 1) + ': ' + (isObject(l) && l.type ? l.type : '?'));
                open.type = 'button';
                open.addEventListener('click', function () { options.select(arrayPath.concat([i])); });
                line.appendChild(open);
                line.appendChild(iconButton('↑', 'Move down in the drawing order', i === 0, function () { reorder(arrayPath, i, i - 1); }));
                line.appendChild(iconButton('↓', 'Move up in the drawing order', i === layers.length - 1, function () { reorder(arrayPath, i, i + 1); }));
                line.appendChild(iconButton('✕', 'Remove this layer', false, function () {
                    edit(function (text) { return JT.remove(text, arrayPath.concat([i])); });
                }));
                box.appendChild(line);
            });
            var btn = el('button', 'discEdSmall', 'Add a panel');
            btn.type = 'button';
            btn.disabled = layers.length >= (f.maxItems || 20);
            btn.addEventListener('click', function () {
                edit(function (text) {
                    var parentPath = path;
                    var out = text;
                    if (!isObject(JT.getValue(out, parentPath))) {
                        throw new Error('This part is not an object.');
                    }

                    if (JT.getValue(out, arrayPath) === undefined) {
                        out = JT.set(out, arrayPath, '[]', { after: anchors('layout', 'layers') });
                    }

                    return JT.insertInArray(out, arrayPath, layers.length, JSON.stringify(f.createItem));
                });
            });
            box.appendChild(btn);
            group.appendChild(box);
            depthBox.appendChild(group);
        }

        // ---- a whole object -----------------------------------------------------------
        function visibleFields(kindName, node) {
            var k = kindDef(kindName);
            var variant = k.discriminator && k.variants ? k.variants[node[k.discriminator]] : null;
            var source = kindName === 'background' ? node.source : null;
            return k.fields.filter(function (f) {
                if (f.hidden) {
                    return false;
                }

                if (variant && f.name !== k.discriminator && variant.fields.indexOf(f.name) < 0) {
                    return false;
                }

                if (k.discriminator && !variant && f.name !== k.discriminator) {
                    return false; // an unknown action or type: nothing else can be shown safely
                }

                if (f.appliesTo && f.appliesTo.indexOf('*') < 0 && source !== null && f.appliesTo.indexOf(source) < 0) {
                    return false;
                }

                return true;
            });
        }

        function renderFields(into, kindName, path, node, only, except) {
            var k = kindDef(kindName);
            var requiredNow = [];
            if (k.requiredWhen && k.requiredWhen.values[node[k.requiredWhen.field]]) {
                requiredNow = k.requiredWhen.values[node[k.requiredWhen.field]];
            }

            // Only the few fields that matter are shown at first; the rest sit under "More options", which opens by itself when something in it is set
            // (so nothing the menu already uses is hidden).
            var all = visibleFields(kindName, node).filter(function (f0) {
                return !((only && only.indexOf(f0.name) < 0) || (except && except.indexOf(f0.name) >= 0));
            });
            var isBasic = function (f0) {
                return !k.basic || k.basic.indexOf(f0.name) >= 0 || f0.required || requiredNow.indexOf(f0.name) >= 0;
            };
            var moreFields = all.filter(function (f0) { return !isBasic(f0); });
            var moreBox = null;
            var moreTarget = function () {
                if (!moreBox) {
                    var moreKey = path.join('/') + '#more-' + kindName;
                    var present = k.moreAuto !== false && moreFields.some(function (f0) { return node[f0.name] !== undefined; });
                    moreBox = el('details', 'discEdMore');
                    moreBox.open = openGroups[moreKey] !== undefined ? openGroups[moreKey] : present;
                    moreBox.addEventListener('toggle', function () { openGroups[moreKey] = moreBox.open; });
                    moreBox.appendChild(el('summary', null, k.moreLabel || 'More options'));
                    into.appendChild(moreBox);
                }

                return moreBox;
            };

            all.forEach(function (f0) {
                var f = f0;
                if (requiredNow.indexOf(f.name) >= 0 && !f.required) {
                    f = JSON.parse(JSON.stringify(f0));
                    f.required = true;
                }

                var into0 = into;
                into = isBasic(f0) ? into0 : moreTarget();
                try {
                    renderOneField(into, kindName, path, node, f);
                } finally {
                    into = into0;
                }
            });
        }

        function renderOneField(into, kindName, path, node, f) {
            {

                if (f.type === 'object') {
                    renderGroup(path, kindName, f, node, into);
                    return;
                }

                if (f.type === 'array' && f.kind === 'entry') {
                    renderEntries(path, kindName, f, node, into);
                    return;
                }

                if (f.type === 'array' && f.kind === 'layer') {
                    renderLayers(path, kindName, f, node, into);
                    return;
                }

                if (f.type === 'map') {
                    return;
                }

                var row = fieldRow(f, kindName);
                if (f.type === 'enum') {
                    renderEnum(path, kindName, f, node, row);
                } else if (f.type === 'boolean') {
                    renderBoolean(path, kindName, f, node, row);
                } else if (f.type === 'integer' || f.type === 'number') {
                    renderNumber(path, kindName, f, node, row);
                } else if (f.type === 'array' && f.control === 'extraKeys') {
                    renderKeyList(path, kindName, f, node, row);
                } else if (f.type === 'string') {
                    renderText(path, kindName, f, node, row);
                } else {
                    return;
                }

                addHelp(row, f);
                if (f.required && node[f.name] === undefined) {
                    problem(row, 'This is needed.');
                }

                into.appendChild(row);
            }
        }

        // ---- the whole-menu form also manages the pages and extras --------------------
        function renderPages(into) {
            var menus = isObject(doc.menus) ? doc.menus : {};
            var keys = Object.keys(menus);
            var group = el('fieldset', 'discEdGroup');
            group.appendChild(el('legend', null, 'Pages (' + keys.length + ')'));
            var box = el('div', 'discEdList');
            keys.forEach(function (k) {
                var m = menus[k];
                var line = el('div', 'discEdListRow');
                var open = el('button', 'discEdLink discEdGrow', (isObject(m) && m.title ? m.title : k) + ' [' + k + ']' + (k === doc.root ? ' (first)' : ''));
                open.type = 'button';
                open.addEventListener('click', function () { options.select(['menus', k]); });
                line.appendChild(open);
                var first = el('button', 'discEdSmall', 'Make first');
                first.type = 'button';
                first.disabled = k === doc.root;
                first.addEventListener('click', function () {
                    edit(function (text) { return JT.set(text, ['root'], JSON.stringify(k)); });
                });
                line.appendChild(first);
                line.appendChild(iconButton('✕', 'Delete this page', k === doc.root || keys.length <= 1, function () {
                    if (referencesPage(k) && !ask('Buttons still open "' + k + '". Delete it anyway? They will show as problems to fix.')) {
                        return;
                    }

                    edit(function (text) { return JT.remove(text, ['menus', k]); });
                }));
                box.appendChild(line);
            });
            var add = el('div', 'discEdListRow');
            var input = el('input');
            input.type = 'text';
            input.placeholder = 'new-page-name';
            input.dataset.key = 'menus/add-name';
            input.setAttribute('aria-label', 'Name for the new page');
            input.maxLength = 64;
            var btn = el('button', 'discEdSmall', 'Add a page');
            btn.type = 'button';
            var msg = el('div', 'discEdFieldMsg');
            btn.addEventListener('click', function () {
                var k = input.value.trim();
                if (!KEY.test(k)) {
                    msg.textContent = 'Use lower-case letters, numbers, - and _ (up to 64).';
                    return;
                }

                if (Object.prototype.hasOwnProperty.call(menus, k)) {
                    msg.textContent = 'There is already a page called ' + k + '.';
                    return;
                }

                msg.textContent = '';
                edit(function (text) {
                    return JT.set(text, ['menus', k], JSON.stringify({ title: 'New page', entries: [{ action: 'back', label: 'Back' }] }));
                });
            });
            add.appendChild(input);
            add.appendChild(btn);
            box.appendChild(add);
            box.appendChild(msg);
            group.appendChild(box);
            into.appendChild(group);
        }

        function referencesPage(key) {
            return Object.keys(doc.menus || {}).some(function (k) {
                var entries = isObject(doc.menus[k]) && Array.isArray(doc.menus[k].entries) ? doc.menus[k].entries : [];
                return entries.some(function (e) { return isObject(e) && e.menu === key; });
            });
        }

        function referencesExtra(key) {
            return Object.keys(doc.menus || {}).some(function (k) {
                var entries = isObject(doc.menus[k]) && Array.isArray(doc.menus[k].entries) ? doc.menus[k].entries : [];
                return entries.some(function (e) { return isObject(e) && (e.extra === key || (Array.isArray(e.extras) && e.extras.indexOf(key) >= 0)); });
            });
        }

        function renderExtras(into) {
            var extras = isObject(doc.extras) ? doc.extras : {};
            var keys = Object.keys(extras);
            var group = el('fieldset', 'discEdGroup');
            group.appendChild(el('legend', null, 'Extras (' + keys.length + ')'));
            var box = el('div', 'discEdList');
            keys.forEach(function (k) {
                var x = extras[k];
                var line = el('div', 'discEdListRow');
                var open = el('button', 'discEdLink discEdGrow', k + (isObject(x) && x.type ? ' - ' + x.type : ''));
                open.type = 'button';
                open.addEventListener('click', function () { options.select(['extras', k]); });
                line.appendChild(open);
                line.appendChild(iconButton('✕', 'Delete this extra', false, function () {
                    if (referencesExtra(k) && !ask('Buttons still play "' + k + '". Delete it anyway? They will show as problems to fix.')) {
                        return;
                    }

                    edit(function (text) { return JT.remove(text, ['extras', k]); });
                }));
                box.appendChild(line);
            });
            var add = el('div', 'discEdListRow');
            var input = el('input');
            input.type = 'text';
            input.placeholder = 'new-extra-name';
            input.dataset.key = 'extras/add-name';
            input.setAttribute('aria-label', 'Name for the new extra');
            input.maxLength = 64;
            var type = el('select');
            type.setAttribute('aria-label', 'Kind of extra');
            EXTRA_TYPES.forEach(function (t) { type.appendChild(new Option(t, t)); });
            type.value = 'Featurette';
            var btn = el('button', 'discEdSmall', 'Add an extra');
            btn.type = 'button';
            var msg = el('div', 'discEdFieldMsg');
            btn.addEventListener('click', function () {
                var k = input.value.trim();
                if (!KEY.test(k)) {
                    msg.textContent = 'Use lower-case letters, numbers, - and _ (up to 64).';
                    return;
                }

                if (Object.prototype.hasOwnProperty.call(extras, k)) {
                    msg.textContent = 'There is already an extra called ' + k + '.';
                    return;
                }

                msg.textContent = '';
                edit(function (text) {
                    var out = text;
                    if (JT.getValue(out, ['extras']) === undefined) {
                        out = JT.set(out, ['extras'], '{}', { after: ['match', 'meta'] });
                    }

                    return JT.set(out, ['extras', k], JSON.stringify({ type: type.value, durationSec: 60 }));
                });
            });
            add.appendChild(input);
            add.appendChild(type);
            add.appendChild(btn);
            box.appendChild(add);
            box.appendChild(msg);
            group.appendChild(box);
            into.appendChild(group);
        }

        // ---- drawing ------------------------------------------------------------------
        function describePath(path) {
            return path.map(function (p) { return typeof p === 'number' ? '[' + (p + 1) + ']' : '.' + p; }).join('').replace(/^\./, '');
        }

        // What the author sees as the heading, in their words rather than as a path.
        function titleOf(kind, path, node, k) {
            if (kind === 'entry') {
                return 'Button: ' + (typeof node.label === 'string' && node.label ? node.label : '(no text)');
            }

            if (kind === 'menu') {
                return 'Page: ' + (typeof node.title === 'string' && node.title ? node.title : path[1]);
            }

            if (kind === 'extra') {
                return 'Extra: ' + path[1];
            }

            if (kind === 'layer') {
                return 'Decoration ' + (path[path.length - 1] + 1);
            }

            return k.title || kind;
        }

        // The part one level up, for the "back" link.
        function parentOf(kind, path) {
            if (kind === 'entry') {
                var page = doc.menus && doc.menus[path[1]];
                return { path: path.slice(0, 2), label: 'Back to ' + (page && page.title ? page.title : path[1]) };
            }

            if (kind === 'layer') {
                var parent = path.slice(0, -3);
                var node = at(doc, parent);
                return { path: parent, label: 'Back to ' + (parent.length ? (node && node.title ? node.title : parent[1]) : 'the whole menu') };
            }

            if (kind === 'menu' || kind === 'extra') {
                return { path: [], label: 'Back to the whole menu' };
            }

            return null;
        }

        function focusKey() {
            var a = document.activeElement;
            if (a && container.contains(a) && a.dataset && a.dataset.key) {
                var pos = null;
                try { pos = a.selectionStart; } catch (e) { pos = null; }
                return { key: a.dataset.key, pos: pos };
            }

            return null;
        }

        function restoreFocus(saved) {
            if (!saved) {
                return;
            }

            var nodes = container.querySelectorAll('[data-key]');
            for (var i = 0; i < nodes.length; i++) {
                if (nodes[i].dataset.key === saved.key) {
                    nodes[i].focus();
                    try { if (saved.pos !== null && saved.pos !== undefined) { nodes[i].setSelectionRange(saved.pos, saved.pos); } } catch (e) { /* not a text control */ }
                    return;
                }
            }
        }

        function draw() {
            var saved = focusKey();
            container.textContent = '';
            var text = options.getText();
            try {
                doc = JSON.parse(text);
            } catch (e) {
                doc = null;
            }

            if (!isObject(doc)) {
                container.appendChild(el('div', 'discEdInspNone', 'Fix the syntax problem in the text to use the forms.'));
                return;
            }

            var found = kindAt(current, doc);
            while (!found && current.length) {
                current = current.slice(0, -1);
                found = kindAt(current, doc);
            }

            if (!found) {
                container.appendChild(el('div', 'discEdInspNone', 'This menu has nothing to show a form for.'));
                return;
            }

            var node = at(doc, current);
            var k = kindDef(found.kind);
            var head = el('div', 'discEdInspHead');
            var up = parentOf(found.kind, current);
            if (up) {
                var back = el('button', 'discEdLink discEdBack', '\u2190 ' + up.label);
                back.type = 'button';
                back.addEventListener('click', function () { options.select(up.path); });
                head.appendChild(back);
            }

            head.appendChild(el('strong', 'discEdTitle', titleOf(found.kind, current, node, k)));
            container.appendChild(head);
            if (k.hint) {
                container.appendChild(el('div', 'discEdHint', k.hint));
            }
            var msg = el('div', 'discEdInspMsg');
            msg.setAttribute('role', 'alert');
            msg.textContent = message || '';
            container.appendChild(msg);

            if (found.kind === 'entry' || found.kind === 'layer') {
                var variantDef = (k.variants || {})[found.variant];
                if (variantDef && variantDef.title) {
                    container.appendChild(el('div', 'discEdFieldHelp', variantDef.title));
                }
            }

            if (found.kind === 'document') {
                renderFields(container, found.kind, current, node, ['root']);
                renderPages(container);
                renderExtras(container);
                renderFields(container, found.kind, current, node, null, ['root']);
            } else {
                renderFields(container, found.kind, current, node);
            }

            if (found.kind === 'extra') {
                // nothing extra: the fields above are all of it
            }

            restoreFocus(saved);
        }

        return {
            show: function (path) {
                current = path.slice();
                message = null;
                draw();
            },
            refresh: draw,
            // Open the named collapsible sections (keys as used by openGroups: a section's path, or path#more-kind) and bring the last into view.
            reveal: function (keys) {
                keys.forEach(function (k) { openGroups[k] = true; });
                draw();
                var last = keys[keys.length - 1];
                var target = container.querySelector('[data-group="' + last + '"]');
                if (target && typeof target.scrollIntoView === 'function') {
                    target.scrollIntoView({ block: 'nearest' });
                }
            },
            path: function () { return current.slice(); }
        };
    }

    return { create: create, kindAt: kindAt };
});
