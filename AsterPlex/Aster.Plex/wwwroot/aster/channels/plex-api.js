/*
 * plex-api.js: Jellyfin's ApiClient, as the Channels page (channels.js) uses it, answered from Plex.
 *
 * The page was written against Jellyfin's API; the Aster apps answer the same calls from Plex when signed in to a Plex
 * server (PlexApiClient.kt), and this does the same in the browser, through the container's /aster/plex/…, which goes
 * to Plex as whoever's signed in to the page. Plex has no tags on playlists, so a channel's are kept in its
 * description, as the apps keep them (ChannelTagParsing.fromDescription and inDescription, ported below).
 */
(function () {
    'use strict';

    var PLEX = '/aster/plex';

    // ---- Tags in a description (ChannelTags.kt) ----

    // "aster:" (or "asterfin:") starting a line, or after a space or comma.
    var ENTRY_START = /(?<![^\s,])aster(?:fin)?:/i;
    // A comma before the next "aster:".
    var ENTRY_BREAK = /,\s*(?=aster(?:fin)?:)/i;

    // The tags kept in a description: "aster:channel, aster:number=7" as two. Only a comma before the next "aster:"
    // parts them, so a comma inside a value stays in it. Text before a line's first one is the description's own.
    function fromDescription(text) {
        if (!text || !text.trim()) {
            return [];
        }
        var tags = [];
        text.split('\n').forEach(function (line) {
            var start = line.search(ENTRY_START);
            if (start < 0) {
                return;
            }
            line.substring(start).split(ENTRY_BREAK).forEach(function (t) {
                t = t.trim();
                if (t) {
                    tags.push(t);
                }
            });
        });
        return tags;
    }

    // The description with its tags replaced, on a line of their own after its own words, which are kept.
    function inDescription(description, tags) {
        var own = [];
        (description || '').replace(/\r/g, '').split('\n').forEach(function (line) {
            var start = line.search(ENTRY_START);
            var kept = start >= 0 ? line.substring(0, start).replace(/[ ,]+$/, '') : line;
            if (kept.length > 0 || start < 0) {
                own.push(kept);
            }
        });
        while (own.length && !own[own.length - 1].trim()) {
            own.pop();
        }
        var entries = tags.map(function (t) { return t.trim(); }).filter(Boolean).join(', ');
        if (!entries) {
            return own.join('\n');
        }
        return own.length ? own.join('\n') + '\n\n' + entries : entries;
    }

    // ---- Plex ----

    function plex(method, path, query) {
        var url = PLEX + path + (query ? '?' + new URLSearchParams(query).toString() : '');
        return fetch(url, { method: method, credentials: 'same-origin', headers: { Accept: 'application/json' } }).then(function (r) {
            if (!r.ok) {
                var error = new Error('Plex answered ' + r.status);
                error.status = r.status;
                throw error;
            }
            return r.text().then(function (text) {
                return text ? (JSON.parse(text).MediaContainer || {}) : {};
            });
        });
    }

    function metadata(container) {
        return container.Metadata || [];
    }

    var sectionsOnce = null;

    // The movie and show libraries, which channels play from.
    function sections() {
        sectionsOnce = sectionsOnce || plex('GET', '/library/sections').then(function (c) {
            return (c.Directory || []).filter(function (d) { return d.type === 'movie' || d.type === 'show'; });
        });
        return sectionsOnce;
    }

    var machineOnce = null;

    function machine() {
        machineOnce = machineOnce || plex('GET', '/identity').then(function (c) { return c.machineIdentifier; });
        return machineOnce;
    }

    // A list of each library's genres, studios, networks or ratings: names, once each.
    function directoryNames(kindFor) {
        return sections().then(function (list) {
            return Promise.all(list.map(function (s) {
                var kinds = kindFor(s);
                return Promise.all(kinds.map(function (kind) {
                    return plex('GET', '/library/sections/' + s.key + '/' + kind).then(function (c) {
                        return (c.Directory || []).map(function (d) { return d.title; });
                    }, function () { return []; });
                }));
            }));
        }).then(function (nested) {
            var seen = {};
            var names = [];
            nested.forEach(function (bySection) {
                bySection.forEach(function (list) {
                    list.forEach(function (name) {
                        if (name && !seen[name.toLowerCase()]) {
                            seen[name.toLowerCase()] = true;
                            names.push(name);
                        }
                    });
                });
            });
            return names.sort(function (a, b) { return a.localeCompare(b, undefined, { sensitivity: 'base' }); });
        });
    }

    function items(list) {
        return { Items: list, TotalRecordCount: list.length };
    }

    // A Plex playlist as Jellyfin's: its tags read from its description.
    function playlist(m) {
        return {
            Id: String(m.ratingKey),
            Name: m.title || '',
            Type: 'Playlist',
            Tags: fromDescription(m.summary),
            ChildCount: m.leafCount || 0,
            ImageTags: m.composite || m.thumb ? { Primary: 'plex' } : {},
            Overview: m.summary || ''
        };
    }

    // ---- The calls channels.js makes ----

    function getJSON(url) {
        var parsed = new URL(url, location.origin);
        var path = parsed.pathname.replace(/^\/jf\//, '');
        var q = {};
        parsed.searchParams.forEach(function (v, k) { q[k.toLowerCase()] = v; });
        var types = (q.includeitemtypes || '').split(',');

        // The person's video playlists.
        if (path === 'Items' && types.indexOf('Playlist') >= 0) {
            return plex('GET', '/playlists', { playlistType: 'video' }).then(function (c) {
                return items(metadata(c).filter(function (m) { return m.playlistType === 'video'; }).map(playlist)
                    .sort(function (a, b) { return a.Name.localeCompare(b.Name, undefined, { sensitivity: 'base' }); }));
            });
        }

        // Every show, for New Channel from a show.
        if (path === 'Items' && types.indexOf('Series') >= 0 && types.length === 1) {
            return sections().then(function (list) {
                return Promise.all(list.filter(function (s) { return s.type === 'show'; }).map(function (s) {
                    return plex('GET', '/library/sections/' + s.key + '/all', { type: '2' }).then(metadata, function () { return []; });
                }));
            }).then(function (nested) {
                return items([].concat.apply([], nested).map(function (m) { return { Id: String(m.ratingKey), Name: m.title }; }));
            });
        }

        // A show's episodes, for its playlist.
        if (path === 'Items' && types.indexOf('Episode') >= 0 && q.parentid) {
            return plex('GET', '/library/metadata/' + encodeURIComponent(q.parentid) + '/allLeaves').then(function (c) {
                return items(metadata(c).map(function (m) { return { Id: String(m.ratingKey) }; }));
            });
        }

        // The ratings titles carry, for keeping a channel to some.
        if (path === 'Items' && (q.fields || '').indexOf('OfficialRating') >= 0) {
            return directoryNames(function () { return ['contentRating']; }).then(function (names) {
                return items(names.map(function (n) { return { OfficialRating: n }; }));
            });
        }

        if (path === 'Genres') {
            return directoryNames(function () { return ['genre']; }).then(function (names) {
                return items(names.map(function (n) { return { Name: n }; }));
            });
        }

        // A show's network and a film's studio, which Jellyfin keeps under one name.
        if (path === 'Studios') {
            return directoryNames(function (s) { return s.type === 'show' ? ['network', 'studio'] : ['studio']; }).then(function (names) {
                return items(names.map(function (n) { return { Name: n }; }));
            });
        }

        if (/^Users\/[^/]+\/Items$/.test(path) && types.indexOf('BoxSet') >= 0) {
            return sections().then(function (list) {
                return Promise.all(list.map(function (s) {
                    return plex('GET', '/library/sections/' + s.key + '/collections').then(metadata, function () { return []; });
                }));
            }).then(function (nested) {
                return items([].concat.apply([], nested).map(function (m) { return { Name: m.title, ChildCount: m.childCount || 0 }; }));
            });
        }

        if (path === 'UserViews') {
            return sections().then(function (list) {
                return items(list.map(function (s) { return { Name: s.title, CollectionType: s.type === 'show' ? 'tvshows' : 'movies' }; }));
            });
        }

        if (path === 'Users') {
            return fetch('/aster/api/names', { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : []; }).then(function (names) {
                return names.map(function (n) { return { Name: n }; });
            });
        }

        // One playlist, to change its tags.
        var one = /^Users\/[^/]+\/Items\/([^/]+)$/.exec(path);
        if (one) {
            return plex('GET', '/playlists/' + encodeURIComponent(one[1])).then(function (c) {
                var m = metadata(c)[0];
                if (!m) {
                    var error = new Error('That playlist is gone.');
                    error.status = 404;
                    throw error;
                }
                return playlist(m);
            });
        }

        // Plex's Live TV isn't Aster's to group: the page's Live TV section stays empty.
        if (path === 'LiveTv/Channels') {
            return Promise.resolve(items([]));
        }

        var unknown = new Error('Not on Plex: ' + path);
        unknown.status = 404;
        return Promise.reject(unknown);
    }

    function ajax(request) {
        var parsed = new URL(request.url, location.origin);
        var path = parsed.pathname.replace(/^\/jf\//, '');
        var body = request.data ? JSON.parse(request.data) : {};

        // A playlist's tags, written into its description, keeping the description's own words.
        var save = /^Items\/([^/]+)$/.exec(path);
        if (request.type === 'POST' && save) {
            return plex('PUT', '/playlists/' + encodeURIComponent(save[1]), { summary: inDescription(body.Overview, body.Tags || []) });
        }

        // A new playlist, the person's own: a show's episodes, or empty for a genre's, network's or collection's channel.
        if (request.type === 'POST' && path === 'Playlists') {
            return machine().then(function (id) {
                var ids = body.Ids || [];
                return plex('POST', '/playlists', {
                    type: 'video',
                    title: body.Name,
                    smart: '0',
                    uri: ids.length ? 'server://' + id + '/com.plexapp.plugins.library/library/metadata/' + ids.join(',') : ''
                });
            }).then(function (c) {
                var made = metadata(c)[0];
                return { Id: made ? String(made.ratingKey) : '' };
            });
        }

        var unknown = new Error('Not on Plex: ' + path);
        unknown.status = 404;
        return Promise.reject(unknown);
    }

    window.ApiClient = {
        getCurrentUserId: function () { return 'me'; },
        getUrl: function (path, query) {
            var url = '/jf/' + path;
            var params = new URLSearchParams();
            Object.keys(query || {}).forEach(function (k) {
                if (query[k] !== undefined && query[k] !== null) {
                    params.set(k, query[k]);
                }
            });
            var text = params.toString();
            return text ? url + '?' + text : url;
        },
        getJSON: getJSON,
        ajax: ajax
    };

    window.AsterPlexTags = { fromDescription: fromDescription, inDescription: inDescription };
})();
