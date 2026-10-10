/*
 * Aurora - Apple TV style behaviour for the Jellyfin 12.x web client.
 *
 * Injected into jellyfin-web's start page by the Aurora plugin. jellyfin-web is a single-page app that swaps pages
 * without reloading, so nothing here runs "once per page": a throttled refresh() looks at what is on screen after
 * every navigation and DOM change and adds or removes features to match.
 *
 * Safety rules, because jellyfin-web changes between releases and this script must never be the reason a page
 * breaks: every feature checks for the elements it needs and does nothing if they are missing; every entry point is
 * wrapped in try/catch; anything Aurora adds is marked with an "au-" class so it can be found and removed; and data
 * from the server is only ever put into the page with textContent, never as HTML.
 */
(function () {
    'use strict';

    if (window.__auroraLoaded) {
        return;
    }
    window.__auroraLoaded = true;

    var LOG = '[Aurora]';
    var BASE = (function () {
        var src = (document.currentScript && document.currentScript.src) || '';
        var at = src.indexOf('/Aurora/web/aurora.js');
        return at > 0 ? src.slice(0, at) : '';
    })();

    var DEFAULTS = {
        EnableHero: true, HeroSource: 'latest', HeroCount: 8, HeroIntervalSeconds: 9,
        EnableTrailers: true, AllowYouTubeTrailers: true, TrailerDelaySeconds: 4,
        EnableTilt: true, EnableGenreChips: true,
        EnableUpNext: true, EnableBadges: true, EnableEpisodeShelf: true
    };
    var config = DEFAULTS;

    var reducedMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var finePointer = window.matchMedia && window.matchMedia('(hover: hover) and (pointer: fine)').matches;

    function safe(name, fn) {
        return function () {
            try {
                return fn.apply(this, arguments);
            } catch (e) {
                console.warn(LOG, name, 'failed; leaving the page as Jellyfin drew it.', e);
                return undefined;
            }
        };
    }

    function el(tag, className, text) {
        var e = document.createElement(tag);
        if (className) {
            e.className = className;
        }
        if (text !== undefined && text !== null) {
            e.textContent = String(text);
        }
        return e;
    }

    function api() {
        return window.ApiClient && typeof window.ApiClient.getCurrentUserId === 'function' && window.ApiClient.getCurrentUserId()
            ? window.ApiClient : null;
    }

    function isDesktop() {
        return document.documentElement.classList.contains('layout-desktop');
    }

    function visiblePage(selector) {
        var pages = document.querySelectorAll(selector);
        for (var i = 0; i < pages.length; i++) {
            if (!pages[i].classList.contains('hide') && pages[i].offsetParent !== null) {
                return pages[i];
            }
        }
        return null;
    }

    function hashParam(name) {
        var q = location.hash.split('?')[1] || '';
        var m = q.match(new RegExp('(?:^|&)' + name + '=([^&]+)'));
        return m ? decodeURIComponent(m[1]) : null;
    }

    function formatRuntime(ticks) {
        if (!ticks) {
            return '';
        }
        var mins = Math.round(ticks / 600000000);
        var h = Math.floor(mins / 60);
        var m = mins % 60;
        return h ? h + 'h ' + m + 'm' : m + 'm';
    }

    /* One fetch per title per minute, shared by trailers, badges and the Episodes row. */
    var itemCache = {};

    function getItemCached(client, id) {
        var hit = itemCache[id];
        if (hit && Date.now() - hit.at < 60000) {
            return hit.promise;
        }
        var promise = client.getItem(client.getCurrentUserId(), id);
        itemCache[id] = { at: Date.now(), promise: promise };
        promise.catch(function () { delete itemCache[id]; });
        return promise;
    }

    /* ------------------------------------------------------------------ */
    /* Home hero banner                                                     */
    /* ------------------------------------------------------------------ */

    var hero = { node: null, items: null, fetchedAt: 0, userId: null, index: 0, timer: null, paused: false };

    function heroQuery() {
        var q = {
            IncludeItemTypes: 'Movie,Series',
            Recursive: true,
            ImageTypes: 'Backdrop',
            Fields: 'Overview,Genres,ProductionYear,OfficialRating,RunTimeTicks,CommunityRating,ChildCount',
            EnableImageTypes: 'Backdrop,Logo',
            ImageTypeLimit: 1,
            Limit: Math.max(config.HeroCount * 2, 12)
        };
        if (config.HeroSource === 'random') {
            q.SortBy = 'Random';
        } else if (config.HeroSource === 'favorites') {
            q.Filters = 'IsFavorite';
            q.SortBy = 'Random';
        } else {
            q.SortBy = 'DateCreated,SortName';
            q.SortOrder = 'Descending';
        }
        return q;
    }

    function loadHeroItems(client) {
        var userId = client.getCurrentUserId();
        var fresh = hero.items && hero.userId === userId && (Date.now() - hero.fetchedAt) < 10 * 60 * 1000;
        if (fresh) {
            return Promise.resolve(hero.items);
        }
        return client.getItems(userId, heroQuery()).then(function (result) {
            var items = ((result && result.Items) || []).filter(function (i) {
                return i.BackdropImageTags && i.BackdropImageTags.length;
            }).slice(0, config.HeroCount);
            hero.items = items;
            hero.userId = userId;
            hero.fetchedAt = Date.now();
            return items;
        });
    }

    function heroMeta(item) {
        var parts = [];
        if (item.ProductionYear) {
            parts.push(String(item.ProductionYear));
        }
        if (item.Type === 'Series' && item.ChildCount) {
            parts.push(item.ChildCount + (item.ChildCount === 1 ? ' Season' : ' Seasons'));
        } else if (item.RunTimeTicks) {
            parts.push(formatRuntime(item.RunTimeTicks));
        }
        if (item.Genres && item.Genres.length) {
            parts.push(item.Genres.slice(0, 2).join(' · '));
        }
        return parts;
    }

    function openDetails(id, autoplay) {
        try {
            if (autoplay) {
                sessionStorage.setItem('aurora.autoplay', id);
            }
        } catch (e) { /* private mode: just open the page */ }
        location.hash = '#/details?id=' + encodeURIComponent(id);
    }

    function buildSlide(client, item) {
        var slide = el('div', 'au-hero-slide');
        var img = el('div', 'au-hero-img');
        img.style.backgroundImage = 'url("' + client.getImageUrl(item.Id, {
            type: 'Backdrop', maxWidth: 1920, tag: item.BackdropImageTags[0], quality: 90
        }) + '")';
        slide.appendChild(img);

        var content = el('div', 'au-hero-content');
        if (item.ImageTags && item.ImageTags.Logo) {
            var logo = el('img', 'au-hero-logo');
            logo.alt = item.Name || '';
            logo.src = client.getImageUrl(item.Id, { type: 'Logo', maxWidth: 900, tag: item.ImageTags.Logo });
            content.appendChild(logo);
        } else {
            content.appendChild(el('h2', 'au-hero-title', item.Name));
        }

        var meta = el('div', 'au-hero-meta');
        heroMeta(item).forEach(function (p) {
            meta.appendChild(el('span', null, p));
        });
        if (item.OfficialRating) {
            meta.appendChild(el('span', 'au-hero-rating', item.OfficialRating));
        }
        content.appendChild(meta);

        if (item.Overview) {
            content.appendChild(el('p', 'au-hero-overview', item.Overview));
        }

        var buttons = el('div', 'au-hero-buttons');
        var play = el('button', 'au-btn au-btn-primary');
        play.type = 'button';
        play.appendChild(el('span', 'material-icons au-btn-icon', 'play_arrow'));
        play.appendChild(el('span', null, 'Play'));
        play.addEventListener('click', function () { openDetails(item.Id, true); });
        var more = el('button', 'au-btn au-btn-glass');
        more.type = 'button';
        more.appendChild(el('span', 'material-icons au-btn-icon', 'info'));
        more.appendChild(el('span', null, 'More Info'));
        more.addEventListener('click', function () { openDetails(item.Id, false); });
        buttons.appendChild(play);
        buttons.appendChild(more);
        content.appendChild(buttons);

        slide.appendChild(content);
        return slide;
    }

    function showSlide(i) {
        if (!hero.node) {
            return;
        }
        var slides = hero.node.querySelectorAll('.au-hero-slide');
        var dots = hero.node.querySelectorAll('.au-hero-dot');
        if (!slides.length) {
            return;
        }
        hero.index = (i + slides.length) % slides.length;
        for (var s = 0; s < slides.length; s++) {
            var on = s === hero.index;
            slides[s].classList.toggle('is-active', on);
            slides[s].setAttribute('aria-hidden', on ? 'false' : 'true');
            slides[s].querySelectorAll('button').forEach(function (b) { b.tabIndex = on ? 0 : -1; });
            if (dots[s]) {
                dots[s].classList.toggle('is-active', on);
                dots[s].setAttribute('aria-current', on ? 'true' : 'false');
            }
        }
    }

    function startRotation() {
        stopRotation();
        if (reducedMotion) {
            return;
        }
        hero.timer = setInterval(function () {
            if (!hero.paused && !document.hidden && hero.node && document.contains(hero.node)) {
                showSlide(hero.index + 1);
            }
        }, config.HeroIntervalSeconds * 1000);
    }

    function stopRotation() {
        if (hero.timer) {
            clearInterval(hero.timer);
            hero.timer = null;
        }
    }

    /* Make the banner full-bleed: pull it out to the window's left edge and up under the transparent header. */
    function placeHero() {
        if (!hero.node || !hero.node.parentNode) {
            return;
        }
        hero.node.style.marginLeft = '0px';
        hero.node.style.marginTop = '0px';
        var r = hero.node.getBoundingClientRect();
        hero.node.style.width = document.documentElement.clientWidth + 'px';
        hero.node.style.marginLeft = (-r.left) + 'px';
        hero.node.style.marginTop = (-(r.top + window.scrollY)) + 'px';
    }

    var ensureHero = safe('hero', function () {
        var client = api();
        var page = visiblePage('.homePage, #indexPage');
        if (!config.EnableHero || !client || !page) {
            stopRotation();
            return;
        }
        var anchor = page.querySelector('.tabContent.is-active .homeSectionsContainer') || page.querySelector('.homeSectionsContainer');
        if (!anchor) {
            return;
        }
        if (hero.node && document.contains(hero.node) && hero.node.nextElementSibling === anchor) {
            return;
        }
        if (hero.node && hero.node.parentNode) {
            hero.node.parentNode.removeChild(hero.node);
        }
        hero.node = null;

        loadHeroItems(client).then(safe('hero render', function (items) {
            if (!items.length || !document.contains(anchor) || (hero.node && document.contains(hero.node))) {
                return;
            }
            var node = el('section', 'au-hero');
            node.setAttribute('aria-roledescription', 'carousel');
            node.setAttribute('aria-label', 'Featured');
            items.forEach(function (item) { node.appendChild(buildSlide(client, item)); });

            if (items.length > 1) {
                var dots = el('div', 'au-hero-dots');
                items.forEach(function (item, i) {
                    var d = el('button', 'au-hero-dot');
                    d.type = 'button';
                    d.setAttribute('aria-label', 'Show ' + (item.Name || 'title ' + (i + 1)));
                    d.addEventListener('click', function () { showSlide(i); startRotation(); });
                    dots.appendChild(d);
                });
                node.appendChild(dots);
            }

            node.addEventListener('mouseenter', function () { hero.paused = true; });
            node.addEventListener('mouseleave', function () { hero.paused = false; });
            node.addEventListener('focusin', function () { hero.paused = true; });
            node.addEventListener('focusout', function () { hero.paused = false; });

            anchor.parentNode.insertBefore(node, anchor);
            hero.node = node;
            document.documentElement.classList.add('au-has-hero');
            placeHero();
            showSlide(0);
            startRotation();
        }), function (err) {
            console.warn(LOG, 'could not load featured titles', err);
        });
    });

    function heroGone() {
        if (!visiblePage('.homePage, #indexPage')) {
            stopRotation();
            document.documentElement.classList.remove('au-has-hero');
        }
    }

    /* "Play" in the banner opens the details page and presses its Play button once it appears. */
    var autoplayFromHero = safe('autoplay', function () {
        var wanted;
        try {
            wanted = sessionStorage.getItem('aurora.autoplay');
        } catch (e) {
            return;
        }
        if (!wanted || hashParam('id') !== wanted) {
            return;
        }
        var page = visiblePage('.itemDetailPage');
        var btn = page && (page.querySelector('.btnResume:not(.hide)') || page.querySelector('.btnPlay:not(.hide)'));
        if (btn) {
            try { sessionStorage.removeItem('aurora.autoplay'); } catch (e) { /* ignore */ }
            stopTrailer();
            btn.click();
        }
    });

    /* ------------------------------------------------------------------ */
    /* Trailer backgrounds                                                  */
    /* ------------------------------------------------------------------ */

    /* doneFor: the item whose trailer already ran (or was tried) on this visit, so an ended or failed trailer never loops. */
    var trailer = { itemId: null, doneFor: null, timer: null, node: null, mute: null, media: null, kind: null, muted: true };

    function youTubeId(url) {
        var m = /(?:youtube\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/)|youtu\.be\/)([A-Za-z0-9_-]{11})/.exec(url || '');
        return m ? m[1] : null;
    }

    function stopTrailer() {
        if (trailer.timer) {
            clearTimeout(trailer.timer);
            trailer.timer = null;
        }
        var node = trailer.node;
        if (trailer.mute && trailer.mute.parentNode) {
            trailer.mute.parentNode.removeChild(trailer.mute);
        }
        trailer.mute = null;
        trailer.node = null;
        trailer.media = null;
        trailer.itemId = null;
        document.documentElement.classList.remove('au-trailer-on');
        if (node) {
            node.classList.remove('is-visible');
            if (node.tagName) {
                var v = node.querySelector('video');
                if (v) {
                    v.pause();
                }
            }
            setTimeout(function () {
                if (node.parentNode) {
                    node.parentNode.removeChild(node);
                }
            }, 900);
        }
    }

    function ytCommand(func, args) {
        if (trailer.kind === 'youtube' && trailer.media && trailer.media.contentWindow) {
            trailer.media.contentWindow.postMessage(JSON.stringify({ event: 'command', func: func, args: args || [] }), '*');
        }
    }

    function setMuted(muted) {
        trailer.muted = muted;
        if (trailer.kind === 'local' && trailer.media) {
            trailer.media.muted = muted;
        } else {
            ytCommand(muted ? 'mute' : 'unMute');
        }
        var btn = trailer.mute && trailer.mute.querySelector('.material-icons');
        if (btn) {
            btn.textContent = muted ? 'volume_off' : 'volume_up';
        }
    }

    function trailerShell() {
        var node = el('div', 'au-trailer');
        node.setAttribute('aria-hidden', 'true');
        var mute = el('button', 'au-trailer-mute');
        mute.type = 'button';
        mute.setAttribute('aria-label', 'Trailer sound');
        mute.appendChild(el('span', 'material-icons', 'volume_off'));
        mute.addEventListener('click', function () { setMuted(!trailer.muted); });
        return { node: node, mute: mute };
    }

    function reveal(itemId) {
        if (trailer.itemId !== itemId || !trailer.node || window.scrollY > window.innerHeight * 0.5 || document.hidden) {
            stopTrailer();
            return;
        }
        trailer.node.classList.add('is-visible');
        if (trailer.mute) {
            trailer.mute.classList.add('is-visible');
        }
        document.documentElement.classList.add('au-trailer-on');
    }

    function playLocal(client, itemId, trailerItemId, fallback) {
        var shell = trailerShell();
        var video = el('video', 'au-trailer-media');
        video.muted = true;
        video.autoplay = true;
        video.playsInline = true;
        video.setAttribute('playsinline', '');
        video.preload = 'auto';
        video.src = client.getUrl('Videos/' + trailerItemId + '/stream', { static: true, api_key: client.accessToken() });
        video.addEventListener('playing', function () { reveal(itemId); }, { once: true });
        video.addEventListener('ended', stopTrailer);
        video.addEventListener('error', function () {
            if (trailer.media === video) {
                stopTrailer();
                if (fallback) {
                    trailer.itemId = itemId;
                    fallback();
                }
            }
        });
        shell.node.appendChild(video);
        document.body.appendChild(shell.node);
        document.body.appendChild(shell.mute);
        trailer.node = shell.node;
        trailer.mute = shell.mute;
        trailer.media = video;
        trailer.kind = 'local';
        var p = video.play();
        if (p && p.catch) {
            p.catch(function () { /* autoplay refused: the error/ended handlers or navigation clean up */ });
        }
    }

    function playYouTube(itemId, ytId) {
        var shell = trailerShell();
        var frame = el('iframe', 'au-trailer-media au-trailer-yt');
        frame.allow = 'autoplay; encrypted-media';
        frame.referrerPolicy = 'strict-origin-when-cross-origin';
        frame.tabIndex = -1;
        frame.src = 'https://www.youtube-nocookie.com/embed/' + ytId + '?autoplay=1&mute=1&controls=0&loop=1&playlist=' + ytId
            + '&playsinline=1&rel=0&iv_load_policy=3&cc_load_policy=0&disablekb=1&modestbranding=1&enablejsapi=1&origin=' + encodeURIComponent(location.origin);
        /* YouTube shows a title card for the first moment; reveal a little after load so only video fades in. */
        frame.addEventListener('load', function () {
            setTimeout(function () { reveal(itemId); }, 1600);
        });
        shell.node.appendChild(frame);
        document.body.appendChild(shell.node);
        document.body.appendChild(shell.mute);
        trailer.node = shell.node;
        trailer.mute = shell.mute;
        trailer.media = frame;
        trailer.kind = 'youtube';
    }

    var scheduleTrailer = safe('trailer', function () {
        var client = api();
        var page = visiblePage('.itemDetailPage');
        var itemId = page ? hashParam('id') : null;
        if (!config.EnableTrailers || !client || !page || !itemId || !isDesktop() || reducedMotion) {
            if (trailer.itemId) {
                stopTrailer();
            }
            return;
        }
        if (trailer.itemId === itemId || trailer.doneFor === itemId) {
            return;
        }
        stopTrailer();
        trailer.itemId = itemId;
        trailer.doneFor = itemId;
        trailer.muted = true;
        trailer.timer = setTimeout(safe('trailer start', function () {
            trailer.timer = null;
            if (trailer.itemId !== itemId || hashParam('id') !== itemId) {
                return;
            }
            var userId = client.getCurrentUserId();
            getItemCached(client, itemId).then(safe('trailer pick', function (item) {
                if (trailer.itemId !== itemId || !item || (item.Type !== 'Movie' && item.Type !== 'Series')) {
                    return;
                }
                var yt = null;
                (item.RemoteTrailers || []).some(function (t) { yt = youTubeId(t.Url); return !!yt; });
                var youtube = config.AllowYouTubeTrailers && yt ? function () { playYouTube(itemId, yt); } : null;

                if (item.LocalTrailerCount > 0 && typeof client.getLocalTrailers === 'function') {
                    client.getLocalTrailers(userId, itemId).then(safe('local trailer', function (list) {
                        if (trailer.itemId !== itemId) {
                            return;
                        }
                        if (list && list.length) {
                            playLocal(client, itemId, list[0].Id, youtube);
                        } else if (youtube) {
                            youtube();
                        }
                    }), function () { if (youtube && trailer.itemId === itemId) { youtube(); } });
                } else if (youtube) {
                    youtube();
                }
            }), function () { /* no item, no trailer */ });
        }), config.TrailerDelaySeconds * 1000);
    });

    /* Fade the trailer out once the viewer scrolls down into the page; YouTube is paused, not just hidden. */
    window.addEventListener('scroll', safe('trailer scroll', function () {
        if (!trailer.node) {
            return;
        }
        var away = window.scrollY > window.innerHeight * 0.55;
        trailer.node.classList.toggle('is-scrolled', away);
        if (trailer.mute) {
            trailer.mute.classList.toggle('is-scrolled', away);
        }
        if (trailer.kind === 'local' && trailer.media) {
            if (away) { trailer.media.pause(); } else if (trailer.media.paused) { trailer.media.play().catch(function () {}); }
        } else {
            ytCommand(away ? 'pauseVideo' : 'playVideo');
        }
    }), { passive: true });

    document.addEventListener('visibilitychange', safe('visibility', function () {
        if (document.hidden && trailer.node) {
            stopTrailer();
        }
    }));

    /* ------------------------------------------------------------------ */
    /* Card tilt                                                            */
    /* ------------------------------------------------------------------ */

    var tilt = { card: null, frame: 0, x: 0, y: 0, target: null };

    function resetTilt() {
        if (tilt.card) {
            tilt.card.classList.remove('au-tilting');
            tilt.card.style.removeProperty('--au-rx');
            tilt.card.style.removeProperty('--au-ry');
            tilt.card.style.removeProperty('--au-mx');
            tilt.card.style.removeProperty('--au-my');
            tilt.card = null;
        }
    }

    function applyTilt() {
        tilt.frame = 0;
        var card = tilt.target && tilt.target.closest ? tilt.target.closest('.card') : null;
        var scalable = card ? card.querySelector('.cardScalable') : null;
        if (scalable !== tilt.card) {
            resetTilt();
        }
        if (!scalable || !isDesktop()) {
            return;
        }
        var r = scalable.getBoundingClientRect();
        if (!r.width || !r.height) {
            return;
        }
        var px = Math.min(Math.max((tilt.x - r.left) / r.width, 0), 1);
        var py = Math.min(Math.max((tilt.y - r.top) / r.height, 0), 1);
        scalable.style.setProperty('--au-ry', ((px - 0.5) * 10).toFixed(2) + 'deg');
        scalable.style.setProperty('--au-rx', ((0.5 - py) * 8).toFixed(2) + 'deg');
        scalable.style.setProperty('--au-mx', (px * 100).toFixed(1) + '%');
        scalable.style.setProperty('--au-my', (py * 100).toFixed(1) + '%');
        scalable.classList.add('au-tilting');
        tilt.card = scalable;
    }

    function enableTilt() {
        if (!config.EnableTilt || !finePointer || reducedMotion) {
            return;
        }
        document.documentElement.classList.add('au-tilt');
        document.addEventListener('mousemove', function (e) {
            tilt.x = e.clientX;
            tilt.y = e.clientY;
            tilt.target = e.target;
            if (!tilt.frame) {
                tilt.frame = requestAnimationFrame(safe('tilt', applyTilt));
            }
        }, { passive: true });
        document.addEventListener('mouseleave', resetTilt);
        window.addEventListener('scroll', resetTilt, { passive: true });
    }

    /* ------------------------------------------------------------------ */
    /* Genre chips                                                          */
    /* ------------------------------------------------------------------ */

    var genreChips = safe('genre chips', function () {
        if (!config.EnableGenreChips) {
            return;
        }
        var page = visiblePage('.itemDetailPage');
        if (!page) {
            return;
        }
        var groups = page.querySelectorAll('.detailsGroupItem:not(.au-genres)');
        for (var i = 0; i < groups.length; i++) {
            var label = groups[i].firstElementChild;
            if (!label || !/^genres?$/i.test((label.textContent || '').trim())) {
                continue;
            }
            /* the list is the smallest element holding every genre link (12.x wraps each link in its own span) */
            var links = groups[i].querySelectorAll('a');
            if (!links.length) {
                continue;
            }
            var list = links[0].parentElement;
            while (list && list !== groups[i] && list.querySelectorAll('a').length < links.length) {
                list = list.parentElement;
            }
            if (!list || list === groups[i]) {
                continue;
            }
            groups[i].classList.add('au-genres');
            list.classList.add('au-genre-list');
        }
    });


    /* ------------------------------------------------------------------ */
    /* Up Next: the title's logo over each still in Continue Watching / Next Up */
    /* ------------------------------------------------------------------ */

    var UPNEXT_TITLES = /^(continue watching|next up)$/i;

    var upNext = safe('up next', function () {
        var client = api();
        var page = visiblePage('.homePage, #indexPage');
        if (!config.EnableUpNext || !client || !page) {
            return;
        }
        var sections = page.querySelectorAll('.homeSectionsContainer .verticalSection');
        var want = [];
        for (var i = 0; i < sections.length; i++) {
            var title = sections[i].querySelector('.sectionTitle');
            if (!title || !UPNEXT_TITLES.test((title.textContent || '').trim())) {
                continue;
            }
            sections[i].classList.add('au-upnext');
            var cards = sections[i].querySelectorAll('.card[data-id]:not(.au-upnext-seen)');
            for (var c = 0; c < cards.length; c++) {
                cards[c].classList.add('au-upnext-seen');
                want.push(cards[c]);
            }
        }
        if (!want.length) {
            return;
        }
        var ids = want.map(function (card) { return card.getAttribute('data-id'); }).filter(function (id) { return /^[0-9a-f]{32}$/i.test(id); });
        client.getItems(client.getCurrentUserId(), { Ids: ids.join(','), EnableImageTypes: 'Logo,Backdrop', ImageTypeLimit: 1 }).then(safe('up next logos', function (result) {
            var byId = {};
            ((result && result.Items) || []).forEach(function (it) { byId[it.Id] = it; });
            want.forEach(function (card) {
                var it = byId[card.getAttribute('data-id')];
                var scalable = card.querySelector('.cardScalable');
                /* Layers go inside the image container, ahead of Jellyfin's own children (progress bar, footer),
                   so those keep painting on top. */
                var holder = (scalable && scalable.querySelector('.cardImageContainer')) || scalable;
                if (!it || !holder || holder.querySelector('.au-upnext-logo')) {
                    return;
                }
                var logoId = it.ImageTags && it.ImageTags.Logo ? it.Id : it.ParentLogoItemId;
                var logoTag = it.ImageTags && it.ImageTags.Logo ? it.ImageTags.Logo : it.ParentLogoImageTag;
                /* Jellyfin's thumbs often have the title printed on them; under a logo that reads twice. Use the
                   title's clean backdrop instead, and leave the card alone if there isn't one. */
                var artId = it.BackdropImageTags && it.BackdropImageTags.length ? it.Id : it.ParentBackdropItemId;
                var artTag = it.BackdropImageTags && it.BackdropImageTags.length ? it.BackdropImageTags[0]
                    : (it.ParentBackdropImageTags && it.ParentBackdropImageTags[0]);
                if (!logoId || !logoTag || !artId || !artTag) {
                    return;
                }
                var art = el('div', 'au-upnext-art');
                art.style.backgroundImage = 'url("' + client.getImageUrl(artId, { type: 'Backdrop', maxWidth: 780, tag: artTag }) + '")';
                var img = el('img', 'au-upnext-logo');
                img.alt = '';
                img.loading = 'lazy';
                img.src = client.getImageUrl(logoId, { type: 'Logo', maxWidth: 500, tag: logoTag });
                img.addEventListener('error', function () { img.remove(); });
                var first = holder.firstChild;
                holder.insertBefore(art, first);
                holder.insertBefore(el('div', 'au-upnext-scrim'), first);
                holder.insertBefore(img, first);
                card.classList.add('au-upnext-has-logo');
            });
        }), function () { /* no logos: cards stay as Jellyfin drew them */ });
    });

    /* ------------------------------------------------------------------ */
    /* Quality badges: 4K / Dolby Vision / HDR10 / Dolby Atmos / 5.1 / CC   */
    /* ------------------------------------------------------------------ */

    function badgesFor(item) {
        var source = (item.MediaSources || [])[0];
        var streams = (source && source.MediaStreams) || item.MediaStreams || [];
        var video = null, audio = null, subs = false;
        streams.forEach(function (st) {
            if (st.Type === 'Video' && !video) {
                video = st;
            } else if (st.Type === 'Audio' && (!audio || st.IsDefault)) {
                audio = st;
            } else if (st.Type === 'Subtitle') {
                subs = true;
            }
        });
        var out = [];
        if (video) {
            var w = video.Width || 0, h = video.Height || 0;
            out.push(w >= 3200 || h >= 2000 ? '4K' : (w >= 1200 || h >= 700 ? 'HD' : 'SD'));
            var range = (video.VideoRangeType || '') + ' ' + (video.VideoRange || '');
            if (/dovi/i.test(range)) {
                out.push('Dolby Vision');
            } else if (/hdr10plus/i.test(range)) {
                out.push('HDR10+');
            } else if (/hdr10/i.test(range)) {
                out.push('HDR10');
            } else if (/hlg/i.test(range)) {
                out.push('HLG');
            } else if (/\bhdr\b/i.test(range)) {
                out.push('HDR');
            }
        }
        if (audio) {
            var desc = [audio.Profile, audio.DisplayTitle, audio.Title].join(' ');
            if (/atmos/i.test(desc)) {
                out.push('Dolby Atmos');
            } else if (/dts[:\- ]?x\b/i.test(desc)) {
                out.push('DTS:X');
            } else if ((audio.Channels || 0) >= 8) {
                out.push('7.1');
            } else if ((audio.Channels || 0) >= 6) {
                out.push('5.1');
            }
        }
        if (subs) {
            out.push('CC');
        }
        return out;
    }

    function showBadges(page, forId, list) {
        var line = page.querySelector('.itemMiscInfo-primary');
        if (!line || !list.length) {
            return;
        }
        var old = page.querySelector('.au-badges');
        if (old) {
            if (old.getAttribute('data-for') === forId) {
                return;
            }
            old.remove();
        }
        var box = el('div', 'au-badges');
        box.setAttribute('data-for', forId);
        list.forEach(function (b) { box.appendChild(el('span', 'au-badge', b)); });
        line.parentNode.insertBefore(box, line.nextSibling);
    }

    var badges = safe('badges', function (client, page, item) {
        if (!config.EnableBadges) {
            return;
        }
        if (item.Type === 'Movie' || item.Type === 'Episode' || item.Type === 'Video' || item.Type === 'MusicVideo') {
            showBadges(page, item.Id, badgesFor(item));
        } else if (item.Type === 'Series') {
            /* a show has no file of its own: describe it by the episode you would watch next (or the first one) */
            nextEpisode(client, item.Id).then(safe('series badges', function (ep) {
                if (!ep || hashParam('id') !== item.Id) {
                    return;
                }
                getItemCached(client, ep.Id).then(safe('series badges item', function (full) {
                    if (hashParam('id') === item.Id) {
                        showBadges(page, item.Id, badgesFor(full));
                    }
                }), function () {});
            }), function () {});
        }
    });

    /* ------------------------------------------------------------------ */
    /* Episodes row with season tabs (show pages)                          */
    /* ------------------------------------------------------------------ */

    var nextUpCache = {};

    /* The episode to resume or start next; falls back to the first episode of the first real season. */
    function nextEpisode(client, seriesId) {
        var hit = nextUpCache[seriesId];
        if (hit && Date.now() - hit.at < 60000) {
            return hit.promise;
        }
        var userId = client.getCurrentUserId();
        var promise = client.getNextUpEpisodes({ SeriesId: seriesId, UserId: userId, Limit: 1, EnableResumable: true }).then(function (r) {
            var ep = r && r.Items && r.Items[0];
            if (ep) {
                return ep;
            }
            return client.getEpisodes(seriesId, { userId: userId, Limit: 1, IsMissing: false }).then(function (e) {
                return (e && e.Items && e.Items[0]) || null;
            });
        });
        nextUpCache[seriesId] = { at: Date.now(), promise: promise };
        promise.catch(function () { delete nextUpCache[seriesId]; });
        return promise;
    }

    function seasonLabel(season) {
        return season.IndexNumber === 0 ? (season.Name || 'Specials') : (season.Name || ('Season ' + season.IndexNumber));
    }

    function episodeCard(client, series, ep) {
        var card = el('div', 'au-ep');
        card.tabIndex = 0;
        card.setAttribute('role', 'link');
        card.setAttribute('data-id', ep.Id);
        card.setAttribute('aria-label', (ep.IndexNumber ? 'Episode ' + ep.IndexNumber + ': ' : '') + (ep.Name || ''));

        var img = el('div', 'au-ep-img');
        var tag = ep.ImageTags && ep.ImageTags.Primary;
        var url = tag
            ? client.getImageUrl(ep.Id, { type: 'Primary', maxWidth: 640, tag: tag })
            : (series.BackdropImageTags && series.BackdropImageTags.length
                ? client.getImageUrl(series.Id, { type: 'Backdrop', maxWidth: 640, tag: series.BackdropImageTags[0] }) : null);
        if (url) {
            img.style.backgroundImage = 'url("' + url + '")';
        }
        var ud = ep.UserData || {};
        if (ud.Played) {
            var done = el('span', 'au-ep-played material-icons', 'check');
            img.appendChild(done);
        } else if (ud.PlayedPercentage) {
            var bar = el('div', 'au-ep-progress');
            var fill = el('div', 'au-ep-progress-fill');
            fill.style.width = Math.min(100, Math.max(2, ud.PlayedPercentage)).toFixed(1) + '%';
            bar.appendChild(fill);
            img.appendChild(bar);
        }
        var play = el('button', 'au-ep-play');
        play.type = 'button';
        play.setAttribute('aria-label', 'Play ' + (ep.Name || 'episode'));
        play.appendChild(el('span', 'material-icons', 'play_arrow'));
        play.addEventListener('click', function (e) {
            e.stopPropagation();
            openDetails(ep.Id, true);
        });
        img.appendChild(play);
        card.appendChild(img);

        var label = ep.IndexNumber !== undefined && ep.IndexNumber !== null ? 'Episode ' + ep.IndexNumber : '';
        if (ep.IndexNumberEnd) {
            label += '–' + ep.IndexNumberEnd;
        }
        if (label) {
            card.appendChild(el('div', 'au-ep-num', label));
        }
        card.appendChild(el('div', 'au-ep-name', ep.Name || ''));
        if (ep.Overview) {
            card.appendChild(el('p', 'au-ep-overview', ep.Overview));
        }
        var meta = [];
        if (ep.RunTimeTicks) {
            meta.push(formatRuntime(ep.RunTimeTicks));
        }
        if (ep.PremiereDate) {
            var d = new Date(ep.PremiereDate);
            if (!isNaN(d)) {
                meta.push(d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }));
            }
        }
        if (meta.length) {
            card.appendChild(el('div', 'au-ep-meta', meta.join(' · ')));
        }

        card.addEventListener('click', function () { openDetails(ep.Id, false); });
        card.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                openDetails(ep.Id, false);
            }
        });
        return card;
    }

    function loadSeason(client, series, shelf, season, focusId) {
        var row = shelf.querySelector('.au-ep-row');
        shelf.querySelectorAll('.au-season-tab').forEach(function (t) {
            var on = t.getAttribute('data-season') === season.Id;
            t.classList.toggle('is-active', on);
            t.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        row.setAttribute('aria-busy', 'true');
        client.getEpisodes(series.Id, {
            userId: client.getCurrentUserId(), seasonId: season.Id, Fields: 'Overview,PremiereDate', EnableImageTypes: 'Primary', IsMissing: false
        }).then(safe('season episodes', function (r) {
            if (!document.contains(row) || shelf.getAttribute('data-season') !== season.Id) {
                return;
            }
            row.textContent = '';
            ((r && r.Items) || []).forEach(function (ep) { row.appendChild(episodeCard(client, series, ep)); });
            row.removeAttribute('aria-busy');
            row.scrollLeft = 0;
            var target = focusId && row.querySelector('.au-ep[data-id="' + focusId + '"]');
            if (target) {
                target.classList.add('is-next');
                /* Line the next episode up with the start of the row. Near the end of a season the row can't scroll that
                   far, so land on the last whole-card boundary instead: a partial card then peeks in on the right
                   rather than being cut off on the left. Measured on screen, not via offsetParent. */
                var rowLeft = row.getBoundingClientRect().left;
                var startOf = function (card) { return row.scrollLeft + card.getBoundingClientRect().left - rowLeft - 4; };
                var max = row.scrollWidth - row.clientWidth;
                var want = startOf(target);
                if (want > max) {
                    var cards = row.querySelectorAll('.au-ep');
                    for (var k = cards.length - 1; k >= 0; k--) {
                        if (startOf(cards[k]) <= max) {
                            want = startOf(cards[k]);
                            break;
                        }
                    }
                }
                row.scrollLeft = Math.max(0, want);
            }
        }), function () { row.removeAttribute('aria-busy'); });
    }

    function restoreJellyfinSeasons(page) {
        page.querySelectorAll('.au-episodes').forEach(function (n) { n.remove(); });
        page.querySelectorAll('.au-replaced').forEach(function (n) { n.classList.remove('au-replaced'); });
    }

    var episodeShelf = safe('episodes', function (client, page, item) {
        if (!config.EnableEpisodeShelf || item.Type !== 'Series') {
            restoreJellyfinSeasons(page);
            return;
        }
        var anchor = page.querySelector('#listChildrenCollapsible');
        if (!anchor || page.querySelector('.au-episodes[data-series="' + item.Id + '"]')) {
            return;
        }
        restoreJellyfinSeasons(page);

        var shelf = el('section', 'au-episodes verticalSection');
        shelf.setAttribute('data-series', item.Id);
        var head = el('div', 'au-ep-head');
        head.appendChild(el('h2', 'sectionTitle sectionTitle-cards au-ep-title', 'Episodes'));
        var arrows = el('div', 'au-ep-arrows');
        [['chevron_left', -1, 'Earlier episodes'], ['chevron_right', 1, 'Later episodes']].forEach(function (a) {
            var b = el('button', 'au-ep-arrow');
            b.type = 'button';
            b.setAttribute('aria-label', a[2]);
            b.appendChild(el('span', 'material-icons', a[0]));
            b.addEventListener('click', function () {
                var row = shelf.querySelector('.au-ep-row');
                row.scrollBy({ left: a[1] * row.clientWidth * 0.8, behavior: 'smooth' });
            });
            arrows.appendChild(b);
        });
        head.appendChild(arrows);
        shelf.appendChild(head);
        var tabs = el('div', 'au-season-tabs');
        tabs.setAttribute('role', 'tablist');
        shelf.appendChild(tabs);
        var row = el('div', 'au-ep-row');
        row.setAttribute('role', 'list');
        shelf.appendChild(row);

        var userId = client.getCurrentUserId();
        Promise.all([
            client.getSeasons(item.Id, { userId: userId }),
            nextEpisode(client, item.Id).catch(function () { return null; })
        ]).then(safe('episodes render', function (res) {
            var seasons = ((res[0] && res[0].Items) || []);
            var next = res[1];
            if (!seasons.length || hashParam('id') !== item.Id || !document.contains(anchor)) {
                return;
            }
            var start = (next && seasons.filter(function (s) { return s.Id === next.SeasonId; })[0])
                || seasons.filter(function (s) { return s.IndexNumber > 0; })[0] || seasons[0];
            seasons.forEach(function (season) {
                var t = el('button', 'au-season-tab', seasonLabel(season));
                t.type = 'button';
                t.setAttribute('role', 'tab');
                t.setAttribute('data-season', season.Id);
                t.addEventListener('click', function () {
                    shelf.setAttribute('data-season', season.Id);
                    loadSeason(client, item, shelf, season, next && next.SeasonId === season.Id ? next.Id : null);
                });
                tabs.appendChild(t);
            });
            if (seasons.length === 1) {
                tabs.classList.add('is-single');
            }
            anchor.parentNode.insertBefore(shelf, anchor);
            /* the row replaces Jellyfin's season cards and its separate Next Up block */
            anchor.classList.add('au-replaced');
            page.querySelectorAll('.detailPagePrimaryContent .verticalSection, .detailPagePrimaryContent .detailSection > *').forEach(function (sec) {
                var h = sec.querySelector('h2');
                if (sec !== shelf && sec !== anchor && h && /^next up$/i.test((h.textContent || '').trim()) && !sec.contains(shelf)) {
                    sec.classList.add('au-replaced');
                }
            });
            shelf.setAttribute('data-season', start.Id);
            loadSeason(client, item, shelf, start, next && next.SeasonId === start.Id ? next.Id : null);
        }), function (err) { console.warn(LOG, 'could not load seasons', err); });
    });

    /* Everything on a details page that needs the item itself. */
    var detailsFeatures = safe('details features', function () {
        var client = api();
        var page = visiblePage('.itemDetailPage');
        var id = page ? hashParam('id') : null;
        if (!client || !page || !id) {
            return;
        }
        if (page.getAttribute('data-au-item') === id && page.querySelector('.au-badges, .au-episodes')) {
            return;
        }
        page.setAttribute('data-au-item', id);
        getItemCached(client, id).then(safe('details item', function (item) {
            if (!item || hashParam('id') !== id) {
                return;
            }
            badges(client, page, item);
            episodeShelf(client, page, item);
        }), function () {});
    });

    /* ------------------------------------------------------------------ */
    /* Wiring                                                               */
    /* ------------------------------------------------------------------ */

    var pending = 0;

    var refresh = safe('refresh', function () {
        pending = 0;
        if (!api()) {
            return;
        }
        ensureHero();
        heroGone();
        genreChips();
        upNext();
        detailsFeatures();
        scheduleTrailer();
        autoplayFromHero();
    });

    function queueRefresh() {
        if (!pending) {
            pending = setTimeout(refresh, 250);
        }
    }

    function start() {
        /* read-only handle for troubleshooting from the browser console */
        window.__aurora = { config: function () { return config; }, hero: hero, trailer: trailer, refresh: refresh };
        document.documentElement.classList.add('au-js');
        enableTilt();
        window.addEventListener('hashchange', function () {
            stopTrailer();
            trailer.doneFor = null;
            queueRefresh();
        });
        window.addEventListener('resize', safe('resize', placeHero));
        new MutationObserver(queueRefresh).observe(document.body, { childList: true, subtree: true });
        queueRefresh();
    }

    function loadConfigThenStart() {
        fetch(BASE + '/Aurora/config', { credentials: 'same-origin' }).then(function (r) {
            return r.ok ? r.json() : DEFAULTS;
        }).then(function (c) {
            config = Object.assign({}, DEFAULTS, c || {});
        }, function () {
            config = DEFAULTS;
        }).then(safe('start', start));
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', loadConfigThenStart);
    } else {
        loadConfigThenStart();
    }
})();
