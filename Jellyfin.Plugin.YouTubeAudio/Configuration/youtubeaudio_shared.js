// YouTube Audio shared module: wraps the JPKribs.Jellyfin.Base kit and adds plugin specifics.

import { createConfigPage, createChoiceGroup, confirmDialog } from './configurationpage?name=youtubeaudio_jpkribs_shared.js';

// getTabs
// Returns the dashboard tab descriptors for the plugin's pages.
//
// No parameters
export function getTabs() {
    return [
        { href: 'configurationpage?name=youtubeaudio_download', name: 'Download' },
        { href: 'configurationpage?name=youtubeaudio_import', name: 'Import' },
        { href: 'configurationpage?name=youtubeaudio_settings', name: 'Settings' }
    ];
}

// createPage
// Wires a config page through the shared kit's page lifecycle with the plugin's tabs, and adds the
// YouTube Audio status badge to its helper bag.
//
// Param: view | the page element the framework passes to the module's default export
// Param: opts | the page's createConfigPage options (tabIndex, bind, load, onHide)
export function createPage(view, opts) {
    var page = createConfigPage(view, Object.assign({
        pluginId: '7323ea64-a200-4265-ab8f-e7ae27d06c38',
        apiPrefix: 'YouTubeAudio',
        tabsKey: 'youtubeaudio',
        tabs: getTabs()
    }, opts));

    var shared = page.shared;
    shared.createChoiceGroup = createChoiceGroup;
    shared.confirmDialog = confirmDialog;

    // Maps the plugin's queue status codes onto the base status-badge states and renders via the shared kit.
    shared.getStatusBadge = function (statusCode, statusText) {
        var map = { 0: 'Queued', 1: 'Downloading', 2: 'Downloaded', 3: 'Imported', 4: 'Error' };
        return this.statusBadge(statusText, map[statusCode] || 'Queued');
    };

    return page;
}
