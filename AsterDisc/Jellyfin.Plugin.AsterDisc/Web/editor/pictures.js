/*
 * Small pure helpers for the Menu Editor's picture and sound picker: reading TMDB's listing, building asset addresses, and checking a file
 * before it is uploaded (the server checks again; this only saves a round trip and gives a friendlier message).
 *
 *   tmdbPath(url)            -> "/abc123.jpg" for "https://image.tmdb.org/t/p/original/abc123.jpg", else null
 *   tmdbThumb(url)           -> the address if it is a TMDB image address (https, image.tmdb.org, no odd characters), else null
 *   assetRoute(ref)          -> "DiscMenus/Assets/folder/name" for "asset:folder/name", else null
 *   kindOfName(name)         -> "image" | "audio" | null
 *   precheck(file, kind)     -> null if the file looks uploadable, else a sentence saying why not (file: { name, size })
 *   accept(kind)             -> the value for a file input's accept attribute
 *   size(bytes)              -> "1.4 MB"
 *
 * Works in a browser (global DiscMenusPictures) and in Node (module.exports).
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.DiscMenusPictures = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    var IMAGE = ['png', 'jpg', 'jpeg', 'webp'];
    var AUDIO = ['mp3', 'ogg', 'opus', 'm4a', 'wav'];
    var MAX = { image: 5 * 1024 * 1024, audio: 25 * 1024 * 1024 };
    var PART = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

    function extension(name) {
        var m = /\.([A-Za-z0-9]+)$/.exec(String(name || ''));
        return m ? m[1].toLowerCase() : '';
    }

    function kindOfName(name) {
        var e = extension(name);
        return IMAGE.indexOf(e) >= 0 ? 'image' : AUDIO.indexOf(e) >= 0 ? 'audio' : null;
    }

    function tmdbPath(url) {
        var m = /^https:\/\/image\.tmdb\.org\/t\/p\/[A-Za-z0-9]+(\/[A-Za-z0-9_-]+\.(?:jpg|png))$/.exec(String(url || ''));
        return m ? m[1] : null;
    }

    function tmdbThumb(url) {
        return /^https:\/\/image\.tmdb\.org\/t\/p\/[A-Za-z0-9]+\/[A-Za-z0-9_-]+\.(?:jpg|png)$/.test(String(url || '')) ? url : null;
    }

    function assetRoute(ref) {
        var m = /^asset:([A-Za-z0-9][A-Za-z0-9._-]{0,63})\/([A-Za-z0-9][A-Za-z0-9._-]{0,63})$/.exec(String(ref || ''));
        return m && PART.test(m[1]) && PART.test(m[2]) ? 'DiscMenus/Assets/' + m[1] + '/' + m[2] : null;
    }

    function size(bytes) {
        if (!(bytes >= 0)) {
            return '';
        }

        return bytes < 1024 * 1024 ? Math.max(1, Math.round(bytes / 1024)) + ' KB' : (Math.round(bytes / 1024 / 102.4) / 10) + ' MB';
    }

    function accept(kind) {
        var list = kind === 'audio' ? AUDIO : IMAGE;
        return list.map(function (e) { return '.' + e; }).join(',');
    }

    function precheck(file, kind) {
        if (!file || typeof file.name !== 'string') {
            return 'Choose a file.';
        }

        var found = kindOfName(file.name);
        if (!found) {
            return 'That kind of file can\'t be used. Pictures: png, jpg, webp. Sounds: mp3, ogg, opus, m4a, wav.';
        }

        if (kind && found !== kind) {
            return kind === 'image' ? 'This needs a picture (png, jpg or webp), not a sound.' : 'This needs a sound (mp3, ogg, opus, m4a or wav), not a picture.';
        }

        if (file.size === 0) {
            return 'That file is empty.';
        }

        if (file.size > MAX[found]) {
            return 'That ' + found + ' is ' + size(file.size) + '; the limit is ' + size(MAX[found]) + '.';
        }

        return null;
    }

    return { tmdbPath: tmdbPath, tmdbThumb: tmdbThumb, assetRoute: assetRoute, kindOfName: kindOfName, precheck: precheck, accept: accept, size: size };
});
