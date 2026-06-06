// YouTube Audio shared module: wraps the JPKribs.Jellyfin.Base kit and adds plugin specifics.

import { createShared as baseCreateShared } from '/web/configurationpage?name=youtubeaudio_jpkribs_shared.js';

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

// createShared
// Builds the per view helper bag from the shared kit and adds the YouTube Audio status badge.
//
// Param: view | the page element the helpers operate on
export function createShared(view) {
    var shared = baseCreateShared(view, '7323ea64-a200-4265-ab8f-e7ae27d06c38', 'YouTubeAudio');

    // Maps the plugin's queue status codes onto the base status-badge states and renders via the shared kit.
    shared.getStatusBadge = function (statusCode, statusText) {
        var map = { 0: 'Queued', 1: 'Downloading', 2: 'Downloaded', 3: 'Imported', 4: 'Error' };
        return this.statusBadge(statusText, map[statusCode] || 'Queued');
    };

    return shared;
}
