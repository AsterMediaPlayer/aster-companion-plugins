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
        EnableTilt: true, EnableGenreChips: true
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
            client.getItem(userId, itemId).then(safe('trailer pick', function (item) {
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
