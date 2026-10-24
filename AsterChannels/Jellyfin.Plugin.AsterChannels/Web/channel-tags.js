/*
 * channel-tags.js: reading and writing Aster's channel tags, a port of the
 * app's ChannelTags.swift.
 *
 * A channel is a Jellyfin playlist tagged aster:channel; its settings are
 * more aster: tags on the same playlist (docs/channel-tags.md in the Aster
 * repo lists them all). The tags are the one place a channel lives, so this
 * page, the app and anyone tagging by hand all edit the same thing.
 *
 * It must read and write exactly as the app does: tests/conformance.test.js
 * runs it against the app's own channel-tags.json vectors.
 *
 * Works in the dashboard (window.AsterChannelTags) and in Node (require).
 */
(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.AsterChannelTags = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    var PREFIX = 'aster:';
    // What the tags began with when the app was called AsterFIN; still read.
    var OLD_PREFIX = 'asterfin:';
    // The keys this reads and writes; other aster: tags are kept as they are.
    var KEYS = ['channel', 'number', 'mode', 'logo', 'reshuffle', 'seed', 'blocks', 'bug', 'bug-location', 'bug-size', 'bug-hide',
        'bug-opacity', 'genre', 'type', 'library', 'network', 'collection', 'years', 'rating', 'weight', 'min-length', 'max-length',
        'filler-playlists', 'filler-playlist', 'filler-types', 'filler-type', 'bumpers-at', 'promo-count', 'trailer-count',
        'interstitial-count', 'break-length', 'break-every', 'align', 'align-fit', 'user', 'users', 'channel-name', 'group'];

    var MODES = ['rotation', 'blocks', 'shuffle', 'sequential', 'airdate'];
    var RESHUFFLES = ['day', 'week', 'month'];
    var CONTENTS = ['all', 'movies', 'shows'];
    var CORNERS = ['top-left', 'top-right', 'bottom-left', 'bottom-right'];
    // In the app's order, which is the order filler-types= is written in.
    var BREAK_KINDS = ['bumpers', 'promos', 'commercials', 'trailers', 'interstitials'];
    var BUMPER_PLACEMENTS = ['both', 'out', 'in'];
    var ALIGNMENTS = [5, 10, 15, 20, 30, 60];

    var LIMITS = {
        mostPromos: 5,
        longestBreak: 30,
        longestBreakGap: 10,
        heaviestWeight: 5,
        lengths: [1, 600],
        longestBlock: 20,
        bugSizes: [25, 300],
        defaultBugOpacity: 45,
        bugOpacities: [5, 100],
        bugHideTimes: [1, 3600],
        defaultBreakMinutes: 2,
        defaultBlocks: [2, 4]
    };

    // ---- small helpers, each matching its Swift counterpart ----------------

    // Swift's trimmingCharacters(in: .whitespaces): spaces and tabs, not new lines.
    function trim(text) {
        return String(text).replace(/^[^\S\r\n]+|[^\S\r\n]+$/g, '');
    }

    // Swift's Int(_:): digits with an optional sign, nothing else.
    function toInt(text) {
        if (text === null || text === undefined || !/^[+-]?\d+$/.test(text)) {
            return null;
        }
        return parseInt(text, 10);
    }

    function inRange(value, range) {
        return value !== null && value >= range[0] && value <= range[1] ? value : null;
    }

    function trimChars(text, chars) {
        var start = 0;
        var end = text.length;
        while (start < end && chars.indexOf(text[start]) >= 0) {
            start++;
        }
        while (end > start && chars.indexOf(text[end - 1]) >= 0) {
            end--;
        }
        return text.slice(start, end);
    }

    function sameName(a, b) {
        return a.toLowerCase() === b.toLowerCase();
    }

    function isOff(value) {
        return ['off', 'no', 'false', '0'].indexOf(value.toLowerCase()) >= 0;
    }

    function oneOf(value, choices, fallback) {
        if (value === null) {
            return fallback;
        }
        var lowered = value.toLowerCase();
        return choices.indexOf(lowered) >= 0 ? lowered : fallback;
    }

    // "aster:Number=101" (or "asterfin:Number=101") → {key: "number", value: "101"};
    // null for a tag that isn't Aster's.
    function parse(tag) {
        var trimmed = trim(tag);
        var lowered = trimmed.toLowerCase();
        var start = [PREFIX, OLD_PREFIX].filter(function (p) { return lowered.indexOf(p) === 0; })[0];
        if (!start) {
            return null;
        }
        // As Swift's split(separator: "=", maxSplits: 1): empty pieces (leading
        // "="s, or nothing after the "=") are left out and don't count.
        var body = trimmed.slice(start.length).replace(/^=+/, '');
        var equals = body.indexOf('=');
        var key = trim(equals < 0 ? body : body.slice(0, equals)).toLowerCase();
        var rest = equals < 0 ? '' : body.slice(equals + 1);
        if (!key) {
            return null;
        }
        return { key: key, value: rest === '' ? null : trim(rest) };
    }

    // "Horror, Thriller" into the list, once each whatever the case; with
    // splitting off, the whole value is one name.
    function collect(value, list, splitting) {
        var pieces = splitting === false ? [value || ''] : (value || '').split(',');
        pieces.forEach(function (piece) {
            var name = trim(piece);
            if (name && !list.some(function (existing) { return sameName(existing, name); })) {
                list.push(name);
            }
        });
    }

    // "30", "30m" or ":30" → 30; "off" → 0; null for anything else.
    function minutes(value) {
        var text = trim(value.toLowerCase());
        if (isOff(text)) {
            return 0;
        }
        if (text[0] === ':') {
            text = text.slice(1);
        }
        var units = ['minutes', 'minute', 'mins', 'min', 'm'];
        for (var i = 0; i < units.length; i++) {
            if (text.length >= units[i].length && text.slice(-units[i].length) === units[i]) {
                text = trim(text.slice(0, -units[i].length));
                break;
            }
        }
        return toInt(text);
    }

    // "30", "30s" → 30; null for "off" or anything else.
    function seconds(value) {
        return toInt(trimChars(value.toLowerCase(), 's '));
    }

    // "150%" or "150" → 150.
    function percent(value) {
        return toInt(trimChars(value, '% '));
    }

    function range(value) {
        var parts = value.split('-').map(function (part) { return toInt(trim(part)); });
        if (parts.length < 1 || parts.length > 2 || parts[0] === null || parts[parts.length - 1] === null) {
            return null;
        }
        return [parts[0], parts[parts.length - 1]];
    }

    // "1985-1999" → [1985, 1999] (either way round), "1994" → [1994, 1994].
    function yearRange(value) {
        var parts = range(value);
        if (!parts) {
            return null;
        }
        var low = Math.min(parts[0], parts[1]);
        var high = Math.max(parts[0], parts[1]);
        return low >= 1880 && high <= 2200 ? [low, high] : null;
    }

    // "3-6" → [3, 6], "4" → [4, 4]; null unless it's 1 to longestBlock, low to high.
    function blockRange(value) {
        var parts = range(value);
        if (!parts || parts[0] < 1 || parts[0] > parts[1] || parts[1] > LIMITS.longestBlock) {
            return null;
        }
        return parts;
    }

    // "Friends:2" → {show: "Friends", weight: 2}; over 5 counts as 5.
    function showWeight(value) {
        var colon = value.lastIndexOf(':');
        if (colon < 0) {
            return null;
        }
        var show = trim(value.slice(0, colon));
        var weight = toInt(trim(value.slice(colon + 1)));
        if (!show || weight === null || weight < 1) {
            return null;
        }
        return { show: show, weight: Math.min(weight, LIMITS.heaviestWeight) };
    }

    // "bumper" or "bumpers", "commercial" or "commercials" and so on, as a kind.
    function kind(word) {
        var lowered = word.toLowerCase();
        if (BREAK_KINDS.indexOf(lowered) >= 0) {
            return lowered;
        }
        return BREAK_KINDS.indexOf(lowered + 's') >= 0 ? lowered + 's' : null;
    }

    // ---- the settings ------------------------------------------------------

    // Every setting at its default: what a playlist without tags has.
    function defaults() {
        return {
            isChannel: false,
            number: null,
            mode: 'rotation',
            showsLogo: false,
            reshuffle: 'day',
            seed: 0,
            blocks: LIMITS.defaultBlocks.slice(),
            showsBug: false,
            bugCorner: 'bottom-right',
            bugSize: 100,
            bugHideAfter: 0,
            bugOpacity: LIMITS.defaultBugOpacity,
            genres: [],
            content: 'all',
            libraries: [],
            networks: [],
            collections: [],
            years: null,
            ratings: [],
            weights: [],
            minLength: null,
            maxLength: null,
            fillerPlaylists: [],
            fillerTypes: BREAK_KINDS.slice(),
            breaks: {
                bumpersAt: 'both',
                promoCount: 1,
                trailerCount: 1,
                interstitialCount: 1,
                minutes: LIMITS.defaultBreakMinutes,
                every: 1,
                align: 0,
                exactAlign: false
            },
            users: [],
            name: null,
            // The guide's group for the channel; null for none.
            group: null
        };
    }

    function read(tags) {
        var s = defaults();
        (tags || []).forEach(function (tag) {
            var parsed = parse(tag);
            if (!parsed) {
                return;
            }
            var value = parsed.value;
            var count = function () { return inRange(value === null ? null : toInt(value), [0, LIMITS.mostPromos]); };
            switch (parsed.key) {
                case 'channel':
                    s.isChannel = value === null ? true : !isOff(value);
                    break;
                case 'number':
                    var number = toInt(value);
                    if (number !== null && number > 0) {
                        s.number = number;
                    }
                    break;
                case 'mode':
                    s.mode = oneOf(value, MODES, 'rotation');
                    break;
                case 'logo':
                    s.showsLogo = value === null ? true : !isOff(value);
                    break;
                case 'reshuffle':
                    s.reshuffle = oneOf(value, RESHUFFLES, 'day');
                    break;
                case 'seed':
                    var seed = toInt(value);
                    s.seed = seed === null ? 0 : Math.max(seed, 0);
                    break;
                case 'blocks':
                    s.blocks = (value !== null && blockRange(value)) || LIMITS.defaultBlocks.slice();
                    break;
                case 'bug':
                    s.showsBug = value === null ? true : !isOff(value);
                    break;
                case 'bug-location':
                    s.bugCorner = oneOf(value, CORNERS, 'bottom-right');
                    break;
                case 'bug-size':
                    var size = value === null ? null : inRange(percent(value), LIMITS.bugSizes);
                    s.bugSize = size === null ? 100 : size;
                    break;
                case 'bug-opacity':
                    var opacity = value === null ? null : inRange(percent(value), LIMITS.bugOpacities);
                    s.bugOpacity = opacity === null ? LIMITS.defaultBugOpacity : opacity;
                    break;
                case 'bug-hide':
                    var hide = value === null ? null : inRange(seconds(value), LIMITS.bugHideTimes);
                    s.bugHideAfter = hide === null ? 0 : hide;
                    break;
                case 'genre':
                    collect(value, s.genres);
                    break;
                case 'type':
                    s.content = oneOf(value, CONTENTS, 'all');
                    break;
                case 'library':
                    collect(value, s.libraries);
                    break;
                case 'network':
                    // Studio names can have commas: one per tag.
                    collect(value, s.networks, false);
                    break;
                case 'collection':
                    collect(value, s.collections, false);
                    break;
                case 'years':
                    s.years = value === null ? null : yearRange(value);
                    break;
                case 'rating':
                    collect(value, s.ratings);
                    break;
                case 'weight':
                    var weight = value === null ? null : showWeight(value);
                    if (weight) {
                        s.weights = s.weights.filter(function (w) { return !sameName(w.show, weight.show); });
                        s.weights.push(weight);
                    }
                    break;
                case 'min-length':
                    s.minLength = inRange(toInt(value), LIMITS.lengths);
                    break;
                case 'max-length':
                    s.maxLength = inRange(toInt(value), LIMITS.lengths);
                    break;
                case 'filler-playlists':
                case 'filler-playlist':
                    collect(value, s.fillerPlaylists, false);
                    break;
                case 'filler-types':
                case 'filler-type':
                    // Kinds it doesn't know are ignored; none it does (or "all") means all.
                    var kinds = [];
                    (value || '').split(',').forEach(function (word) {
                        var k = kind(trim(word));
                        if (k && kinds.indexOf(k) < 0) {
                            kinds.push(k);
                        }
                    });
                    s.fillerTypes = kinds.length ? BREAK_KINDS.filter(function (k) { return kinds.indexOf(k) >= 0; }) : BREAK_KINDS.slice();
                    break;
                case 'bumpers-at':
                    s.breaks.bumpersAt = oneOf(value, BUMPER_PLACEMENTS, 'both');
                    break;
                case 'promo-count':
                    s.breaks.promoCount = count() === null ? 1 : count();
                    break;
                case 'trailer-count':
                    s.breaks.trailerCount = count() === null ? 1 : count();
                    break;
                case 'interstitial-count':
                    s.breaks.interstitialCount = count() === null ? 1 : count();
                    break;
                case 'break-length':
                    var length = value === null ? null : inRange(minutes(value), [0, LIMITS.longestBreak]);
                    s.breaks.minutes = length === null ? LIMITS.defaultBreakMinutes : length;
                    break;
                case 'break-every':
                    var every = inRange(toInt(value), [1, LIMITS.longestBreakGap]);
                    s.breaks.every = every === null ? 1 : every;
                    break;
                case 'align-fit':
                    s.breaks.exactAlign = value !== null && value.toLowerCase() === 'exact';
                    break;
                case 'align':
                    var align = value === null ? null : minutes(value);
                    s.breaks.align = ALIGNMENTS.indexOf(align) >= 0 ? align : 0;
                    break;
                case 'user':
                case 'users':
                    collect(value, s.users);
                    break;
                case 'channel-name':
                    s.name = value ? value : null;
                    break;
                case 'group':
                    s.group = value && value.trim() ? value.trim() : null;
                    break;
                default:
                    break;
            }
        });
        return s;
    }

    function hasBreaks(s) {
        return s.fillerPlaylists.length > 0 && s.fillerTypes.length > 0;
    }

    function hasSources(s) {
        return s.genres.length > 0 || s.networks.length > 0 || s.collections.length > 0;
    }

    // tags with Aster's own replaced by every one of these settings, defaults
    // included; every other tag is kept, in its place. A playlist that stops
    // being a channel is marked channel=off and keeps the rest.
    function applied(s, tags) {
        var result = (tags || []).filter(function (tag) {
            var parsed = parse(tag);
            return !parsed || KEYS.indexOf(parsed.key) < 0;
        });
        var add = function (body) { result.push(PREFIX + body); };
        var on = function (flag) { return flag ? 'on' : 'off'; };
        var span = function (pair) { return pair[0] === pair[1] ? String(pair[0]) : pair[0] + '-' + pair[1]; };
        add(s.isChannel ? 'channel' : 'channel=off');
        if (s.number !== null) {
            add('number=' + s.number);
        }
        add('mode=' + s.mode);
        add('reshuffle=' + s.reshuffle);
        add('blocks=' + span(s.blocks));
        add('seed=' + s.seed);
        add('logo=' + on(s.showsLogo));
        add('bug=' + on(s.showsBug));
        add('bug-location=' + s.bugCorner);
        add('bug-size=' + s.bugSize + '%');
        add(s.bugHideAfter > 0 ? 'bug-hide=' + s.bugHideAfter : 'bug-hide=off');
        add('bug-opacity=' + s.bugOpacity + '%');
        s.genres.forEach(function (g) { add('genre=' + g); });
        add('type=' + s.content);
        s.libraries.forEach(function (l) { add('library=' + l); });
        s.networks.forEach(function (n) { add('network=' + n); });
        s.collections.forEach(function (c) { add('collection=' + c); });
        if (s.years) {
            add('years=' + span(s.years));
        }
        s.ratings.forEach(function (r) { add('rating=' + r); });
        s.weights.forEach(function (w) { add('weight=' + w.show + ':' + w.weight); });
        if (s.minLength !== null) {
            add('min-length=' + s.minLength);
        }
        if (s.maxLength !== null) {
            add('max-length=' + s.maxLength);
        }
        // The break settings only mean something with clips to play.
        if (hasBreaks(s)) {
            s.fillerPlaylists.forEach(function (f) { add('filler-playlists=' + f); });
            var types = BREAK_KINDS.filter(function (k) { return s.fillerTypes.indexOf(k) >= 0; });
            add('filler-types=' + (types.length === BREAK_KINDS.length ? 'all' : types.join(',')));
            add('bumpers-at=' + s.breaks.bumpersAt);
            add('break-length=' + s.breaks.minutes);
            add('promo-count=' + s.breaks.promoCount);
            add('trailer-count=' + s.breaks.trailerCount);
            add('interstitial-count=' + s.breaks.interstitialCount);
            add('break-every=' + s.breaks.every);
            add(s.breaks.align > 0 ? 'align=' + s.breaks.align : 'align=off');
            add('align-fit=' + (s.breaks.exactAlign ? 'exact' : 'best'));
        }
        if (s.users.length) {
            add('user=' + s.users.join(','));
        }
        if (s.name !== null) {
            add('channel-name=' + s.name);
        }
        if (s.group !== null) {
            add('group=' + s.group);
        }
        return result;
    }

    // Whether a playlist's tags make it a filler playlist (aster:filler).
    function isFillerPlaylist(tags) {
        return (tags || []).some(function (tag) {
            var parsed = parse(tag);
            return parsed && parsed.key === 'filler' && (parsed.value === null || !isOff(parsed.value));
        });
    }

    // Whether tags set key at all, as opposed to leaving it to its default.
    function sets(key, tags) {
        return (tags || []).some(function (tag) {
            var parsed = parse(tag);
            return parsed && parsed.key === key;
        });
    }

    // Channel numbers: their own where they have one, then the rest after the
    // highest, in name order. channels: [{id, name, number}] → {id: number}.
    function numbers(channels) {
        var result = {};
        var next = channels.reduce(function (high, c) { return c.number !== null && c.number > high ? c.number : high; }, 0) + 1;
        channels.forEach(function (c) {
            if (c.number !== null) {
                result[c.id] = c.number;
            }
        });
        channels.filter(function (c) { return c.number === null; })
            .sort(function (a, b) { return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }); })
            .forEach(function (c) { result[c.id] = next++; });
        return result;
    }

    return {
        PREFIX: PREFIX,
        MODES: MODES,
        RESHUFFLES: RESHUFFLES,
        CONTENTS: CONTENTS,
        CORNERS: CORNERS,
        BREAK_KINDS: BREAK_KINDS,
        BUMPER_PLACEMENTS: BUMPER_PLACEMENTS,
        ALIGNMENTS: ALIGNMENTS,
        LIMITS: LIMITS,
        defaults: defaults,
        read: read,
        applied: applied,
        hasBreaks: hasBreaks,
        hasSources: hasSources,
        isFillerPlaylist: isFillerPlaylist,
        sets: sets,
        numbers: numbers
    };
});
