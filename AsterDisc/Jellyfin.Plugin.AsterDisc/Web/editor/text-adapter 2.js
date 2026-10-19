/*
 * Applies a programmatic edit to the editor's <textarea> so that it becomes ONE step of the browser's own undo history.
 *
 * Assigning textarea.value wipes the browser's undo stack, which is what made the first fanart picker carry a private one-level Undo.
 * Instead the adapter works out the smallest changed range, selects it, and lets the browser perform the change with
 * document.execCommand('insertText' | 'delete'): the same path as typing, so native undo, redo and IME composition all keep working.
 *
 * Where that is not possible (jsdom in the tests, a browser that refuses, or a result that is not what was asked for) it falls back to
 * assigning the value and keeping its own small history, and says so through `usesNative`. A fallback never leaves wrong text behind.
 *
 *   var a = DiscMenusTextAdapter.create(textarea);
 *   a.apply(newText, caretIndexOrUndefined)  -> true if the text changed; fires 'input' either way the change was made
 *   a.undo() / a.redo()                      -> true if something was undone/redone
 *   a.reset()                                -> forget the fallback history (call when a different file is opened)
 *   DiscMenusTextAdapter.diff(oldText, newText) -> { start, endOld, replacement }  (exposed for tests)
 *
 * Works in a browser (global DiscMenusTextAdapter) and in Node (module.exports). No dependencies.
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusTextAdapter = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    function isHigh(c) { return c >= 0xD800 && c <= 0xDBFF; }
    function isLow(c) { return c >= 0xDC00 && c <= 0xDFFF; }

    // The smallest range of oldText that must be replaced to get newText (never splitting a surrogate pair).
    function diff(a, b) {
        var p = 0, max = Math.min(a.length, b.length);
        while (p < max && a.charCodeAt(p) === b.charCodeAt(p)) { p++; }
        if (p > 0 && p < a.length && isLow(a.charCodeAt(p)) && isHigh(a.charCodeAt(p - 1))) { p--; }
        var s = 0;
        while (s < a.length - p && s < b.length - p && a.charCodeAt(a.length - 1 - s) === b.charCodeAt(b.length - 1 - s)) { s++; }
        if (s > 0 && a.length - s > 0 && isLow(a.charCodeAt(a.length - s)) && isHigh(a.charCodeAt(a.length - s - 1))) { s--; }
        return { start: p, endOld: a.length - s, replacement: b.slice(p, b.length - s) };
    }

    function create(ta) {
        var doc = ta.ownerDocument;
        var history = [], future = [];
        var api = { usesNative: false };

        function fireInput() {
            var view = doc.defaultView || {};
            var Ev = view.Event || (typeof Event !== 'undefined' ? Event : null);
            if (Ev) { ta.dispatchEvent(new Ev('input', { bubbles: true })); }
        }

        function nativeApply(d, newText) {
            if (typeof doc.execCommand !== 'function') { return false; }
            var before = ta.value;
            var previous = doc.activeElement;
            var ok = false;
            try {
                ta.focus({ preventScroll: true });
                if (doc.activeElement !== ta) { return false; } // not focusable (hidden): the command would edit whatever else has focus
                ta.setSelectionRange(d.start, d.endOld);
                ok = d.replacement === '' ? doc.execCommand('delete') : doc.execCommand('insertText', false, d.replacement);
            } catch (e) {
                ok = false;
            }

            if (!ok || ta.value !== newText) {
                if (ta.value !== before) { ta.value = before; } // a half-done native edit must not survive
                return false;
            }

            if (previous && previous !== ta && typeof previous.focus === 'function') { previous.focus({ preventScroll: true }); }
            return true;
        }

        api.apply = function (newText, caret) {
            var old = ta.value;
            if (old === newText) { return false; }
            var d = diff(old, newText);
            if (nativeApply(d, newText)) {
                api.usesNative = true;
            } else {
                api.usesNative = false;
                history.push({ text: old, start: ta.selectionStart, end: ta.selectionEnd });
                future.length = 0;
                ta.value = newText;
                fireInput();
            }

            var at = typeof caret === 'number' ? caret : d.start + d.replacement.length;
            try { ta.setSelectionRange(at, at); } catch (e) { /* not focusable here */ }
            return true;
        };

        function swap(from, to) {
            if (from.length === 0) { return false; }
            var entry = from.pop();
            to.push({ text: ta.value, start: ta.selectionStart, end: ta.selectionEnd });
            ta.value = entry.text;
            try { ta.setSelectionRange(entry.start, entry.end); } catch (e) { /* ignore */ }
            fireInput();
            return true;
        }

        function nativeStep(name) {
            if (typeof doc.execCommand !== 'function') { return false; }
            var before = ta.value;
            try {
                ta.focus({ preventScroll: true });
                if (doc.activeElement !== ta) { return false; }
                doc.execCommand(name);
            } catch (e) {
                return false;
            }

            return ta.value !== before;
        }

        // our own steps first (they are the most recent programmatic edits when the browser would not do it natively), then the browser's
        api.undo = function () { return swap(history, future) || nativeStep('undo'); };
        api.redo = function () { return swap(future, history) || nativeStep('redo'); };
        api.canUndo = function () {
            if (history.length > 0) { return true; }
            try { return typeof doc.queryCommandEnabled === 'function' && doc.queryCommandEnabled('undo'); } catch (e) { return false; }
        };

        api.reset = function () { history.length = 0; future.length = 0; };
        return api;
    }

    return { create: create, diff: diff };
});
