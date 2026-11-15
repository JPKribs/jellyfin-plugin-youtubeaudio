// youtubeaudio_settings.js
// Drives the Settings tab, where each section saves its own part of the plugin config.

import { createPage, createOrderedList, confirmDialog } from './configurationpage?name=youtubeaudio_shared.js';

// COVER_TEXT_NAMES
// The text lines a generated cover can show, by stored key.
var COVER_TEXT_NAMES = { Artist: 'Artist Name', Album: 'Album Name', Year: 'Year' };

// parseCoverText
// Turns the stored cover text settings into ordered rows, adding any line the stored order lacks.
//
// Inputs
// order [string]: Keys in display order, separated by commas.
// shown [string]: Keys that are turned on, separated by commas.
//
// Output
// [object[]]: One { key, on } row per line, in order.
function parseCoverText(order, shown) {
    var keys = String(order || 'Album,Artist,Year').split(',').filter(function (k) { return COVER_TEXT_NAMES[k]; });
    Object.keys(COVER_TEXT_NAMES).forEach(function (k) { if (keys.indexOf(k) === -1) keys.push(k); });
    var on = String(shown == null ? 'Album' : shown).split(',');
    return keys.map(function (k) { return { key: k, on: on.indexOf(k) !== -1 }; });
}

// formatCoverText
// Turns ordered cover text rows back into the stored settings.
//
// Inputs
// rows [object[]]: The { key, on } rows, in order.
//
// Output
// [object]: { order, shown } as keys separated by commas.
function formatCoverText(rows) {
    return {
        order: rows.map(function (r) { return r.key; }).join(','),
        shown: rows.filter(function (r) { return r.on; }).map(function (r) { return r.key; }).join(',')
    };
}

export default function (view) {
    'use strict';

    var Shared = null;
    var _approvedUsers = null;
    var _coverText = null;

    // ensureApprovedUsers
    // Builds the approved users picker once, limited to users who are not administrators.
    //
    // Output
    // [object]: The picker handle, or null when its container is missing.
    function ensureApprovedUsers() {
        if (_approvedUsers) return _approvedUsers;
        var container = Shared.getEl('approvedUsersList');
        if (!container) return null;
        _approvedUsers = Shared.createUserMultiSelector({
            adminFilter: 'exclude',
            emptyMessage: 'No users other than administrators exist yet.'
        });
        container.innerHTML = '';
        container.appendChild(_approvedUsers.element);
        return _approvedUsers;
    }

    // ensureCoverText
    // Builds the cover text list once, as a Base Ordered List whose rows each hold a checkbox.
    //
    // Output
    // [object]: The list handle, or null when its container is missing.
    function ensureCoverText() {
        if (_coverText) return _coverText;
        var container = Shared.getEl('coverTextList');
        if (!container) return null;
        _coverText = createOrderedList({
            render: function (row) {
                var label = document.createElement('label');
                label.className = 'emby-checkbox-label';
                var box = document.createElement('input');
                box.type = 'checkbox';
                box.setAttribute('is', 'emby-checkbox');
                box.checked = row.on;
                box.addEventListener('change', function () { row.on = box.checked; });
                var text = document.createElement('span');
                text.className = 'checkboxLabel';
                text.textContent = COVER_TEXT_NAMES[row.key];
                label.appendChild(box);
                label.appendChild(text);
                return label;
            },
            label: function (row) { return COVER_TEXT_NAMES[row.key]; }
        });
        container.innerHTML = '';
        container.appendChild(_coverText.element);
        return _coverText;
    }

    // loadLibraries
    // Fills the library select with the server's music libraries.
    //
    // Output
    // [Promise]: Settles once the select is filled.
    function loadLibraries() {
        return Shared.apiRequest('Libraries', 'GET').then(function (libraries) {
            var sel = Shared.getEl('selMusicLibrary');
            if (!sel) return;
            while (sel.options.length > 1) sel.remove(1);
            (libraries || []).forEach(function (lib) {
                var opt = document.createElement('option');
                opt.value = lib.Id;
                opt.textContent = lib.Name;
                opt.dataset.path = lib.Path || '';
                sel.appendChild(opt);
            });
        });
    }

    // loadConfig
    // Reads the config and the libraries, and fills every section.
    function loadConfig() {
        // A failed library list must not blank the whole page.
        Promise.all([
            Shared.getConfig(),
            loadLibraries().catch(function () { return null; })
        ]).then(function (results) {
            var config = results[0];
            Shared.getEl('selAudioFormat').value = config.AudioFormat || 'Opus';
            Shared.getEl('selMusicLibrary').value = config.MusicLibraryId || '';
            Shared.getEl('txtLibraryPath').value = config.MusicLibraryPath || '';
            Shared.getEl('chkReplaceDuplicates').checked = config.ReplaceDuplicates !== false;
            Shared.getEl('chkLookUpCatalog').checked = config.LookUpCatalogMetadata !== false;
            Shared.getEl('chkEmbedCoverArt').checked = config.EmbedCoverArt !== false;
            var coverText = ensureCoverText();
            if (coverText) coverText.setItems(parseCoverText(config.GeneratedCoverTextOrder, config.GeneratedCoverText));
            Shared.getEl('selCoverColors').value = config.GeneratedCoverColors || 'Album';
            Shared.getEl('txtCacheOverride').value = config.CacheDirectoryOverride || '';
            Shared.getEl('txtYtDlpPath').value = config.YtDlpPath || '';
            var picker = ensureApprovedUsers();
            if (picker) picker.setValue(config.ApprovedUserIds || []);
            Shared.getEl('txtMaxPublicPlaylistItems').value = typeof config.MaxPublicPlaylistItems === 'number' ? config.MaxPublicPlaylistItems : 200;
        });
    }

    // onLibrarySelectionChanged
    // Fills the library path with the picked library's first folder.
    function onLibrarySelectionChanged() {
        var libSel = Shared.getEl('selMusicLibrary');
        var pathInput = Shared.getEl('txtLibraryPath');
        if (!libSel || !pathInput) return;

        var selectedOption = libSel.options[libSel.selectedIndex];
        pathInput.value = selectedOption && selectedOption.value && selectedOption.dataset.path ? selectedOption.dataset.path : '';
    }

    // saveSection
    // Saves one section's settings and reports the result beside its Save button.
    //
    // Inputs
    // mutator [function]: Writes the section's fields into the config.
    // statusId [string]: The status element beside the button.
    // thing [string]: What was saved, such as "Library".
    function saveSection(mutator, statusId, thing) {
        Shared.saveConfigWith(mutator, statusId, {
            success: thing + ' saved.',
            error: 'Could not save the ' + thing.toLowerCase() + '.'
        }).catch(function () { return null; });
    }

    // updateYtDlp
    // Updates yt-dlp and Deno now and reports what the server did.
    function updateYtDlp() {
        var btn = Shared.getEl('btnUpdateYtDlp');
        if (btn) btn.disabled = true;
        Shared.setStatus('ytdlpStatus', 'Updating yt-dlp…', false, { timeout: 0 });
        Shared.apiRequest('UpdateYtDlp', 'POST')
            .then(function (result) {
                Shared.setStatus('ytdlpStatus', (result && result.Message) || 'yt-dlp updated.', false);
            })
            .catch(function (err) {
                return Shared.errorText(err, 'Could not update yt-dlp.').then(function (text) { Shared.setStatus('ytdlpStatus', text, true); });
            })
            .then(function () { if (btn) btn.disabled = false; });
    }

    // resetQueue
    // Stops downloads and clears the queue with its cached files, after a confirm.
    function resetQueue() {
        confirmDialog('Reset the queue? Running downloads stop and the cached files of every item not yet imported are deleted.', {
            title: 'Reset Queue',
            confirmText: 'Reset',
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('ResetQueue', 'POST')
                .then(function () { Shared.setStatus('resetStatus', 'Queue reset.', false); })
                .catch(function (err) { Shared.errorText(err, 'Could not reset the queue.').then(function (text) { Shared.setStatus('resetStatus', text, true); }); });
        });
    }

    // resetCache
    // Stops downloads, deletes every cached file, and clears the queue, after a confirm.
    function resetCache() {
        confirmDialog('Delete every cached file and reset the queue? This cannot be undone.', {
            title: 'Reset Cache',
            confirmText: 'Reset All',
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('ResetCache', 'POST')
                .then(function () { Shared.setStatus('resetStatus', 'Cache reset.', false); })
                .catch(function (err) { Shared.errorText(err, 'Could not reset the cache.').then(function (text) { Shared.setStatus('resetStatus', text, true); }); });
        });
    }

    // bindEvents
    // Wires the page's controls once, on the first viewshow.
    function bindEvents() {
        Shared.initCollapsibles();

        Shared.bindEvent('selMusicLibrary', 'change', onLibrarySelectionChanged);

        Shared.bindClick('btnSaveLibrary', function () {
            saveSection(function (config) {
                config.MusicLibraryId = Shared.getEl('selMusicLibrary').value || '';
                config.MusicLibraryPath = Shared.getEl('txtLibraryPath').value || '';
            }, 'saveLibraryStatus', 'Library');
        });

        Shared.bindClick('btnSaveFormat', function () {
            saveSection(function (config) {
                config.AudioFormat = Shared.getEl('selAudioFormat').value || 'Opus';
            }, 'saveFormatStatus', 'Audio format');
        });

        Shared.bindClick('btnSaveImportBehavior', function () {
            saveSection(function (config) {
                config.ReplaceDuplicates = Shared.getEl('chkReplaceDuplicates').checked;
                config.LookUpCatalogMetadata = Shared.getEl('chkLookUpCatalog').checked;
                config.EmbedCoverArt = Shared.getEl('chkEmbedCoverArt').checked;
            }, 'saveImportBehaviorStatus', 'Import behavior');
        });

        Shared.bindClick('btnSaveCache', function () {
            saveSection(function (config) {
                config.CacheDirectoryOverride = Shared.getEl('txtCacheOverride').value || '';
            }, 'saveCacheStatus', 'Cache directory');
        });

        Shared.bindClick('btnSaveAlbumArt', function () {
            saveSection(function (config) {
                var text = formatCoverText(_coverText ? _coverText.getItems() : parseCoverText(config.GeneratedCoverTextOrder, config.GeneratedCoverText));
                config.GeneratedCoverTextOrder = text.order;
                config.GeneratedCoverText = text.shown;
                config.GeneratedCoverColors = Shared.getEl('selCoverColors').value || 'Album';
            }, 'saveAlbumArtStatus', 'Album art');
        });

        Shared.bindClick('btnSaveApprovedUsers', function () {
            saveSection(function (config) {
                config.ApprovedUserIds = _approvedUsers ? _approvedUsers.getValue() : [];
                var limit = parseInt(Shared.getEl('txtMaxPublicPlaylistItems').value, 10);
                if (!isNaN(limit) && limit >= 0) config.MaxPublicPlaylistItems = limit;
            }, 'saveApprovedUsersStatus', 'Approved users');
        });

        Shared.bindClick('btnSaveYtDlp', function () {
            saveSection(function (config) {
                config.YtDlpPath = Shared.getEl('txtYtDlpPath').value || '';
            }, 'saveYtDlpStatus', 'yt-dlp path');
        });

        Shared.bindClick('btnUpdateYtDlp', updateYtDlp);

        var origin = ((window.ApiClient && ApiClient.serverAddress) ? ApiClient.serverAddress() : window.location.origin).replace(/\/+$/, '');
        var dlLink = Shared.getEl('publicDownloadUrl');
        if (dlLink) {
            dlLink.href = origin + '/YouTube/Download';
            dlLink.textContent = dlLink.href;
        }
        var queueLink = Shared.getEl('publicQueueUrl');
        if (queueLink) {
            queueLink.href = origin + '/YouTube/Queue';
            queueLink.textContent = queueLink.href;
        }

        Shared.bindClick('btnResetQueue', resetQueue);
        Shared.bindClick('btnResetCache', resetCache);
    }

    var page = createPage(view, {
        tabIndex: 2,
        bind: bindEvents,
        load: loadConfig
    });
    Shared = page.shared;
}
