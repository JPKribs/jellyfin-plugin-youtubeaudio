// youtubeaudio_shared.js
// Holds the tab list and the page wrapper, and passes on each Base builder the pages use.

import {
    createConfigPage,
    createAudioPlayer,
    createCheckboxList,
    createChoiceGroup,
    createFilePicker,
    createFilteredList,
    createImagePicker,
    createMediaCard,
    createOrderedList,
    confirmDialog,
    promptDialog
} from './configurationpage?name=youtubeaudio_jpkribs_shared.js';

export {
    createAudioPlayer,
    createCheckboxList,
    createChoiceGroup,
    createFilePicker,
    createFilteredList,
    createImagePicker,
    createMediaCard,
    createOrderedList,
    confirmDialog,
    promptDialog
};

// STATUS_STATES
// Maps each queue status code to the Base badge state that colors it.
export var STATUS_STATES = { 0: 'Queued', 1: 'Downloading', 2: 'Downloaded', 3: 'Imported', 4: 'Error' };

// getTabs
// Returns the dashboard tabs of the plugin's pages.
//
// Output
// [object[]]: The tabs, in order.
export function getTabs() {
    return [
        { href: 'configurationpage?name=youtubeaudio_download', name: 'Download' },
        { href: 'configurationpage?name=youtubeaudio_import', name: 'Import' },
        { href: 'configurationpage?name=youtubeaudio_settings', name: 'Settings' }
    ];
}

// createPage
// Wires a config page through Base's page lifecycle with the plugin's tabs, and adds the queue status badge to its helper bag.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the module's default export.
// opts [object]: The page's createConfigPage options, such as tabIndex, bind, load, and onHide.
//
// Output
// [object]: The page handle, whose shared bag also has getStatusBadge.
export function createPage(view, opts) {
    var page = createConfigPage(view, Object.assign({
        pluginId: '7323ea64-a200-4265-ab8f-e7ae27d06c38',
        apiPrefix: 'YouTubeAudio',
        tabsKey: 'youtubeaudio',
        tabs: getTabs()
    }, opts));

    var shared = page.shared;

    // getStatusBadge
    // Draws a queue item's status as a Base status badge.
    //
    // Inputs
    // statusCode [number]: The item's StatusCode.
    // statusText [string]: The item's Status, shown in the badge.
    //
    // Output
    // [string]: The badge HTML.
    shared.getStatusBadge = function (statusCode, statusText) {
        return shared.statusBadge(statusText, STATUS_STATES[statusCode] || 'Queued');
    };

    return page;
}
