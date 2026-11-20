// youtubeaudio_shared.js
// Holds the tab list, the page wrapper, and the translations, and passes on each Base builder the pages use.

import {
    createConfigPage,
    createAudioPlayer,
    createCheckbox,
    createCheckboxList,
    createChoiceGroup,
    createFilePicker,
    createFilteredList,
    createImagePicker,
    createMediaCard,
    createOrderedList,
    createSaveBar,
    confirmDialog,
    loadTranslations,
    promptDialog
} from './configurationpage?name=youtubeaudio_jpkribs_shared.js';

export {
    createAudioPlayer,
    createCheckbox,
    createCheckboxList,
    createChoiceGroup,
    createFilePicker,
    createFilteredList,
    createImagePicker,
    createMediaCard,
    createOrderedList,
    createSaveBar,
    confirmDialog,
    promptDialog
};

// STATUS_STATES
// Maps each queue status code to the Base badge state that colors it.
export var STATUS_STATES = { 0: 'Queued', 1: 'Downloading', 2: 'Downloaded', 3: 'Imported', 4: 'Error' };

// TRANSLATIONS
// The plugin's strings in the viewer's language, loaded once for every page.
var TRANSLATIONS = loadTranslations('YouTubeAudio/Translations');

// _t
// The loaded strings, or null until they arrive.
var _t = null;
TRANSLATIONS.then(function (t) { _t = t; });

// tr
// Reads one string, or its English while the strings load.
//
// Inputs
// key [string]: The translation key.
// english [string]: The US English, shown until the strings load.
//
// Output
// [string]: The text.
function tr(key, english) {
    return _t ? _t.tr(key, english) : english;
}

// statusText
// Names a queue status in the viewer's language.
//
// Inputs
// t [object]: The translations handle.
// statusCode [number]: The item's StatusCode.
//
// Output
// [string]: Such as "Queued".
export function statusText(t, statusCode) {
    switch (statusCode) {
        case 1: return t.tr('StatusDownloading');
        case 2: return t.tr('StatusDownloaded');
        case 3: return t.tr('StatusImported');
        case 4: return t.tr('HeaderError');
        default: return t.tr('StatusQueued');
    }
}

// countText
// Says a count in a sentence with the right plural, one key for one and another for any other count.
//
// Inputs
// t [object]: The translations handle.
// count [number]: How many.
// oneKey [string]: The key for a count of 1.
// manyKey [string]: The key for any other count, with {0} for the count.
// ...rest [any]: The values for {1}, {2} and on.
//
// Output
// [string]: Such as "Deleted 3 items."
export function countText(t, count, oneKey, manyKey, ...rest) {
    return t.format(count === 1 ? oneKey : manyKey, count, ...rest);
}

// componentText
// Gives the text options of each Base component the pages use, in the viewer's language.
//
// Inputs
// t [object]: The translations handle.
//
// Output
// [object]: Options by component, such as filteredList and saveBar, to merge into its options.
export function componentText(t) {
    return {
        filteredList: {
            selectAllText: t.tr('SelectAll'),
            selectedText: t.tr('MessageCountSelected'),
            selectText: t.tr('Select'),
            allText: t.tr('All'),
            summaryText: t.tr('MessageShowingCount')
        },
        checkboxList: { selectAllText: t.tr('SelectAll'), emptyMessage: t.tr('MessageNoItems') },
        saveBar: { saveText: t.tr('Save'), pendingText: t.tr('MessageUnsavedChanges'), savingText: t.tr('MessageSaving') },
        confirm: { cancelText: t.tr('ButtonCancel') },
        imagePicker: {
            chooseText: t.tr('ButtonSelectImage'),
            replaceText: t.tr('ButtonReplace'),
            removeText: t.tr('ButtonRemove'),
            emptyText: t.tr('MessageNoCover'),
            tooLargeText: t.tr('ErrorImageLargerThan'),
            readErrorText: t.tr('ErrorReadFile')
        },
        audioPlayer: { playText: t.tr('Play'), pauseText: t.tr('ButtonPause'), seekText: t.tr('ButtonSeek'), resetText: t.tr('Reset') },
        filePicker: {
            dropText: t.tr('MessageDropFile'),
            removeLabel: t.tr('ButtonRemoveValue'),
            typeText: t.tr('ErrorFileType'),
            sizeLimitText: t.tr('ErrorFileSize'),
            singleText: t.tr('MessageSelectOneFile')
        },
        comboBox: { noResultsText: t.tr('MessageNoMatches'), errorText: t.tr('ErrorSearch') },
        userMultiSelector: { placeholder: t.tr('Search'), selectAllText: t.tr('SelectAll'), adminText: t.tr('LabelAdmin') }
    };
}

// fillLinks
// Sets a sentence that holds links, putting each link in its {0}, {1} placeholder so the words stay in the language's order.
//
// Inputs
// el [HTMLElement]: The element whose text is set.
// text [string]: The translated sentence.
// links [HTMLElement[]]: The links, in placeholder order.
export function fillLinks(el, text, links) {
    el.textContent = '';
    String(text).split(/(\{\d+\})/).forEach(function (part) {
        var match = /^\{(\d+)\}$/.exec(part);
        el.appendChild(match && links[Number(match[1])] ? links[Number(match[1])] : document.createTextNode(part));
    });
}

// getTabs
// Returns the dashboard tabs of the plugin's pages, named in the viewer's language once the strings have loaded.
//
// Output
// [object[]]: The tabs, in order.
export function getTabs() {
    return [
        { href: 'configurationpage?name=youtubeaudio_download', name: tr('Download', 'Download') },
        { href: 'configurationpage?name=youtubeaudio_import', name: tr('Import', 'Import') },
        { href: 'configurationpage?name=youtubeaudio_settings', name: tr('Settings', 'Settings') }
    ];
}

// createPage
// Wires a config page through Base's page lifecycle with the plugin's tabs. The page's bind and load wait for the strings,
// which are applied to its markup first, and the helper bag gets the translations as t and the component text as text.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the module's default export.
// opts [object]: The page's createConfigPage options, such as tabIndex, bind, load, and onHide.
//
// Output
// [object]: The page handle, whose shared bag also has t and text.
export function createPage(view, opts) {
    var bound = null;
    return createConfigPage(view, Object.assign({
        pluginId: '7323ea64-a200-4265-ab8f-e7ae27d06c38',
        apiPrefix: 'YouTubeAudio',
        tabsKey: 'youtubeaudio',
        tabs: getTabs
    }, opts, {
        bind: function (shared, handle) {
            bound = TRANSLATIONS.then(function (t) {
                shared.t = t;
                shared.text = componentText(t);
                t.apply(view);
                if (opts.bind) opts.bind(shared, handle);
            });
        },
        load: function (shared, handle) {
            bound.then(function () {
                if (opts.load) opts.load(shared, handle);
            });
        }
    }));
}
