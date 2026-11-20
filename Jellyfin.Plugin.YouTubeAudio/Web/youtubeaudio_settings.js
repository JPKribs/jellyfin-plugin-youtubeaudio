// youtubeaudio_settings.js
// Drives the Settings tab, where each section saves its own part of the plugin config.

import { createPage, createCheckbox, createOrderedList, createSaveBar, confirmDialog, fillLinks } from './configurationpage?name=youtubeaudio_shared.js';

// COVER_TEXT_KEYS
// The translation key that names each text line a generated cover can show, by stored key.
var COVER_TEXT_KEYS = { Artist: 'LabelArtistName', Album: 'LabelAlbumName', Year: 'LabelYear' };

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
    var keys = String(order || 'Album,Artist,Year').split(',').filter(function (k) { return COVER_TEXT_KEYS[k]; });
    Object.keys(COVER_TEXT_KEYS).forEach(function (k) { if (keys.indexOf(k) === -1) keys.push(k); });
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
    var t = null;
    var _approvedUsers = null;
    var _coverText = null;
    var _saveBars = {};

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
            emptyMessage: t.tr('MessageNoOtherUsers')
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
            onMove: function () { markDirty('albumArtContent'); },
            render: function (row) {
                return createCheckbox({
                    label: t.tr(COVER_TEXT_KEYS[row.key]),
                    checked: row.on,
                    onChange: function (checked) { row.on = checked; }
                }).element;
            },
            label: function (row) { return t.tr(COVER_TEXT_KEYS[row.key]); }
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
            Object.keys(_saveBars).forEach(function (id) { _saveBars[id].setDirty(false); });
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

    // markDirty
    // Shows the unsaved line on one section's Save Bar.
    //
    // Inputs
    // contentId [string]: The section's content element.
    function markDirty(contentId) {
        if (_saveBars[contentId]) _saveBars[contentId].setDirty(true);
    }

    // addSaveBar
    // Puts a full width Save Bar at the end of one section, which saves that section's settings.
    //
    // Inputs
    // contentId [string]: The section's content element, which the bar ends.
    // savedText [string]: The line after a save, such as "Library saved."
    // failedText [string]: The line after a failed save, such as "Could not save the library."
    // mutator [function]: Writes the section's fields into the config.
    function addSaveBar(contentId, savedText, failedText, mutator) {
        var content = Shared.getEl(contentId);
        if (!content) return;
        var bar = createSaveBar({
            fill: true,
            savedText: savedText,
            failedText: failedText,
            onSave: function () { return Shared.saveConfigWith(mutator); }
        });
        _saveBars[contentId] = bar;
        content.appendChild(bar.element);
        // A field changed in the section marks it unsaved.
        content.addEventListener('input', function () { markDirty(contentId); });
        content.addEventListener('change', function () { markDirty(contentId); });
    }

    // showYtDlpIntro
    // Translates the yt-dlp section's intro, keeping its yt-dlp and Deno links in place.
    function showYtDlpIntro() {
        var intro = Shared.getEl('ytdlpIntro');
        if (!intro || !t.has('YtDlpIntro')) return;
        var links = Array.prototype.slice.call(intro.querySelectorAll('a'));
        fillLinks(intro, t.tr('YtDlpIntro'), links);
    }

    // updateYtDlp
    // Updates yt-dlp and Deno now and reports what the server did on the yt-dlp section's Save Bar.
    function updateYtDlp() {
        var btn = Shared.getEl('btnUpdateYtDlp');
        var bar = _saveBars.ytdlpContent;
        if (btn) btn.disabled = true;
        if (bar) bar.setState('saving', t.tr('MessageUpdatingYtDlp'));
        Shared.apiRequest('UpdateYtDlp', 'POST')
            .then(function (result) {
                if (bar) bar.setState('saved', (result && result.Message) || t.tr('MessageYtDlpUpdated'));
            })
            .catch(function (err) {
                return Shared.errorText(err, t.tr('ErrorUpdateYtDlp')).then(function (text) { if (bar) bar.setState('failed', text); });
            })
            .then(function () { if (btn) btn.disabled = false; });
    }

    // resetQueue
    // Stops downloads and clears the queue with its cached files, after a confirm.
    function resetQueue() {
        confirmDialog(t.tr('ConfirmResetQueue'), {
            title: t.tr('HeaderResetQueue'),
            confirmText: t.tr('Reset'),
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('ResetQueue', 'POST')
                .then(function () { Shared.setStatus('resetStatus', t.tr('MessageQueueReset'), false); })
                .catch(function (err) { Shared.errorText(err, t.tr('ErrorResetQueue')).then(function (text) { Shared.setStatus('resetStatus', text, true); }); });
        });
    }

    // resetCache
    // Stops downloads, deletes every cached file, and clears the queue, after a confirm.
    function resetCache() {
        confirmDialog(t.tr('ConfirmResetCache'), {
            title: t.tr('HeaderResetCache'),
            confirmText: t.tr('ButtonResetAll'),
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('ResetCache', 'POST')
                .then(function () { Shared.setStatus('resetStatus', t.tr('MessageCacheReset'), false); })
                .catch(function (err) { Shared.errorText(err, t.tr('ErrorResetCache')).then(function (text) { Shared.setStatus('resetStatus', text, true); }); });
        });
    }

    // bindEvents
    // Wires the page's controls once, on the first viewshow.
    function bindEvents() {
        t = Shared.t;
        Shared.initCollapsibles();
        showYtDlpIntro();

        Shared.bindEvent('selMusicLibrary', 'change', onLibrarySelectionChanged);

        addSaveBar('libraryContent', t.tr('MessageLibrarySaved'), t.tr('ErrorSaveLibrary'), function (config) {
            config.MusicLibraryId = Shared.getEl('selMusicLibrary').value || '';
            config.MusicLibraryPath = Shared.getEl('txtLibraryPath').value || '';
        });

        addSaveBar('formatContent', t.tr('MessageAudioFormatSaved'), t.tr('ErrorSaveAudioFormat'), function (config) {
            config.AudioFormat = Shared.getEl('selAudioFormat').value || 'Opus';
        });

        addSaveBar('importBehaviorContent', t.tr('MessageImportBehaviorSaved'), t.tr('ErrorSaveImportBehavior'), function (config) {
            config.ReplaceDuplicates = Shared.getEl('chkReplaceDuplicates').checked;
            config.LookUpCatalogMetadata = Shared.getEl('chkLookUpCatalog').checked;
            config.EmbedCoverArt = Shared.getEl('chkEmbedCoverArt').checked;
        });

        addSaveBar('cacheContent', t.tr('MessageCacheDirectorySaved'), t.tr('ErrorSaveCacheDirectory'), function (config) {
            config.CacheDirectoryOverride = Shared.getEl('txtCacheOverride').value || '';
        });

        addSaveBar('albumArtContent', t.tr('MessageAlbumArtSaved'), t.tr('ErrorSaveAlbumArt'), function (config) {
            var text = formatCoverText(_coverText ? _coverText.getItems() : parseCoverText(config.GeneratedCoverTextOrder, config.GeneratedCoverText));
            config.GeneratedCoverTextOrder = text.order;
            config.GeneratedCoverText = text.shown;
            config.GeneratedCoverColors = Shared.getEl('selCoverColors').value || 'Album';
        });

        addSaveBar('approvedUsersContent', t.tr('MessageApprovedUsersSaved'), t.tr('ErrorSaveApprovedUsers'), function (config) {
            config.ApprovedUserIds = _approvedUsers ? _approvedUsers.getValue() : [];
            var limit = parseInt(Shared.getEl('txtMaxPublicPlaylistItems').value, 10);
            if (!isNaN(limit) && limit >= 0) config.MaxPublicPlaylistItems = limit;
        });

        addSaveBar('ytdlpContent', t.tr('MessageYtDlpPathSaved'), t.tr('ErrorSaveYtDlpPath'), function (config) {
            config.YtDlpPath = Shared.getEl('txtYtDlpPath').value || '';
        });

        Shared.bindClick('btnUpdateYtDlp', updateYtDlp);

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
