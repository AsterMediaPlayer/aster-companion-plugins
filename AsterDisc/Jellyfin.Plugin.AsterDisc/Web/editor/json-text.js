/*
 * Edits JSON *text* in place, by path, and leaves everything else in the file exactly as the author wrote it: indentation, key order,
 * spacing, line endings. The Menu Editor keeps the text as its single source of truth and every form, picker and drag is one of these
 * operations, so a change is one small splice (and one undo step), never a re-serialisation of the whole file.
 *
 *   parse(text)                       -> { root, duplicates }   positions of every value (throws a 'syntax' error on bad JSON)
 *   get(text, path)                   -> the value's JSON text, or undefined          (path: ['menus','main','title'] or ['menus','main','entries',0])
 *   getValue(text, path)              -> the parsed value, or undefined
 *   locate(text, path)                -> { start, end, line, column } of the value (and keyStart for a property), or null
 *   set(text, path, valueText, opts)  -> new text: replace the value, or add the property (creating missing objects on the way);
 *                                        opts.after = property names to insert after, in order of preference
 *   remove(text, path)                -> new text without that property or array item
 *   insertInArray(text, path, index, valueText) -> new text with the item inserted (index past the end appends)
 *   moveInArray(text, path, from, to) -> new text with the item moved (to = its final index)
 *
 * Rules that keep edits safe:
 *  - Anything that edits refuses (error code 'syntax') when the text is not valid JSON, and refuses (code 'duplicate') when any object
 *    has a repeated key, because "which one did the form change?" has no right answer. Reading with get/locate does not need that.
 *  - Every result is re-parsed before it is returned (code 'internal' if it would not parse: a bug here, never bad output).
 *  - Values given as text must themselves be valid JSON (code 'value').
 *
 * Works in a browser (global DiscMenusJsonText) and in Node (module.exports). No dependencies.
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusJsonText = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    var MAX_DEPTH = 64;

    function JsonTextError(code, message, details) {
        var e = new Error(message);
        e.name = 'JsonTextError';
        e.code = code;
        Object.keys(details || {}).forEach(function (k) { e[k] = details[k]; });
        return e;
    }

    function position(text, index) {
        var line = 1, last = -1;
        for (var i = 0; i < index && i < text.length; i++) {
            if (text.charCodeAt(i) === 10) { line++; last = i; }
        }

        return { line: line, column: index - last };
    }

    function pathText(path) {
        return path.map(function (p) { return typeof p === 'number' ? '[' + p + ']' : '.' + p; }).join('').replace(/^\./, '') || '(root)';
    }

    // The position and reason of the first syntax error, or null if the text is valid JSON. Browsers word and position
    // JSON.parse errors differently (some give no position at all), so the editor finds its own.
    function findError(text) {
        var i = 0, n = text.length;
        function fail(msg, at) { throw { index: at === undefined ? i : at, message: msg }; }
        function ws() {
            while (i < n) {
                var c = text.charCodeAt(i);
                if (c === 32 || c === 9 || c === 10 || c === 13) { i++; } else { break; }
            }
        }

        function str() {
            i++;
            while (i < n) {
                var c = text.charCodeAt(i);
                if (c === 34) { i++; return; }
                if (c < 32) { fail('A string cannot contain a raw line break or control character'); }
                if (c === 92) {
                    var d = text.charAt(i + 1);
                    if ('"\\/bfnrt'.indexOf(d) >= 0 && d !== '') { i += 2; }
                    else if (d === 'u' && /^[0-9a-fA-F]{4}$/.test(text.substr(i + 2, 4))) { i += 6; }
                    else { fail('Unknown escape in a string', i + 1); }
                } else { i++; }
            }

            fail('A string is not closed', n);
        }

        function val(depth) {
            if (depth > 400) { fail('Nested too deeply'); }
            ws();
            if (i >= n) { fail('The JSON ends too soon', n); }
            var c = text.charAt(i);
            if (c === '{') {
                i++;
                ws();
                if (text.charAt(i) === '}') { i++; return; }
                for (;;) {
                    ws();
                    if (text.charAt(i) !== '"') { fail(i >= n ? 'The JSON ends too soon' : 'A property name in quotes is expected'); }
                    str();
                    ws();
                    if (text.charAt(i) !== ':') { fail("A ':' is expected after the property name"); }
                    i++;
                    val(depth + 1);
                    ws();
                    if (text.charAt(i) === ',') { i++; continue; }
                    if (text.charAt(i) === '}') { i++; return; }
                    fail(i >= n ? 'The JSON ends too soon' : "A ',' or '}' is expected");
                }
            }

            if (c === '[') {
                i++;
                ws();
                if (text.charAt(i) === ']') { i++; return; }
                for (;;) {
                    val(depth + 1);
                    ws();
                    if (text.charAt(i) === ',') { i++; continue; }
                    if (text.charAt(i) === ']') { i++; return; }
                    fail(i >= n ? 'The JSON ends too soon' : "A ',' or ']' is expected");
                }
            }

            if (c === '"') { str(); return; }
            var lit = /^(true|false|null)/.exec(text.slice(i, i + 5));
            if (lit) { i += lit[0].length; return; }
            var num = /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?/.exec(text.slice(i, i + 80));
            if (num) { i += num[0].length; return; }
            fail("Unexpected '" + c + "'");
        }

        try {
            val(0);
            ws();
            if (i < n) { fail('Unexpected text after the end of the JSON'); }
        } catch (e) {
            if (e && typeof e.index === 'number') { return e; }
            throw e;
        }

        return null;
    }

    // ---- scanning --------------------------------------------------------------------------------------------------------
    function parse(text) {
        if (typeof text !== 'string') {
            throw JsonTextError('syntax', 'The text is not a string.');
        }

        var found = findError(text);
        if (found) {
            throw JsonTextError('syntax', found.message + ' (line ' + position(text, found.index).line + ')', Object.assign({ index: found.index }, position(text, found.index)));
        }

        try {
            JSON.parse(text);
        } catch (e) {
            throw JsonTextError('syntax', String(e.message));
        }

        var i = 0, n = text.length, duplicates = [];

        function ws() {
            while (i < n) {
                var c = text.charCodeAt(i);
                if (c === 32 || c === 9 || c === 10 || c === 13) { i++; } else { break; }
            }
        }

        function str() {
            i++; // opening quote
            while (i < n) {
                var c = text.charCodeAt(i);
                if (c === 92) { i += 2; } else if (c === 34) { i++; return; } else { i++; }
            }
        }

        function value(depth, path) {
            if (depth > MAX_DEPTH) {
                throw JsonTextError('syntax', 'The JSON is nested too deeply to edit.');
            }

            ws();
            var start = i, c = text.charAt(i);
            if (c === '{') {
                i++;
                var props = [], seen = {};
                ws();
                if (text.charAt(i) === '}') { i++; return { type: 'object', start: start, end: i, props: props }; }
                for (;;) {
                    ws();
                    var ks = i;
                    str();
                    var key = JSON.parse(text.slice(ks, i));
                    if (Object.prototype.hasOwnProperty.call(seen, key)) {
                        duplicates.push({ path: path, key: key, index: ks });
                    }

                    seen[key] = true;
                    ws();
                    i++; // the colon
                    var v = value(depth + 1, path.concat([key]));
                    props.push({ key: key, keyStart: ks, value: v });
                    ws();
                    if (text.charAt(i) === ',') { i++; continue; }
                    i++; // the closing brace
                    break;
                }

                return { type: 'object', start: start, end: i, props: props };
            }

            if (c === '[') {
                i++;
                var items = [];
                ws();
                if (text.charAt(i) === ']') { i++; return { type: 'array', start: start, end: i, items: items }; }
                for (;;) {
                    var iv = value(depth + 1, path.concat([items.length]));
                    items.push({ value: iv });
                    ws();
                    if (text.charAt(i) === ',') { i++; continue; }
                    i++; // the closing bracket
                    break;
                }

                return { type: 'array', start: start, end: i, items: items };
            }

            if (c === '"') { str(); return { type: 'string', start: start, end: i }; }
            var m2 = /^(-?\d+(\.\d+)?([eE][+-]?\d+)?|true|false|null)/.exec(text.slice(i, i + 40));
            i += m2[0].length;
            return { type: 'primitive', start: start, end: i };
        }

        var tree = value(0, []);
        return { root: tree, duplicates: duplicates };
    }

    // For editing: valid JSON and no repeated keys.
    function parseForEditing(text) {
        var t = parse(text);
        if (t.duplicates.length > 0) {
            var d = t.duplicates[0];
            throw JsonTextError('duplicate', 'The key "' + d.key + '" appears more than once' + (d.path.length ? ' in ' + pathText(d.path) : '') + '. Remove the extra one first.',
                Object.assign({ index: d.index, key: d.key }, position(text, d.index)));
        }

        return t;
    }

    function lastProp(obj, key) {
        var found = null;
        obj.props.forEach(function (p) { if (p.key === key) { found = p; } });
        return found;
    }

    // Follows a path through the tree: {found:true,node,parent,holder} or {found:false,container,rest,index}
    function walk(rootNode, path) {
        var node = rootNode, parent = null, holder = null;
        for (var d = 0; d < path.length; d++) {
            var seg = path[d];
            if (node.type === 'object') {
                if (typeof seg !== 'string') {
                    throw JsonTextError('path', 'Expected a property name at ' + pathText(path.slice(0, d + 1)) + '.');
                }

                var prop = lastProp(node, seg);
                if (!prop) { return { found: false, container: node, rest: path.slice(d) }; }
                parent = node;
                holder = prop;
                node = prop.value;
            } else if (node.type === 'array') {
                if (typeof seg !== 'number' || seg < 0 || seg % 1 !== 0) {
                    throw JsonTextError('path', 'Expected an array position at ' + pathText(path.slice(0, d + 1)) + '.');
                }

                if (seg >= node.items.length) { return { found: false, container: node, rest: path.slice(d), index: seg }; }
                parent = node;
                holder = node.items[seg];
                node = holder.value;
            } else {
                throw JsonTextError('path', 'There is nothing inside ' + pathText(path.slice(0, d)) + ' (it is a ' + node.type + ').');
            }
        }

        return { found: true, node: node, parent: parent, holder: holder };
    }

    function splice(text, start, end, replacement) {
        return text.slice(0, start) + replacement + text.slice(end);
    }

    function checkValueText(valueText) {
        try {
            JSON.parse(valueText);
        } catch (e) {
            throw JsonTextError('value', 'The new value is not valid JSON: ' + String(e.message));
        }
    }

    // A pretty-printed value must use the file's own line endings.
    function adoptLineEndings(text, valueText) {
        return text.indexOf('\r\n') >= 0 ? valueText.replace(/\r?\n/g, '\r\n') : valueText.replace(/\r\n/g, '\n');
    }

    // Re-parses what an edit produced; a failure here is a bug in this module, and never reaches the caller as bad output.
    function verify(out) {
        try {
            JSON.parse(out);
            if (parse(out).duplicates.length) { throw new Error('duplicate keys'); }
        } catch (e) {
            throw JsonTextError('internal', 'The edit would have produced invalid JSON (' + e.message + '), so it was not applied.');
        }

        return out;
    }

    // The whitespace to put before a new property so it lines up with the one it follows: its line's indent, or one space on a one-line object.
    function gapBefore(text, keyStart) {
        var lineStart = text.lastIndexOf('\n', keyStart - 1) + 1;
        var lead = text.slice(lineStart, keyStart);
        if (lineStart > 0 && /^[ \t]*$/.test(lead)) {
            return (text.charAt(lineStart - 2) === '\r' ? '\r\n' : '\n') + lead;
        }

        return ' ';
    }

    function nest(rest, valueText) {
        if (rest.length === 0) { return valueText; }
        if (typeof rest[0] !== 'string') {
            throw JsonTextError('path', 'Cannot create an array position that does not exist yet.');
        }

        return '{ ' + JSON.stringify(rest[0]) + ': ' + nest(rest.slice(1), valueText) + ' }';
    }

    function insertProperty(text, obj, key, valueText, after) {
        var kv = JSON.stringify(key) + ': ' + valueText;
        if (obj.props.length === 0) {
            return splice(text, obj.start, obj.end, '{ ' + kv + ' }');
        }

        var anchor = null;
        (after || []).some(function (k) {
            var p = lastProp(obj, k);
            if (p) { anchor = p; return true; }
            return false;
        });
        anchor = anchor || obj.props[obj.props.length - 1];
        return splice(text, anchor.value.end, anchor.value.end, ',' + gapBefore(text, anchor.keyStart) + kv);
    }

    // The text that separates array items: the comma and whitespace the author already uses.
    function itemSeparator(text, arr) {
        var items = arr.items;
        if (items.length >= 2) { return text.slice(items[0].value.end, items[1].value.start); }
        var lead = text.slice(arr.start + 1, items[0].value.start);
        return lead.indexOf('\n') >= 0 ? ',' + lead : ', ';
    }

    function insertItem(text, arr, index, valueText) {
        var items = arr.items;
        if (items.length === 0) {
            return splice(text, arr.start, arr.end, '[' + valueText + ']');
        }

        var sep = itemSeparator(text, arr);
        if (index <= 0) { return splice(text, items[0].value.start, items[0].value.start, valueText + sep); }
        if (index >= items.length) { return splice(text, items[items.length - 1].value.end, items[items.length - 1].value.end, sep + valueText); }
        return splice(text, items[index].value.start, items[index].value.start, valueText + sep);
    }

    // ---- the operations ----------------------------------------------------------------------------------------------------
    function get(text, path) {
        var w = walk(parse(text).root, path || []);
        return w.found ? text.slice(w.node.start, w.node.end) : undefined;
    }

    function getValue(text, path) {
        var raw = get(text, path);
        return raw === undefined ? undefined : JSON.parse(raw);
    }

    function locate(text, path) {
        var w = walk(parse(text).root, path || []);
        if (!w.found) { return null; }
        var out = { start: w.node.start, end: w.node.end };
        Object.assign(out, position(text, w.node.start));
        if (w.holder && w.holder.keyStart !== undefined) { out.keyStart = w.holder.keyStart; }
        return out;
    }

    function set(text, path, valueText, opts) {
        var tree = parseForEditing(text);
        checkValueText(valueText);
        valueText = adoptLineEndings(text, valueText);
        if (!path || path.length === 0) {
            throw JsonTextError('path', 'The whole file cannot be replaced this way.');
        }

        var w = walk(tree.root, path), out;
        if (w.found) {
            out = splice(text, w.node.start, w.node.end, valueText);
        } else if (w.container.type === 'array') {
            if (w.rest.length === 1 && w.index === w.container.items.length) {
                out = insertItem(text, w.container, w.container.items.length, valueText);
            } else {
                throw JsonTextError('path', 'There is no position ' + w.index + ' in ' + pathText(path.slice(0, path.length - w.rest.length)) + '.');
            }
        } else {
            out = insertProperty(text, w.container, w.rest[0], nest(w.rest.slice(1), valueText), opts && opts.after);
        }

        return verify(out);
    }

    function remove(text, path) {
        var tree = parseForEditing(text);
        var w = walk(tree.root, path || []);
        if (!w.found) { throw JsonTextError('path', 'There is nothing at ' + pathText(path || []) + ' to remove.'); }
        if (!w.parent) { throw JsonTextError('path', 'The whole file cannot be removed.'); }
        var parent = w.parent, out;
        if (parent.type === 'object') {
            var props = parent.props, pi = props.indexOf(w.holder);
            if (props.length === 1) { out = splice(text, parent.start, parent.end, '{}'); }
            else if (pi < props.length - 1) { out = splice(text, w.holder.keyStart, props[pi + 1].keyStart, ''); }
            else { out = splice(text, props[pi - 1].value.end, w.holder.value.end, ''); }
        } else {
            var items = parent.items, ii = items.indexOf(w.holder);
            if (items.length === 1) { out = splice(text, parent.start, parent.end, '[]'); }
            else if (ii < items.length - 1) { out = splice(text, items[ii].value.start, items[ii + 1].value.start, ''); }
            else { out = splice(text, items[ii - 1].value.end, items[ii].value.end, ''); }
        }

        return verify(out);
    }

    function arrayAt(tree, path) {
        var w = walk(tree.root, path || []);
        if (!w.found || w.node.type !== 'array') {
            throw JsonTextError('path', pathText(path || []) + ' is not a list.');
        }

        return w.node;
    }

    function insertInArray(text, path, index, valueText) {
        var tree = parseForEditing(text);
        checkValueText(valueText);
        return verify(insertItem(text, arrayAt(tree, path), index, adoptLineEndings(text, valueText)));
    }

    function moveInArray(text, path, from, to) {
        var tree = parseForEditing(text);
        var arr = arrayAt(tree, path), count = arr.items.length;
        if (from % 1 !== 0 || to % 1 !== 0 || from < 0 || to < 0 || from >= count || to >= count) {
            throw JsonTextError('path', 'Cannot move position ' + from + ' to ' + to + ' in a list of ' + count + '.');
        }

        if (from === to) { return text; }
        var item = text.slice(arr.items[from].value.start, arr.items[from].value.end);
        var without = remove(text, path.concat([from]));
        return verify(insertInArray(without, path, to, item));
    }

    return {
        parse: parse, findError: findError, get: get, getValue: getValue, locate: locate, set: set, remove: remove,
        insertInArray: insertInArray, moveInArray: moveInArray, JsonTextError: JsonTextError,
    };
});
