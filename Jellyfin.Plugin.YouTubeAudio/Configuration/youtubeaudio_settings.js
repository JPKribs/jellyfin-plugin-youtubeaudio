export default function (view) {
    'use strict';

    var getTabs;
    var Shared = null;
    var _approvedUsers = null;
    var _sharedPromise = import('/web/configurationpage?name=youtubeaudio_shared.js').then(function(mod) {
        getTabs = mod.getTabs;
        Shared = mod.createShared(view);
    });

    // Approved-users picker: a base user multi-selector restricted to non-admins. Created once, reused.
    function ensureApprovedUsers() {
        if (_approvedUsers) return _approvedUsers;
        var container = Shared.getEl('approvedUsersList');
        if (!container) return null;
        _approvedUsers = Shared.createUserMultiSelector({
            adminFilter: 'exclude',
            emptyMessage: 'No non-admin users exist yet.'
        });
        container.innerHTML = '';
        container.appendChild(_approvedUsers.element);
        return _approvedUsers;
    }

    // ============================================
    // DATA LOADING
    // ============================================

    function loadLibraries() {
        return Shared.apiRequest('Libraries', 'GET').then(function(libraries) {
            var sel = Shared.getEl('selMusicLibrary');
            if (!sel) return;
            // Keep first option, remove rest
            while (sel.options.length > 1) sel.remove(1);
            (libraries || []).forEach(function(lib) {
                var opt = document.createElement('option');
                opt.value = lib.Id;
                opt.textContent = lib.Name;
                opt.dataset.path = lib.Path || '';
                sel.appendChild(opt);
            });
        });
    }

    function loadConfig() {
        _sharedPromise.then(function() {
            // Each side request is isolated: a failure in one must not blank the whole page.
            Promise.all([
                Shared.getConfig(),
                loadLibraries().catch(function () { /* leave the placeholder option */ })
            ]).then(function(results) {
                var config = results[0];
                Shared.getEl('selAudioFormat').value = config.AudioFormat || 'Opus';
                Shared.getEl('selMusicLibrary').value = config.MusicLibraryId || '';
                Shared.getEl('txtLibraryPath').value = config.MusicLibraryPath || '';
                Shared.getEl('chkReplaceDuplicates').checked = config.ReplaceDuplicates !== false;
                Shared.getEl('chkLookUpCatalog').checked = config.LookUpCatalogMetadata !== false;
                Shared.getEl('chkEmbedCoverArt').checked = config.EmbedCoverArt !== false;
                renderCoverText(config.GeneratedCoverTextOrder, config.GeneratedCoverText);
                Shared.getEl('selCoverColors').value = config.GeneratedCoverColors || 'Album';
                Shared.getEl('txtCacheOverride').value = config.CacheDirectoryOverride || '';
                Shared.getEl('txtYtDlpPath').value = config.YtDlpPath || '';
                var picker = ensureApprovedUsers();
                if (picker) picker.setValue(config.ApprovedUserIds || []);
            });
        });
    }

    function onLibrarySelectionChanged() {
        var libSel = Shared.getEl('selMusicLibrary');
        var pathInput = Shared.getEl('txtLibraryPath');
        if (!libSel || !pathInput) return;

        var selectedOption = libSel.options[libSel.selectedIndex];
        if (selectedOption && selectedOption.value && selectedOption.dataset.path) {
            pathInput.value = selectedOption.dataset.path;
        } else {
            pathInput.value = '';
        }
    }

    // ============================================
    // SAVE ACTIONS (per-section)
    // ============================================

    function saveLibrary() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.MusicLibraryId = Shared.getEl('selMusicLibrary').value || '';
                config.MusicLibraryPath = Shared.getEl('txtLibraryPath').value || '';
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Library settings saved.');
                });
            });
        });
    }

    function saveFormat() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.AudioFormat = Shared.getEl('selAudioFormat').value || 'Opus';
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Audio format saved.');
                });
            });
        });
    }

    // The text lines a generated cover can show, by stored key.
    var COVER_TEXT_NAMES = { Artist: 'Artist Name', Album: 'Album Name', Year: 'Year' };

    // renderCoverText
    // Lists the cover text lines in their saved order, each with a checkbox and up/down arrows.
    //
    // Param: order | comma-separated keys in display order
    // Param: shown | comma-separated keys that are checked
    function renderCoverText(order, shown) {
        var list = Shared.getEl('coverTextList');
        if (!list) return;
        var keys = String(order || 'Album,Artist,Year').split(',').filter(function(k) { return COVER_TEXT_NAMES[k]; });
        Object.keys(COVER_TEXT_NAMES).forEach(function(k) { if (keys.indexOf(k) === -1) keys.push(k); });
        var on = String(shown === undefined || shown === null ? 'Album' : shown).split(',');
        list.innerHTML = keys.map(function(k, i) {
            return '<li data-key="' + k + '">'
                + '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox"' + (on.indexOf(k) !== -1 ? ' checked' : '') + ' />'
                + '<span class="checkboxLabel">' + COVER_TEXT_NAMES[k] + '</span></label>'
                + '<button type="button" class="yta-move-up" aria-label="Move ' + COVER_TEXT_NAMES[k] + ' up"' + (i === 0 ? ' disabled' : '') + '><span class="material-icons">arrow_upward</span></button>'
                + '<button type="button" class="yta-move-down" aria-label="Move ' + COVER_TEXT_NAMES[k] + ' down"' + (i === keys.length - 1 ? ' disabled' : '') + '><span class="material-icons">arrow_downward</span></button>'
                + '</li>';
        }).join('');
    }

    // readCoverText
    // Returns the cover text order and the checked lines as comma-separated keys.
    function readCoverText() {
        var items = Array.prototype.slice.call(Shared.getEl('coverTextList').querySelectorAll('li'));
        return {
            order: items.map(function(li) { return li.getAttribute('data-key'); }).join(','),
            shown: items.filter(function(li) { return li.querySelector('input').checked; }).map(function(li) { return li.getAttribute('data-key'); }).join(',')
        };
    }

    function saveAlbumArt() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                var text = readCoverText();
                config.GeneratedCoverTextOrder = text.order;
                config.GeneratedCoverText = text.shown;
                config.GeneratedCoverColors = Shared.getEl('selCoverColors').value || 'Album';
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Album art saved.');
                });
            });
        });
    }

    function saveImportBehavior() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.ReplaceDuplicates = Shared.getEl('chkReplaceDuplicates').checked;
                config.LookUpCatalogMetadata = Shared.getEl('chkLookUpCatalog').checked;
                config.EmbedCoverArt = Shared.getEl('chkEmbedCoverArt').checked;
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Import behavior saved.');
                });
            });
        });
    }

    function saveCache() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.CacheDirectoryOverride = Shared.getEl('txtCacheOverride').value || '';
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Cache settings saved.');
                });
            });
        });
    }

    function saveYtDlp() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.YtDlpPath = Shared.getEl('txtYtDlpPath').value || '';
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('yt-dlp settings saved.');
                });
            });
        });
    }

    function saveApprovedUsers() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.ApprovedUserIds = _approvedUsers ? _approvedUsers.getValue() : [];
                Shared.saveConfig(config).then(function() {
                    Dashboard.alert('Approved users saved.');
                });
            });
        });
    }

    function updateYtDlp() {
        var btn = Shared.getEl('btnUpdateYtDlp');
        if (btn) btn.disabled = true;
        Shared.setStatus('ytdlpStatus', 'Updating yt-dlp... this can take a minute.', false);
        Shared.apiRequest('UpdateYtDlp', 'POST')
            .then(function(result) {
                if (btn) btn.disabled = false;
                Shared.setStatus('ytdlpStatus', (result && result.Message) || 'yt-dlp updated.', false);
            })
            .catch(function(err) {
                if (btn) btn.disabled = false;
                Shared.setStatus('ytdlpStatus', (err && err.Error) || 'Failed to update yt-dlp.', true);
            });
    }

    // ============================================
    // RESET ACTIONS
    // ============================================

    function resetQueue() {
        if (!confirm('Clear the entire download queue? This cannot be undone.')) return;
        Shared.apiRequest('ResetQueue', 'POST')
            .then(function() { Shared.setStatus('resetStatus', 'Queue reset successfully.', false); })
            .catch(function() { Shared.setStatus('resetStatus', 'Failed to reset queue.', true); });
    }

    function resetCache() {
        if (!confirm('Delete ALL cached audio files and reset the queue? This cannot be undone.')) return;
        Shared.apiRequest('ResetCache', 'POST')
            .then(function() { Shared.setStatus('resetStatus', 'Cache reset successfully.', false); })
            .catch(function() { Shared.setStatus('resetStatus', 'Failed to reset cache.', true); });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function() {
        _sharedPromise.then(function() {
            LibraryMenu.setTabs('youtubeaudio', 2, getTabs);
            loadConfig();
        });
    });

    _sharedPromise.then(function() {
        // Initialize collapsible sections
        Shared.initCollapsibles();

        // Library dropdown change -> update path field
        var selLibrary = Shared.getEl('selMusicLibrary');
        if (selLibrary) selLibrary.addEventListener('change', onLibrarySelectionChanged);

        // Per-section save buttons
        var btnSaveLibrary = Shared.getEl('btnSaveLibrary');
        if (btnSaveLibrary) btnSaveLibrary.addEventListener('click', saveLibrary);

        var btnSaveFormat = Shared.getEl('btnSaveFormat');
        if (btnSaveFormat) btnSaveFormat.addEventListener('click', saveFormat);

        var btnSaveImportBehavior = Shared.getEl('btnSaveImportBehavior');
        if (btnSaveImportBehavior) btnSaveImportBehavior.addEventListener('click', saveImportBehavior);

        var btnSaveCache = Shared.getEl('btnSaveCache');
        if (btnSaveCache) btnSaveCache.addEventListener('click', saveCache);

        var btnSaveYtDlp = Shared.getEl('btnSaveYtDlp');
        if (btnSaveYtDlp) btnSaveYtDlp.addEventListener('click', saveYtDlp);

        var btnUpdateYtDlp = Shared.getEl('btnUpdateYtDlp');
        if (btnUpdateYtDlp) btnUpdateYtDlp.addEventListener('click', updateYtDlp);

        // Links to the approved-user download page and the read-only queue.
        var origin = ((window.ApiClient && ApiClient.serverAddress) ? ApiClient.serverAddress() : window.location.origin).replace(/\/+$/, '');
        var dlLink = Shared.getEl('publicDownloadUrl');
        if (dlLink) dlLink.href = origin + '/YouTube/Download';
        var queueLink = Shared.getEl('publicQueueUrl');
        if (queueLink) queueLink.href = origin + '/YouTube/Queue';

        var coverTextList = Shared.getEl('coverTextList');
        if (coverTextList) coverTextList.addEventListener('click', function(e) {
            var btn = e.target.closest('.yta-move-up, .yta-move-down');
            if (!btn || btn.disabled) return;
            var text = readCoverText();
            var keys = text.order.split(',');
            var key = btn.closest('li').getAttribute('data-key');
            var from = keys.indexOf(key);
            var to = from + (btn.classList.contains('yta-move-up') ? -1 : 1);
            keys.splice(to, 0, keys.splice(from, 1)[0]);
            renderCoverText(keys.join(','), text.shown);
            var moved = coverTextList.querySelector('li[data-key="' + key + '"] .' + (to < from ? 'yta-move-up' : 'yta-move-down'));
            if (moved && !moved.disabled) moved.focus();
        });

        var btnSaveAlbumArt = Shared.getEl('btnSaveAlbumArt');
        if (btnSaveAlbumArt) btnSaveAlbumArt.addEventListener('click', saveAlbumArt);

        var btnSaveApprovedUsers = Shared.getEl('btnSaveApprovedUsers');
        if (btnSaveApprovedUsers) btnSaveApprovedUsers.addEventListener('click', saveApprovedUsers);

        // Reset buttons
        var btnResetQueue = Shared.getEl('btnResetQueue');
        if (btnResetQueue) btnResetQueue.addEventListener('click', resetQueue);

        var btnResetCache = Shared.getEl('btnResetCache');
        if (btnResetCache) btnResetCache.addEventListener('click', resetCache);
    });
}
