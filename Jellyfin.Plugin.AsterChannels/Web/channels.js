/*
 * channels.js: the dashboard's Channels page.
 *
 * Lists the server's video playlists that are channels, edits their tags with
 * channel-tags.js (the same rules as the app), and makes new channels the way
 * the app's New Channel does. Every change is saved at once, as in the app:
 * the playlist is read, its Tags changed, and the whole record posted back,
 * with the signed-in administrator's own token.
 */
(function () {
    'use strict';

    var T = window.AsterChannelTags;

    // The fields an item update writes back, each asked for when reading the
    // item first, since one left out of the update would be cleared.
    var EDITABLE_FIELDS = 'Tags,Overview,Genres,Studios,People,ProviderIds,DateCreated,Taglines,SortName,PremiereDate,'
        + 'ProductionYear,OfficialRating,CustomRating,LockedFields,LockData,ChildCount';

    var MODE_TITLES = { rotation: 'Rotation', blocks: 'Blocks', shuffle: 'Shuffle', sequential: 'In Order', airdate: 'Air Date' };
    var MODE_HELP = {
        rotation: 'Shows take turns, one episode each.',
        blocks: 'Shows take turns, a few episodes in a row each.',
        shuffle: 'Every movie and episode on its own, in a new order each day.',
        sequential: "The playlist's own order, straight through and round again.",
        airdate: 'Everything in the order it first aired, across every show.'
    };
    var RESHUFFLE_TITLES = { day: 'Daily', week: 'Weekly', month: 'Monthly' };
    var CONTENT_TITLES = { all: 'Movies & Shows', movies: 'Movies', shows: 'Shows' };
    var CORNER_TITLES = { 'top-left': 'Top Left', 'top-right': 'Top Right', 'bottom-left': 'Bottom Left', 'bottom-right': 'Bottom Right' };
    var KIND_TITLES = { bumpers: 'Bumpers', promos: 'Promos', commercials: 'Commercials', trailers: 'Trailers', interstitials: 'Interstitials' };
    var BUMPER_TITLES = { both: 'Before & After', out: 'Before the Break', 'in': 'After the Break' };
    var SOURCE_KINDS = [
        { id: 'show', title: 'A Show', pick: 'Show' },
        { id: 'genre', title: 'A Genre', pick: 'Genre' },
        { id: 'network', title: 'A Network', pick: 'Network' },
        { id: 'collection', title: 'A Collection', pick: 'Collection' },
        { id: 'playlist', title: 'One of Your Playlists', pick: 'Playlist' }
    ];
    // Libraries a channel can't play from.
    var SKIPPED_LIBRARIES = ['music', 'books', 'livetv', 'playlists', 'boxsets', 'musicvideos'];

    // ---- DOM helpers ---------------------------------------------------------

    function h(tag, attrs, children) {
        var el = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (key) {
            var value = attrs[key];
            if (value === null || value === undefined || value === false) {
                return;
            }
            if (key.indexOf('on') === 0) {
                el.addEventListener(key.slice(2), value);
            } else if (key === 'text') {
                el.textContent = value;
            } else if (key === 'className') {
                el.className = value;
            } else if (value === true) {
                el.setAttribute(key, '');
            } else {
                el.setAttribute(key, value);
            }
        });
        (children || []).forEach(function (child) {
            if (child === null || child === undefined || child === false) {
                return;
            }
            el.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
        });
        return el;
    }

    function field(label, control, help) {
        return h('div', { className: 'acField' }, [
            h('label', { className: 'acFieldLabel', text: label }),
            control,
            help ? h('div', { className: 'acHelp', text: help }) : null
        ]);
    }

    function select(choices, value, onChange) {
        return h('select', { onchange: function (e) { onChange(e.target.value); } }, choices.map(function (choice) {
            return h('option', { value: String(choice[0]), selected: String(choice[0]) === String(value), text: choice[1] });
        }));
    }

    function checkbox(label, checked, onChange) {
        return h('label', { className: 'acCheck' }, [
            h('input', { type: 'checkbox', checked: checked, onchange: function (e) { onChange(e.target.checked); } }),
            h('span', { text: label })
        ]);
    }

    // A whole number in [min, max], or nothing (null) when emptied and allowed.
    function numberInput(value, min, max, placeholder, onChange, allowEmpty) {
        return h('input', {
            type: 'number', min: min, max: max, step: 1, placeholder: placeholder || null,
            value: value === null || value === undefined ? '' : String(value),
            onchange: function (e) {
                var text = e.target.value.trim();
                if (text === '') {
                    if (allowEmpty) {
                        onChange(null);
                    } else {
                        e.target.value = value === null ? '' : String(value);
                    }
                    return;
                }
                var number = Math.round(Number(text));
                if (!isFinite(number)) {
                    return;
                }
                onChange(Math.min(Math.max(number, min), max));
            }
        });
    }

    var listCount = 0;

    // Names as chips, each removable, with a box to add more: from the
    // suggestions, or (with free on) anything typed.
    function chips(values, suggestions, onChange, options) {
        options = options || {};
        var listId = 'acList' + (++listCount);
        var input = h('input', { type: 'text', list: listId, placeholder: options.placeholder || 'Add…' });

        function add() {
            var typed = input.value.trim();
            if (!typed) {
                return;
            }
            var match = suggestions.filter(function (s) { return s.toLowerCase() === typed.toLowerCase(); })[0];
            var name = match || (options.free ? typed : null);
            if (options.validate && name) {
                name = options.validate(name);
            }
            if (!name) {
                input.setCustomValidity(options.invalid || 'Pick one from the list.');
                input.reportValidity();
                return;
            }
            input.value = '';
            if (values.some(function (v) { return v.toLowerCase() === name.toLowerCase(); })) {
                return;
            }
            onChange(values.concat([name]));
        }

        input.addEventListener('input', function () { input.setCustomValidity(''); });
        input.addEventListener('change', add);
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                add();
            }
        });

        return h('div', {}, [
            h('div', { className: 'acChips' }, values.map(function (value) {
                return h('span', { className: 'acChip' }, [
                    value,
                    h('button', {
                        type: 'button', title: 'Remove', 'aria-label': 'Remove ' + value, text: '×',
                        onclick: function () { onChange(values.filter(function (v) { return v !== value; })); }
                    })
                ]);
            })),
            input,
            h('datalist', { id: listId }, suggestions.map(function (s) { return h('option', { value: s }); }))
        ]);
    }

    function button(label, onClick, primary) {
        return h('button', { is: 'emby-button', type: 'button', className: 'raised' + (primary ? ' button-submit' : ''), onclick: onClick }, [
            h('span', { text: label })
        ]);
    }

    function byName(a, b) {
        return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
    }

    // ---- the page --------------------------------------------------------------

    function create(page) {
        var el = {
            status: page.querySelector('#acStatus'),
            list: page.querySelector('#acList'),
            others: page.querySelector('#acOthers'),
            othersTitle: page.querySelector('#acOthersTitle'),
            editor: page.querySelector('#acEditor'),
            newPanel: page.querySelector('#acNewPanel')
        };

        var state = {
            playlists: [],
            options: { genres: [], networks: [], collections: [], libraries: [], ratings: [], users: [], series: [] },
            selected: null,
            // Folds left open, by "fold|playlist id", so a change doesn't close them.
            folds: {},
            loading: null,
            saving: Promise.resolve(),
            pendingSaves: 0,
            creating: false,
            drag: null
        };

        function userId() {
            return ApiClient.getCurrentUserId();
        }

        function getJSON(path, query) {
            return ApiClient.getJSON(ApiClient.getUrl(path, query || {}));
        }

        function postJSON(path, body, wantsReply) {
            var request = { type: 'POST', url: ApiClient.getUrl(path), data: JSON.stringify(body), contentType: 'application/json' };
            if (wantsReply) {
                request.dataType = 'json';
            }
            return ApiClient.ajax(request);
        }

        function setStatus(text, kind) {
            el.status.textContent = text || '';
            el.status.className = 'acStatus' + (kind ? ' ' + kind : '');
        }

        function failure(error, what) {
            var detail = error && (error.message || error.statusText || (error.status ? 'HTTP ' + error.status : '')) || 'unknown error';
            if (error && (error.status === 401 || error.status === 403)) {
                detail = 'only an administrator can change playlists';
            }
            setStatus(what + ': ' + detail, 'error');
        }

        // Every item a query finds, 500 at a time.
        function allItems(query) {
            var found = [];
            function next() {
                var page = Object.assign({}, query, { startIndex: found.length, limit: 500 });
                return getJSON('Items', page).then(function (result) {
                    var items = result.Items || [];
                    found = found.concat(items);
                    if (items.length < 500 || found.length >= (result.TotalRecordCount || 0)) {
                        return found;
                    }
                    return next();
                });
            }
            return next();
        }

        // ---- loading ----

        function loadPlaylists() {
            return getJSON('Items', {
                userId: userId(), includeItemTypes: 'Playlist', mediaTypes: 'Video', recursive: true,
                sortBy: 'SortName', sortOrder: 'Ascending', fields: 'Tags,ChildCount'
            }).then(function (result) {
                state.playlists = result.Items || [];
            });
        }

        function names(items) {
            return items.map(function (i) { return i.Name; }).filter(Boolean);
        }

        // The lists the editor picks from; any that fails is left empty.
        function loadOptions() {
            var user = userId();
            var o = state.options;
            var quiet = function (promise, use) { return promise.then(use, function () {}); };
            return Promise.all([
                quiet(getJSON('Genres', { userId: user, recursive: true, includeItemTypes: 'Movie,Series', sortBy: 'SortName' }),
                    function (r) { o.genres = names(r.Items || []); }),
                quiet(getJSON('Studios', { userId: user, recursive: true, includeItemTypes: 'Movie,Series', sortBy: 'SortName' }),
                    function (r) { o.networks = names(r.Items || []); }),
                quiet(getJSON('Users/' + user + '/Items', { includeItemTypes: 'BoxSet', recursive: true, sortBy: 'SortName', fields: 'ChildCount' }),
                    function (r) { o.collections = names((r.Items || []).filter(function (i) { return (i.ChildCount === undefined ? 1 : i.ChildCount) > 0; })); }),
                quiet(getJSON('UserViews', { userId: user }), function (r) {
                    o.libraries = names((r.Items || []).filter(function (i) {
                        return SKIPPED_LIBRARIES.indexOf((i.CollectionType || '').toLowerCase()) < 0;
                    }));
                }),
                quiet(getJSON('Items', {
                    userId: user, recursive: true, includeItemTypes: 'Movie,Series', fields: 'OfficialRating', enableImages: false, enableUserData: false
                }), function (r) {
                    var counts = {};
                    (r.Items || []).forEach(function (i) {
                        if (i.OfficialRating) {
                            counts[i.OfficialRating] = (counts[i.OfficialRating] || 0) + 1;
                        }
                    });
                    o.ratings = Object.keys(counts).sort(function (a, b) { return counts[b] - counts[a] || (a < b ? -1 : 1); });
                }),
                quiet(getJSON('Users'), function (r) { o.users = names(r || []).sort(byName); }),
                quiet(allItems({ userId: user, recursive: true, includeItemTypes: 'Series', sortBy: 'SortName', enableImages: false, enableUserData: false }),
                    function (items) { o.series = items.map(function (i) { return { id: i.Id, name: i.Name }; }); })
            ]);
        }

        function reload() {
            if (state.loading) {
                return state.loading;
            }
            setStatus('Loading…');
            state.loading = Promise.all([loadPlaylists(), loadOptions()]).then(function () {
                setStatus('');
                render();
            }, function (error) {
                failure(error, "Couldn't load the playlists");
            }).then(function () {
                state.loading = null;
            });
            return state.loading;
        }

        // ---- the channels ----

        function playlist(id) {
            return state.playlists.filter(function (p) { return p.Id === id; })[0];
        }

        function isChannelPlaylist(p) {
            return T.read(p.Tags).isChannel;
        }

        // Every playlist that's a channel, on or off, keeps its number; the
        // ones without get theirs after the highest, as in the app.
        function numbering() {
            return T.numbers(state.playlists.filter(function (p) { return T.sets('channel', p.Tags) && !T.isFillerPlaylist(p.Tags); }).map(function (p) {
                return { id: p.Id, name: p.Name, number: T.read(p.Tags).number };
            }));
        }

        function channels() {
            var numbers = numbering();
            return state.playlists.filter(isChannelPlaylist).map(function (p) {
                return { playlist: p, settings: T.read(p.Tags), number: numbers[p.Id] };
            }).sort(function (a, b) { return a.number - b.number || byName(a.playlist.Name, b.playlist.Name); });
        }

        function fillerNames() {
            return state.playlists.filter(function (p) { return T.isFillerPlaylist(p.Tags); }).map(function (p) { return p.Name; });
        }

        function displayName(p, s) {
            return s.name || p.Name;
        }

        // What the channel plays, in a line: "Horror · Movies" or "12 titles".
        function playsSummary(p, s) {
            var sources = s.genres.concat(s.networks, s.collections);
            if (sources.length) {
                var text = sources.slice(0, 3).join(', ') + (sources.length > 3 ? ' and ' + (sources.length - 3) + ' more' : '');
                return s.genres.length && s.content !== 'all' ? text + ' · ' + CONTENT_TITLES[s.content] : text;
            }
            var count = p.ChildCount || 0;
            return count === 1 ? '1 title' : count + ' titles';
        }

        function summary(p, s) {
            var parts = [playsSummary(p, s), MODE_TITLES[s.mode]];
            if (T.hasBreaks(s)) {
                parts.push('Breaks');
            }
            return parts.join(' · ');
        }

        // ---- saving ----

        // Changes a channel starting from its latest tags, so a change made a
        // moment after another never undoes it.
        function update(id, change) {
            var p = playlist(id);
            if (!p) {
                return;
            }
            var settings = T.read(p.Tags);
            change(settings);
            save(id, settings);
        }

        // Writes the playlist's tags with these settings, every one of them,
        // keeping its other tags. The page shows the change at once; saves go
        // to the server one after another. A channel numbered automatically
        // keeps the number it has now, so later channels don't move it.
        function save(id, settings) {
            var p = playlist(id);
            if (!p) {
                return Promise.resolve();
            }
            if (settings.number === null) {
                settings.number = numbering()[id] || null;
            }
            var previous = p.Tags || [];
            var updated = T.applied(settings, previous);
            p.Tags = updated;
            render();
            return writeTags(id, updated, function () {
                p.Tags = previous;
                render();
            });
        }

        // Saves run one after another. With undo, a failure is shown and
        // undone here; without, it's passed on to the caller.
        function writeTags(id, tags, undo) {
            state.pendingSaves++;
            setStatus('Saving…');
            var job = state.saving.then(function () {
                return getJSON('Users/' + userId() + '/Items/' + id, { fields: EDITABLE_FIELDS }).then(function (record) {
                    record.Tags = tags;
                    return postJSON('Items/' + id, record);
                });
            });
            state.saving = job.catch(function () {});
            return job.then(function () {
                state.pendingSaves--;
                if (!state.pendingSaves) {
                    setStatus('Saved', 'ok');
                }
            }, function (error) {
                state.pendingSaves--;
                if (!undo) {
                    throw error;
                }
                undo();
                failure(error, "Couldn't save");
            });
        }

        // Makes a playlist a channel with the next free number, and its
        // picture as the logo when it has one, unless its tags already say.
        function turnOn(p) {
            var settings = T.read(p.Tags);
            settings.isChannel = true;
            if (settings.number === null) {
                settings.number = nextNumber();
            }
            if (!T.sets('logo', p.Tags)) {
                settings.showsLogo = !!(p.ImageTags && p.ImageTags.Primary);
            }
            state.selected = p.Id;
            save(p.Id, settings);
        }

        function nextNumber() {
            var numbers = numbering();
            return Object.keys(numbers).reduce(function (high, id) { return Math.max(high, numbers[id]); }, 0) + 1;
        }

        // Drag to renumber: the channels keep the numbers they had between
        // them, handed out again in the new order.
        function renumber(order) {
            var current = channels();
            var numbers = current.map(function (c) { return c.number; }).sort(function (a, b) { return a - b; });
            order.forEach(function (id, index) {
                var channel = current.filter(function (c) { return c.playlist.Id === id; })[0];
                if (channel && channel.settings.number !== numbers[index]) {
                    update(id, function (s) { s.number = numbers[index]; });
                }
            });
        }

        // ---- the list ----

        function render() {
            renderList();
            renderOthers();
            renderEditor();
        }

        function renderList() {
            var list = channels();
            var counts = {};
            list.forEach(function (c) { counts[c.number] = (counts[c.number] || 0) + 1; });
            el.list.innerHTML = '';
            if (!list.length) {
                el.list.appendChild(h('div', { className: 'acEmpty', text: 'No channels yet. Make one with New Channel, or turn on one of your playlists below.' }));
                return;
            }
            list.forEach(function (c) {
                var p = c.playlist;
                var row = h('div', {
                    className: 'acRow' + (state.selected === p.Id ? ' selected' : ''),
                    draggable: 'true',
                    onclick: function () {
                        state.selected = state.selected === p.Id ? null : p.Id;
                        render();
                    }
                }, [
                    h('span', { className: 'acHandle', title: 'Drag to renumber', text: '⠿' }),
                    h('span', { className: 'acNumber', text: String(c.number) }),
                    h('div', { className: 'acRowText' }, [
                        h('div', { className: 'acRowName', text: displayName(p, c.settings) }),
                        h('div', { className: 'acRowSummary', text: summary(p, c.settings) }),
                        counts[c.number] > 1 ? h('div', { className: 'acWarn', text: 'Another channel has this number too.' }) : null
                    ])
                ]);
                row.dataset.id = p.Id;
                addDrag(row);
                el.list.appendChild(row);
            });
        }

        function addDrag(row) {
            row.addEventListener('dragstart', function (e) {
                state.drag = row.dataset.id;
                row.classList.add('dragging');
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', row.dataset.id);
            });
            row.addEventListener('dragend', function () {
                state.drag = null;
                row.classList.remove('dragging');
                clearDropMarks();
            });
            row.addEventListener('dragover', function (e) {
                if (!state.drag || state.drag === row.dataset.id) {
                    return;
                }
                e.preventDefault();
                clearDropMarks();
                row.classList.add(isAfter(row, e) ? 'dropAfter' : 'dropBefore');
            });
            row.addEventListener('drop', function (e) {
                if (!state.drag || state.drag === row.dataset.id) {
                    return;
                }
                e.preventDefault();
                var after = isAfter(row, e);
                var order = channels().map(function (c) { return c.playlist.Id; }).filter(function (id) { return id !== state.drag; });
                var at = order.indexOf(row.dataset.id) + (after ? 1 : 0);
                order.splice(at, 0, state.drag);
                clearDropMarks();
                renumber(order);
            });
        }

        function isAfter(row, e) {
            var box = row.getBoundingClientRect();
            return e.clientY > box.top + box.height / 2;
        }

        function clearDropMarks() {
            el.list.querySelectorAll('.dropBefore, .dropAfter').forEach(function (r) { r.classList.remove('dropBefore', 'dropAfter'); });
        }

        // Video playlists that aren't channels (filler playlists aside), to
        // turn on.
        function renderOthers() {
            var others = state.playlists.filter(function (p) { return !isChannelPlaylist(p) && !T.isFillerPlaylist(p.Tags); });
            el.othersTitle.textContent = 'Other Video Playlists (' + others.length + ')';
            el.others.innerHTML = '';
            if (!others.length) {
                el.others.appendChild(h('div', { className: 'acEmpty', text: 'Every video playlist is a channel or a filler playlist.' }));
            }
            others.forEach(function (p) {
                var off = T.sets('channel', p.Tags);
                el.others.appendChild(h('div', { className: 'acRow off' }, [
                    h('div', { className: 'acRowText' }, [
                        h('div', { className: 'acRowName', text: p.Name }),
                        h('div', { className: 'acRowSummary', text: off ? 'A channel, turned off' : playsSummary(p, T.read(p.Tags)) })
                    ]),
                    button(off ? 'Turn On' : 'Make a Channel', function () { turnOn(p); })
                ]));
            });
        }

        // ---- the editor ----

        function fold(key, title, children) {
            var details = h('details', { className: 'acFold', open: !!state.folds[key] }, [h('summary', { text: title })].concat(children));
            details.addEventListener('toggle', function () { state.folds[key] = details.open; });
            return details;
        }

        function renderEditor() {
            el.editor.innerHTML = '';
            var p = state.selected && playlist(state.selected);
            if (!p || !isChannelPlaylist(p)) {
                state.selected = null;
                if (!state.playlists.length) {
                    return;
                }
                el.editor.appendChild(h('div', { className: 'acPanel' }, [
                    h('div', { className: 'acEmpty', text: 'Pick a channel to change it.' })
                ]));
                return;
            }
            var s = T.read(p.Tags);
            var id = p.Id;
            var set = function (change) { return function (value) { update(id, function (t) { change(t, value); }); }; };
            var number = numbering()[id];

            var panel = h('div', { className: 'acPanel' }, [
                h('div', { className: 'acPanelHead' }, [
                    h('h3', { text: number + '  ' + displayName(p, s) }),
                    button('Close', function () {
                        state.selected = null;
                        render();
                    })
                ]),
                checkbox('On', s.isChannel, set(function (t, v) { t.isChannel = v; })),
                field('Name', h('input', {
                    type: 'text', value: s.name || '', placeholder: p.Name,
                    onchange: function (e) {
                        var name = e.target.value.trim();
                        update(id, function (t) { t.name = name && name !== p.Name ? name : null; });
                    }
                }), "Leave it empty for the playlist's own name."),
                field('Number', numberInput(number, 1, 99999, null, set(function (t, v) { t.number = v; }))),
                field('Mode', select(T.MODES.map(function (m) { return [m, MODE_TITLES[m]]; }), s.mode, set(function (t, v) { t.mode = v; })), MODE_HELP[s.mode]),
                checkbox("Logo: the playlist's picture is the channel's logo", s.showsLogo, set(function (t, v) { t.showsLogo = v; })),
                checkbox('Show the logo in the corner while it plays', s.showsBug, set(function (t, v) { t.showsBug = v; })),
                field('Breaks', chips(s.fillerPlaylists, fillerNames(), set(function (t, v) {
                    // Turning breaks on lines programmes up on the half hour, the usual TV feel.
                    if (!T.hasBreaks(t) && v.length) {
                        t.breaks.align = 30;
                        t.breaks.exactAlign = false;
                    }
                    t.fillerPlaylists = v;
                }), { placeholder: 'Add a filler playlist…' }),
                fillerNames().length
                    ? 'Clips from these filler playlists play between programmes. None: no breaks.'
                    : 'No filler playlists yet: tag a playlist aster:filler, and each clip in it aster:commercial, promo, trailer, interstitial or bumper.'),
                fold('plays|' + id, 'What It Plays: ' + playsSummary(p, s), playsFields(s, set)),
                fold('advanced|' + id, 'Advanced', advancedFields(s, set, id)),
                fold('tags|' + id, 'Tags', [
                    h('div', { className: 'acHelp', text: "Everything above is these tags on the playlist; Jellyfin's Edit Metadata ▸ Tags shows the same." }),
                    h('div', { className: 'acTags', text: (p.Tags || []).join('\n') })
                ])
            ]);
            el.editor.appendChild(panel);
        }

        function playsFields(s, set) {
            var o = state.options;
            return [
                h('div', { className: 'acHelp', text: "As well as the playlist's own titles, the channel plays every title in these:" }),
                field('Genres', chips(s.genres, o.genres, set(function (t, v) { t.genres = v; }))),
                field('Networks', chips(s.networks, o.networks, set(function (t, v) { t.networks = v; }))),
                field('Collections', chips(s.collections, o.collections, set(function (t, v) { t.collections = v; }))),
                s.genres.length ? field('Content', select(T.CONTENTS.map(function (c) { return [c, CONTENT_TITLES[c]]; }), s.content,
                    set(function (t, v) { t.content = v; })), 'What it takes from its genres.') : null,
                T.hasSources(s) ? field('Libraries', chips(s.libraries, o.libraries, set(function (t, v) { t.libraries = v; })), 'None: every library.') : null,
                T.hasSources(s) ? field('Years', h('div', { className: 'acPair' }, [
                    numberInput(s.years ? s.years[0] : null, 1880, 2200, 'From', set(function (t, v) { t.years = yearsWith(t.years, v, 0); }), true),
                    numberInput(s.years ? s.years[1] : null, 1880, 2200, 'To', set(function (t, v) { t.years = yearsWith(t.years, v, 1); }), true)
                ]), 'Only titles from these years. Empty: any year.') : null,
                T.hasSources(s) ? field('Ratings', chips(s.ratings, o.ratings, set(function (t, v) { t.ratings = v; }), { free: true }), 'Only titles with these ratings. None: any.') : null
            ];
        }

        // A years range with one end changed: the other end follows when there
        // was none, and emptying an end leaves just the other year.
        function yearsWith(years, value, end) {
            if (value === null) {
                return years ? [years[1 - end], years[1 - end]] : null;
            }
            var range = years ? years.slice() : [value, value];
            range[end] = value;
            return [Math.min(range[0], range[1]), Math.max(range[0], range[1])];
        }

        function advancedFields(s, set, id) {
            var o = state.options;
            var b = s.breaks;
            var counts = [];
            for (var i = 0; i <= T.LIMITS.mostPromos; i++) {
                counts.push([i, String(i)]);
            }
            var fields = [h('div', { className: 'acSub', text: 'Lineup' })];
            if (s.mode !== 'sequential' && s.mode !== 'airdate') {
                fields.push(field('New Order', select(T.RESHUFFLES.map(function (r) { return [r, RESHUFFLE_TITLES[r]]; }), s.reshuffle,
                    set(function (t, v) { t.reshuffle = v; })), 'How long one shuffled order lasts. Weeks start on Monday, months on the 1st.'));
                fields.push(h('div', { className: 'acButtons' }, [
                    button('Shuffle Again', function () { update(id, function (t) { t.seed += 1; }); })
                ]), h('div', { className: 'acHelp', text: 'Deals a fresh lineup, for every device at once.' }));
            }
            if (s.mode === 'blocks') {
                fields.push(field('Episodes in a Row', h('div', { className: 'acPair' }, [
                    numberInput(s.blocks[0], 1, T.LIMITS.longestBlock, 'Fewest', set(function (t, v) { t.blocks = [v, Math.max(v, t.blocks[1])]; })),
                    numberInput(s.blocks[1], 1, T.LIMITS.longestBlock, 'Most', set(function (t, v) { t.blocks = [Math.min(v, t.blocks[0]), v]; }))
                ])));
            }
            fields.push(field('Length, in Minutes', h('div', { className: 'acPair' }, [
                numberInput(s.minLength, 1, 600, 'Shortest', set(function (t, v) { t.minLength = v; }), true),
                numberInput(s.maxLength, 1, 600, 'Longest', set(function (t, v) { t.maxLength = v; }), true)
            ]), 'Leaves out titles shorter or longer than these. Empty: no limit.'));
            fields.push(field('More Turns', chips(s.weights.map(function (w) { return w.show + ':' + w.weight; }), [], set(function (t, v) {
                t.weights = v.map(function (text) {
                    var colon = text.lastIndexOf(':');
                    return { show: text.slice(0, colon).trim(), weight: Number(text.slice(colon + 1)) };
                });
            }), {
                free: true, placeholder: 'Show:2',
                invalid: 'Type a show and a number of turns from 1 to 5, like Friends:2.',
                validate: function (text) {
                    var colon = text.lastIndexOf(':');
                    var weight = colon < 0 ? NaN : Number(text.slice(colon + 1).trim());
                    var show = colon < 0 ? '' : text.slice(0, colon).trim();
                    return show && weight >= 1 && Math.round(weight) === weight ? show + ':' + Math.min(weight, T.LIMITS.heaviestWeight) : null;
                }
            }), 'A show that takes its turn more often: Friends:2 plays twice as often.'));

            if (s.showsBug) {
                fields.push(h('div', { className: 'acSub', text: 'Corner Logo' }),
                    field('Corner', select(T.CORNERS.map(function (c) { return [c, CORNER_TITLES[c]]; }), s.bugCorner, set(function (t, v) { t.bugCorner = v; }))),
                    field('Hide After, in Seconds', numberInput(s.bugHideAfter || null, 1, 3600, 'Never', set(function (t, v) { t.bugHideAfter = v || 0; }), true),
                        'Fades after this long from tuning in or a programme starting. Empty: it stays.'),
                    field('Opacity, %', numberInput(s.bugOpacity, 5, 100, null, set(function (t, v) { t.bugOpacity = v; }))),
                    field('Size, %', numberInput(s.bugSize, 25, 300, null, set(function (t, v) { t.bugSize = v; }))));
            }

            if (T.hasBreaks(s)) {
                fields.push(h('div', { className: 'acSub', text: 'Breaks' }),
                    field('Clips to Play', h('div', {}, T.BREAK_KINDS.map(function (kind) {
                        return checkbox(KIND_TITLES[kind], s.fillerTypes.indexOf(kind) >= 0, set(function (t, on) {
                            var types = t.fillerTypes.filter(function (k) { return k !== kind; });
                            // At least one: none would turn the breaks off.
                            t.fillerTypes = on ? types.concat([kind]) : (types.length ? types : t.fillerTypes);
                        }));
                    })), 'Clips only play when their own tag says what they are.'),
                    field('Bumpers', select(T.BUMPER_PLACEMENTS.map(function (x) { return [x, BUMPER_TITLES[x]]; }), b.bumpersAt, set(function (t, v) { t.breaks.bumpersAt = v; }))),
                    field('Commercial Time, in Minutes', numberInput(b.minutes, 0, T.LIMITS.longestBreak, null, set(function (t, v) { t.breaks.minutes = v; }))),
                    h('div', { className: 'acPair' }, [
                        field('Promos', select(counts, b.promoCount, set(function (t, v) { t.breaks.promoCount = Number(v); }))),
                        field('Trailers', select(counts, b.trailerCount, set(function (t, v) { t.breaks.trailerCount = Number(v); }))),
                        field('Interstitials', select(counts, b.interstitialCount, set(function (t, v) { t.breaks.interstitialCount = Number(v); })))
                    ]),
                    field('A Break After', select(Array.from({ length: T.LIMITS.longestBreakGap }, function (_, i) {
                        return [i + 1, i === 0 ? 'Every Programme' : 'Every ' + (i + 1) + ' Programmes'];
                    }), b.every, set(function (t, v) { t.breaks.every = Number(v); }))),
                    field('Line Up', select([[0, 'Off']].concat(T.ALIGNMENTS.map(function (m) {
                        return [m, m === 60 ? 'On the Hour' : m === 30 ? 'On the Half Hour' : 'Every ' + m + ' Minutes'];
                    })), b.align, set(function (t, v) { t.breaks.align = Number(v); })), 'Programmes start on the mark, each break filling the gap.'),
                    b.align > 0 ? field('Fit', select([['best', 'Best Effort'], ['exact', 'Exact']], b.exactAlign ? 'exact' : 'best',
                        set(function (t, v) { t.breaks.exactAlign = v === 'exact'; })),
                    b.exactAlign ? 'The last clip is cut short to land exactly on the mark.' : 'Whole clips, as near the mark as they get; never late.') : null);
            }

            fields.push(h('div', { className: 'acSub', text: 'Who Gets It' }),
                field('Users', chips(s.users, o.users, set(function (t, v) { t.users = v; })),
                    "None: everyone. It only hides the channel in Aster; whoever the playlist is shared with can still open it in Jellyfin."));
            return fields;
        }

        // ---- New Channel ----

        var newState = { kind: 'show', pick: '', name: '' };

        function pickChoices(kind) {
            var o = state.options;
            switch (kind) {
                case 'show': return o.series.map(function (x) { return x.name; });
                case 'genre': return o.genres;
                case 'network': return o.networks;
                case 'collection': return o.collections;
                default:
                    return state.playlists.filter(function (p) { return !isChannelPlaylist(p) && !T.isFillerPlaylist(p.Tags); }).map(function (p) { return p.Name; });
            }
        }

        function renderNew() {
            var panel = el.newPanel;
            panel.innerHTML = '';
            var kind = SOURCE_KINDS.filter(function (k) { return k.id === newState.kind; })[0];
            var choices = pickChoices(newState.kind);
            var listId = 'acNewChoices';
            var nameInput = h('input', {
                type: 'text', value: newState.name, placeholder: newState.pick || 'Name',
                oninput: function (e) { newState.name = e.target.value; }
            });
            var pickInput = h('input', {
                type: 'text', list: listId, value: newState.pick, placeholder: 'Type to search…',
                oninput: function (e) {
                    newState.pick = e.target.value;
                    nameInput.placeholder = newState.pick || 'Name';
                }
            });
            panel.appendChild(h('div', {}, [
                h('div', { className: 'acPanelHead' }, [
                    h('h3', { text: 'New Channel' }),
                    button('Cancel', function () { panel.hidden = true; })
                ]),
                field('Make a Channel From', select(SOURCE_KINDS.map(function (k) { return [k.id, k.title]; }), newState.kind, function (v) {
                    newState.kind = v;
                    newState.pick = '';
                    renderNew();
                })),
                field(kind.pick, h('div', {}, [pickInput, h('datalist', { id: listId }, choices.map(function (c) { return h('option', { value: c }); }))]),
                    choices.length ? null : 'There are none to pick from.'),
                newState.kind === 'playlist' ? null : field('Name', nameInput, 'Empty: named after what you picked. You can change it later.'),
                h('div', { className: 'acHelp', text: {
                    show: "A public playlist of the show's episodes, with the show's logo.",
                    genre: 'A public playlist that fills itself with every title in the genre.',
                    network: 'A public playlist that fills itself with every title from the network or studio.',
                    collection: 'A public playlist that fills itself with every title in the collection.',
                    playlist: 'The playlist becomes a channel as it is.'
                }[newState.kind] }),
                h('div', { className: 'acButtons' }, [button(state.creating ? 'Making…' : 'Make the Channel', makeChannel, true)])
            ]));
        }

        function makeChannel() {
            if (state.creating) {
                return;
            }
            var picked = newState.pick.trim();
            var match = pickChoices(newState.kind).filter(function (c) { return c.toLowerCase() === picked.toLowerCase(); })[0];
            if (!match) {
                setStatus('Pick one from the list first.', 'error');
                return;
            }
            if (newState.kind === 'playlist') {
                var existing = state.playlists.filter(function (p) { return p.Name === match && !isChannelPlaylist(p); })[0];
                el.newPanel.hidden = true;
                turnOn(existing);
                return;
            }
            var name = newState.name.trim() || match;
            var settings = T.defaults();
            settings.isChannel = true;
            settings.number = nextNumber();
            if (newState.kind === 'show') {
                settings.showsLogo = true;
            } else if (newState.kind === 'genre') {
                settings.genres = [match];
            } else if (newState.kind === 'network') {
                settings.networks = [match];
            } else {
                settings.collections = [match];
            }
            var written = T.applied(settings, []);
            var series = newState.kind === 'show' ? state.options.series.filter(function (x) { return x.name === match; })[0] : null;

            state.creating = true;
            renderNew();
            setStatus('Making ' + name + '…');
            var episodes = series
                ? allItems({ userId: userId(), parentId: series.id, recursive: true, includeItemTypes: 'Episode', isMissing: false, enableImages: false, enableUserData: false })
                    .then(function (items) { return items.map(function (i) { return i.Id; }); })
                : Promise.resolve([]);
            episodes.then(function (ids) {
                return postJSON('Playlists', { Name: name, Ids: ids, UserId: userId(), MediaType: 'Video', IsPublic: true }, true);
            }).then(function (result) {
                return writeTags(result.Id, written).then(function () { return result.Id; });
            }).then(function (id) {
                state.selected = id;
                el.newPanel.hidden = true;
                newState = { kind: newState.kind, pick: '', name: '' };
                return reload().then(function () { setStatus(name + ' is channel ' + settings.number + '.', 'ok'); });
            }, function (error) {
                failure(error, "Couldn't make the channel");
            }).then(function () {
                state.creating = false;
                if (!el.newPanel.hidden) {
                    renderNew();
                }
            });
        }

        page.querySelector('#acNew').addEventListener('click', function () {
            el.newPanel.hidden = false;
            renderNew();
        });
        page.querySelector('#acReload').addEventListener('click', function () { reload(); });

        reload();
        return { reload: reload };
    }

    window.AsterChannelsPage = { create: create };
})();
