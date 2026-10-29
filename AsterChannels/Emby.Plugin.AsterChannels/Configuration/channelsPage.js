// The Channels page's controller, in the form Emby's dashboard loads (an AMD module returning a view, as Emby's own
// plugins do). It loads the tag rules (channel-tags.js, shared with the Jellyfin plugin) and the page (channels.js),
// then hands the page its element.
define(['baseView', 'pluginManager', 'emby-button'], function (BaseView, pluginManager) {
    'use strict';

    function load(name) {
        return require([pluginManager.getConfigurationResourceUrl(name)]);
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var status = view.querySelector('#acStatus');
        this.ready = load('asterchanneltagsjs').then(function () {
            return load('asterchannelsviewjs');
        }).then(function () {
            if (!window.AsterChannelTags || !window.AsterChannelsPage) {
                throw new Error('The page could not load its scripts.');
            }
            return window.AsterChannelsPage.create(view);
        });
        this.ready.catch(function (error) {
            status.textContent = (error && error.message) || String(error);
            status.className = 'acStatus error';
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this.ready.then(function (controller) { controller.reload(); }, function () {});
    };

    return View;
});
