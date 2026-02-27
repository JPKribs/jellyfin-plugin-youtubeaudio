export default function (view) {
    'use strict';

    var getTabs;
    var Shared = null;
    var _sharedPromise = import('/web/configurationpage?name=youtubeaudio_shared.js').then(function(mod) {
        getTabs = mod.getTabs;
        Shared = mod.createShared(view);
    });

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
            Promise.all([Shared.getConfig(), loadLibraries()]).then(function(results) {
                var config = results[0];
                Shared.getEl('selAudioFormat').value = config.AudioFormat || 'Opus';
                Shared.getEl('selMusicLibrary').value = config.MusicLibraryId || '';
                Shared.getEl('txtLibraryPath').value = config.MusicLibraryPath || '';
                Shared.getEl('chkReplaceDuplicates').checked = config.ReplaceDuplicates !== false;
                Shared.getEl('txtCacheOverride').value = config.CacheDirectoryOverride || '';
                Shared.getEl('txtYtDlpPath').value = config.YtDlpPath || '';
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

    function saveImportBehavior() {
        _sharedPromise.then(function() {
            Shared.getConfig().then(function(config) {
                config.ReplaceDuplicates = Shared.getEl('chkReplaceDuplicates').checked;
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

        // Reset buttons
        var btnResetQueue = Shared.getEl('btnResetQueue');
        if (btnResetQueue) btnResetQueue.addEventListener('click', resetQueue);

        var btnResetCache = Shared.getEl('btnResetCache');
        if (btnResetCache) btnResetCache.addEventListener('click', resetCache);
    });
}
