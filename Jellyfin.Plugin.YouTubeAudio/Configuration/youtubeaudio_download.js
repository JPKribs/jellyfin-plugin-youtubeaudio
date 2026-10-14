export default function (view) {
    'use strict';

    var getTabs;
    var Shared = null;
    var _sharedPromise = import('/web/configurationpage?name=youtubeaudio_shared.js').then(function(mod) {
        getTabs = mod.getTabs;
        Shared = mod.createShared(view);
    });

    var _allItems = [];
    var _sourceNames = { replace: 'Replacement' };
    var _pendingUpload = null;
    var _ownerSelector = null;
    var _pollInterval = null;
    var _processingGraceUntil = 0;
    var _replaceSongId = null;
    var _replaceSearch = 0;

    // ============================================
    // DATA LOADING
    // ============================================

    function loadQueue() {
        var refreshBtn = view.querySelector('#btnRefreshQueue');
        if (refreshBtn) refreshBtn.classList.add('spinning');

        _sharedPromise.then(function() {
            Shared.apiRequest('Queue', 'GET')
                .then(function(response) {
                    _allItems = (response && response.Items) || response || [];
                    renderQueue();
                    if (refreshBtn) refreshBtn.classList.remove('spinning');
                })
                .catch(function() {
                    _allItems = [];
                    renderQueue();
                    if (refreshBtn) refreshBtn.classList.remove('spinning');
                });
        });
    }

    // ============================================
    // FILTERING
    // ============================================

    function getFilteredItems() {
        var searchVal = (view.querySelector('#queueSearch') || {}).value || '';
        var filterVal = (view.querySelector('#queueFilter') || {}).value || '';
        searchVal = searchVal.toLowerCase();

        return _allItems.filter(function(item) {
            // Filter out Imported items (they move to Import tab)
            if (item.StatusCode === 3) return false;

            // Status filter
            if (filterVal && String(item.StatusCode) !== filterVal) return false;

            // Search filter
            if (searchVal) {
                var haystack = [item.Title, item.Artist, item.Url].join(' ').toLowerCase();
                if (haystack.indexOf(searchVal) === -1) return false;
            }

            return true;
        });
    }

    // ============================================
    // RENDERING (ServerSync jpk-table-row pattern)
    // ============================================

    // renderMetrics
    // Renders the queue status counts as stat cards from the full item set.
    //
    // No parameters
    function renderMetrics() {
        var el = view.querySelector('#queueMetrics');
        if (!el) return;

        var counts = { queued: 0, downloading: 0, downloaded: 0, error: 0 };
        for (var i = 0; i < _allItems.length; i++) {
            switch (_allItems[i].StatusCode) {
                case 0: counts.queued++; break;
                case 1: counts.downloading++; break;
                case 2: counts.downloaded++; break;
                case 4: counts.error++; break;
            }
        }

        el.innerHTML =
            '<div class="jpk-card gray"><span class="jpk-card-count">' + counts.queued + '</span><span class="jpk-card-label">Queued</span></div>' +
            '<div class="jpk-card blue"><span class="jpk-card-count">' + counts.downloading + '</span><span class="jpk-card-label">Downloading</span></div>' +
            '<div class="jpk-card green"><span class="jpk-card-count">' + counts.downloaded + '</span><span class="jpk-card-label">Downloaded</span></div>' +
            '<div class="jpk-card red"><span class="jpk-card-count">' + counts.error + '</span><span class="jpk-card-label">Error</span></div>';
    }

    function renderQueue() {
        renderMetrics();

        var body = view.querySelector('#queueBody');
        var footer = view.querySelector('#queueFooter');
        if (!body) return;

        // Keep an open link editor (and what was typed) across poll-driven re-renders.
        var openEditor = body.querySelector('.yta-link-editor:not([hidden])');
        var editingId = openEditor ? openEditor.closest('.jpk-table-row').getAttribute('data-id') : null;
        var editingValue = openEditor ? openEditor.querySelector('.yta-link-input').value : '';

        // Remember checked rows so poll-driven re-renders don't wipe the selection.
        var checkedIds = {};
        body.querySelectorAll('.yta-queue-check:checked').forEach(function(cb) {
            checkedIds[cb.getAttribute('data-id')] = true;
        });

        var items = getFilteredItems();

        if (items.length === 0) {
            body.innerHTML = '<div class="jpk-table-empty">Queue is empty. Add a URL above to get started.</div>';
            if (footer) footer.textContent = '';
        } else {
            var html = '';
            for (var i = 0; i < items.length; i++) {
                html += renderRow(items[i]);
            }
            body.innerHTML = html;
            if (footer) footer.textContent = items.length + ' item' + (items.length !== 1 ? 's' : '');

            // Bind row checkboxes, restoring the previous selection
            body.querySelectorAll('.jpk-table-row-checkbox').forEach(function(cb) {
                cb.checked = !!checkedIds[cb.getAttribute('data-id')];
                cb.addEventListener('change', updateQueueSelectedCount);
            });

            bindLinkEditors(body);
            if (editingId) {
                var editingRow = body.querySelector('.jpk-table-row[data-id="' + editingId.replace(/"/g, '') + '"]');
                if (editingRow && editingRow.querySelector('.yta-link-btn')) openLinkEditor(editingRow, editingValue);
            }

        }

        updateQueueSelectedCount();
        var chkAll = view.querySelector('#chkSelectAllQueue');
        if (chkAll) {
            var boxes = body.querySelectorAll('.yta-queue-check');
            var checked = body.querySelectorAll('.yta-queue-check:checked');
            chkAll.checked = boxes.length > 0 && checked.length === boxes.length;
        }

        // Auto-poll while downloads are active, plus a grace window right after starting a
        // batch — the background run may still be provisioning yt-dlp before anything
        // shows as Downloading.
        var shouldPoll = _allItems.some(function(i) { return i.StatusCode === 1; })
            || Date.now() < _processingGraceUntil;
        if (shouldPoll && !_pollInterval) {
            _pollInterval = setInterval(loadQueue, 3000);
        } else if (!shouldPoll && _pollInterval) {
            clearInterval(_pollInterval);
            _pollInterval = null;
        }
    }

    function renderRow(item) {
        var esc = Shared.escapeHtml;
        var displayTitle = item.Title || item.Url;
        var subText = item.Url;

        // Playlist-imported tracks show their artist, and have no URL until their YouTube match
        // is found at download time.
        if (item.Source) {
            if (item.Artist) displayTitle = item.Artist + ' \u2013 ' + displayTitle;
            var sourceName = _sourceNames[item.Source] || item.Source;
            subText = item.Url
                ? sourceName + ' \u00b7 ' + item.Url
                : sourceName + ' \u00b7 YouTube match is found when downloaded';
        }

        var matchNote = '';
        if (item.MatchNote && item.StatusCode !== 4) {
            matchNote = '<div class="yta-match-note" title="' + esc(item.MatchNote) + '">' + esc(item.MatchNote) + '</div>';
        }
        if (item.ReplacesFileCount > 0) {
            matchNote += '<div class="jpk-table-item-sub">Replaces the single already in your library when imported.</div>';
        }

        var errorPreview = '';
        if (item.StatusCode === 4 && item.ErrorMessage) {
            errorPreview = '<div class="jpk-table-item-error" title="' +
                esc(item.ErrorMessage) + '">' +
                esc(item.ErrorMessage) + '</div>';
        }

        return '<div class="jpk-table-row" data-id="' + esc(item.Id) + '">'
            + '<div class="jpk-table-cell jpk-table-cell-checkbox">'
            + '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="jpk-table-row-checkbox yta-queue-check" data-id="' + esc(item.Id) + '" /><span class="checkboxLabel"></span></label>'
            + '</div>'
            + '<div class="jpk-table-cell">'
            + '<div class="jpk-table-item-info">'
            + '<div class="jpk-table-item-title" title="' + esc(subText) + '">' + esc(displayTitle) + '</div>'
            + '<div class="jpk-table-item-sub">' + esc(subText) + '</div>'
            + matchNote
            + errorPreview
            + '<div class="yta-link-editor" hidden>'
            + '<input type="url" class="yta-link-input" placeholder="https://www.youtube.com/watch?v=…" aria-label="YouTube link" />'
            + '<button is="emby-button" type="button" class="raised jpk-button-submit jpk-button-small yta-link-save"><span>Use Link</span></button>'
            + '<button is="emby-button" type="button" class="raised jpk-button-small yta-link-cancel"><span>Cancel</span></button>'
            + '<div class="yta-link-error"></div>'
            + '</div>'
            + '</div>'
            + '</div>'
            + '<div class="jpk-table-cell jpk-table-cell-status yta-status-cell">'
            + (item.StatusCode === 1 ? '' : '<button type="button" class="jpk-row-btn yta-link-btn" title="Change YouTube link"><span class="material-icons">link</span></button>')
            + Shared.getStatusBadge(item.StatusCode, item.Status)
            + '</div>'
            + '</div>';
    }

    // ============================================
    // CHANGE YOUTUBE LINK
    // ============================================

    // bindLinkEditors
    // Wires each row's link button and inline editor, used to point a track at a different
    // YouTube upload when its match is wrong or failed.
    //
    // Param: body | the queue body element
    function bindLinkEditors(body) {
        body.querySelectorAll('.jpk-table-row').forEach(function(row) {
            var id = row.getAttribute('data-id');
            var editor = row.querySelector('.yta-link-editor');
            var input = row.querySelector('.yta-link-input');
            var btn = row.querySelector('.yta-link-btn');
            if (!editor || !input) return;

            if (btn) btn.addEventListener('click', function() {
                var opening = editor.hidden;
                closeLinkEditors(body);
                if (opening) openLinkEditor(row, '');
            });
            row.querySelector('.yta-link-cancel').addEventListener('click', function() { closeLinkEditors(body); });
            row.querySelector('.yta-link-save').addEventListener('click', function() { saveLink(row, id); });
            input.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') { e.preventDefault(); saveLink(row, id); }
                if (e.key === 'Escape') { e.preventDefault(); closeLinkEditors(body); }
            });
        });
    }

    // openLinkEditor
    // Shows a row's link editor with the given text and focuses it.
    //
    // Param: row   | the queue row
    // Param: value | text to put in the field
    function openLinkEditor(row, value) {
        var editor = row.querySelector('.yta-link-editor');
        var input = row.querySelector('.yta-link-input');
        editor.hidden = false;
        input.value = value || '';
        row.querySelector('.yta-link-error').textContent = '';
        input.focus();
    }

    // closeLinkEditors
    // Hides every open link editor.
    //
    // Param: body | the queue body element
    function closeLinkEditors(body) {
        body.querySelectorAll('.yta-link-editor').forEach(function(ed) { ed.hidden = true; });
    }

    // saveLink
    // Sends the new link; the server keeps the track's details and starts downloading it.
    //
    // Param: row | the queue row
    // Param: id  | queue item id
    function saveLink(row, id) {
        var input = row.querySelector('.yta-link-input');
        var error = row.querySelector('.yta-link-error');
        var url = (input.value || '').trim();
        if (!url) { error.textContent = 'Paste a YouTube link.'; return; }

        Shared.apiRequest('Queue/' + encodeURIComponent(id) + '/Url', 'POST', { Url: url })
            .then(function() {
                row.querySelector('.yta-link-editor').hidden = true;
                _processingGraceUntil = Date.now() + 120000;
                Shared.setStatus('queueStatus', 'Link changed. Downloading from the new link.', false);
                loadQueue();
            })
            .catch(function(err) {
                error.textContent = (err && err.Error) || 'Could not use that link.';
            });
    }

    // ============================================
    // SELECTION
    // ============================================

    function updateQueueSelectedCount() {
        var checked = view.querySelectorAll('.yta-queue-check:checked').length;
        var countEl = view.querySelector('#queueSelectedCount');
        if (countEl) {
            countEl.textContent = checked > 0 ? checked + ' selected' : '';
        }

        var deleteBtn = view.querySelector('#btnDeleteSelectedQueue');
        if (deleteBtn) {
            deleteBtn.style.visibility = checked > 0 ? '' : 'hidden';
        }

        var downloadBtn = view.querySelector('#btnProcess');
        if (downloadBtn) {
            downloadBtn.style.visibility = checked > 0 ? '' : 'hidden';
        }
    }

    function deleteSelectedQueue() {
        var checkboxes = view.querySelectorAll('.yta-queue-check:checked');
        var selectedIds = [];
        checkboxes.forEach(function(cb) { selectedIds.push(cb.getAttribute('data-id')); });

        if (selectedIds.length === 0) {
            Shared.setStatus('queueStatus', 'Select at least one item to delete.', true);
            return;
        }

        if (!confirm('Remove ' + selectedIds.length + ' item(s) from the queue? This cannot be undone.')) return;

        Shared.setStatus('queueStatus', 'Deleting ' + selectedIds.length + ' item(s)...', false);

        Shared.apiRequest('Queue/BatchDelete', 'POST', { Ids: selectedIds })
            .then(function(result) {
                var deleted = result && result.Deleted || 0;
                var errors = result && result.Errors || 0;
                var msg = 'Removed ' + deleted + ' item(s).';
                if (errors > 0) msg += ' ' + errors + ' error(s).';
                Shared.setStatus('queueStatus', msg, errors > 0);
                loadQueue();
            })
            .catch(function() {
                Shared.setStatus('queueStatus', 'Failed to delete items.', true);
                loadQueue();
            });
    }

    // ============================================
    // ACTIONS
    // ============================================

    function queueUrl() {
        var input = Shared.getEl('txtYouTubeUrl');
        var url = (input || {}).value;
        if (!url || !url.trim()) {
            Shared.setStatus('queueStatus', 'Please enter a YouTube URL.', true);
            return;
        }

        Shared.apiRequest('Queue', 'POST', { Url: url.trim() })
            .then(function(items) {
                var count = items ? items.length : 0;
                Shared.setStatus('queueStatus', 'Added ' + count + ' item(s) to queue.', false);
                if (input) input.value = '';
                var qBtn = Shared.getEl('btnQueue');
                if (qBtn) qBtn.disabled = true;
                loadQueue();
            })
            .catch(function(err) {
                Shared.setStatus('queueStatus', (err && err.Error) || 'Failed to add.', true);
            });
    }

    // loadPlaylistSources
    // Fetches the supported playlist export formats, so queue rows can name the service each
    // song came from. Each service's instructions are on its card.
    //
    // No parameters
    function loadPlaylistSources() {
        Shared.apiRequest('Playlists/Sources', 'GET')
            .then(function(sources) {
                (sources || []).forEach(function(source) {
                    _sourceNames[source.Id] = source.Name;
                });
                renderQueue();
            })
            .catch(function() { /* queue rows fall back to the source ID */ });
    }

    // searchReplaceSongs
    // Lists library songs matching the search text, to pick the one to replace. Answers to
    // older searches that arrive late are dropped.
    //
    // No parameters
    function searchReplaceSongs() {
        var input = Shared.getEl('txtReplaceSearch');
        var list = Shared.getEl('replaceResults');
        var query = ((input || {}).value || '').trim();
        var search = ++_replaceSearch;
        if (!list) return;
        if (!query) {
            list.innerHTML = '';
            selectReplaceSong(null);
            return;
        }

        Shared.apiRequest('Songs?query=' + encodeURIComponent(query), 'GET')
            .then(function(songs) {
                if (search !== _replaceSearch) return;
                var esc = Shared.escapeHtml;
                songs = songs || [];
                list.innerHTML = songs.length
                    ? songs.map(function(song) {
                        var sub = [song.Artist, song.Album].filter(Boolean).join(' \u2014 ');
                        return '<button type="button" class="yta-replace-result' + (song.Id === _replaceSongId ? ' selected' : '') + '" data-id="' + esc(song.Id) + '" title="' + esc(song.Path || '') + '">'
                            + '<span>' + esc(song.Title) + '</span>'
                            + '<span class="yta-replace-result-sub">' + esc(sub) + '</span></button>';
                    }).join('')
                    : '<div class="yta-replace-empty">No library songs match.</div>';
                list.querySelectorAll('.yta-replace-result').forEach(function(btn) {
                    btn.addEventListener('click', function() { selectReplaceSong(btn.getAttribute('data-id')); });
                });
                if (_replaceSongId && !songs.some(function(song) { return song.Id === _replaceSongId; })) selectReplaceSong(null);
            })
            .catch(function() {
                if (search === _replaceSearch) list.innerHTML = '<div class="yta-replace-empty">Search failed.</div>';
            });
    }

    // selectReplaceSong
    // Marks the library song to replace and enables the button once a link is entered too.
    //
    // Param: id | the song's item ID, or null to clear the choice
    function selectReplaceSong(id) {
        _replaceSongId = id;
        view.querySelectorAll('.yta-replace-result').forEach(function(btn) {
            btn.classList.toggle('selected', btn.getAttribute('data-id') === id);
        });
        updateReplaceButton();
    }

    function updateReplaceButton() {
        var btn = Shared.getEl('btnReplace');
        var url = Shared.getEl('txtReplaceUrl');
        if (btn) btn.disabled = !(_replaceSongId && url && url.value.trim());
    }

    // queueReplacement
    // Queues the link to replace the chosen library song, and starts its download.
    //
    // No parameters
    function queueReplacement() {
        var url = Shared.getEl('txtReplaceUrl');
        if (!_replaceSongId || !url || !url.value.trim()) return;

        var btn = Shared.getEl('btnReplace');
        if (btn) btn.disabled = true;
        Shared.apiRequest('Queue/Replace', 'POST', { ItemId: _replaceSongId, Url: url.value.trim() })
            .then(function(item) {
                Shared.setStatus('queueStatus', 'Downloading the replacement for "' + ((item && item.Title) || 'the song') + '". Import it from the Import tab.', false);
                url.value = '';
                var search = Shared.getEl('txtReplaceSearch');
                if (search) search.value = '';
                searchReplaceSongs();
                loadQueue();
            })
            .catch(function(err) {
                Shared.setStatus('queueStatus', (err && err.Error) || 'Failed to queue the replacement.', true);
                updateReplaceButton();
            });
    }

    // readFileAsBase64
    // Reads a file's raw bytes as base64 so the server can detect its text encoding (the Music
    // app writes UTF-16), rather than letting the browser decode it as UTF-8.
    //
    // Param: file | the File from the input
    function readFileAsBase64(file) {
        return new Promise(function(resolve, reject) {
            var reader = new FileReader();
            reader.onload = function() {
                var result = String(reader.result || '');
                resolve(result.substring(result.indexOf(',') + 1));
            };
            reader.onerror = function() { reject(reader.error); };
            reader.readAsDataURL(file);
        });
    }

    // resetPlaylistImport
    // Clears the pending upload and the playlist picker, e.g. after a new file is chosen.
    //
    // No parameters
    function resetPlaylistImport() {
        _pendingUpload = null;
        var choices = Shared.getEl('playlistChoices');
        if (choices) {
            choices.hidden = true;
            choices.innerHTML = '';
        }

        var btn = Shared.getEl('btnImportPlaylist');
        if (btn) btn.querySelector('span').textContent = 'Queue Playlist';
    }

    // renderPlaylistChoices
    // Shows a checkbox per playlist when the file holds several (a library export).
    //
    // Param: playlists | the PlaylistChoice list from the server
    function renderPlaylistChoices(playlists) {
        var esc = Shared.escapeHtml;
        var choices = Shared.getEl('playlistChoices');
        if (!choices) return;

        // Everything is ticked by default: a library upload downloads every song and recreates
        // every playlist unless something is unticked.
        var html = '<div class="fieldDescription">This file holds ' + playlists.length + ' playlists. Untick any you don\'t want:</div>';
        playlists.forEach(function(p) {
            var label = esc(p.Name) + ' (' + p.TrackCount + ' track' + (p.TrackCount !== 1 ? 's' : '') + ')';
            if (p.IsLibrary) label += ' \u2014 songs only, no playlist';
            html += '<label class="emby-checkbox-label">'
                + '<input type="checkbox" is="emby-checkbox" class="yta-playlist-choice" value="' + esc(p.Id) + '" checked />'
                + '<span class="checkboxLabel">' + label + '</span>'
                + '</label>';
        });
        choices.innerHTML = html;
        choices.hidden = false;

        var btn = Shared.getEl('btnImportPlaylist');
        if (btn) {
            btn.querySelector('span').textContent = 'Queue Selected';
            btn.disabled = false;
        }

        choices.querySelectorAll('.yta-playlist-choice').forEach(function(cb) {
            cb.addEventListener('change', function() {
                if (btn) btn.disabled = !choices.querySelector('.yta-playlist-choice:checked');
            });
        });
    }

    // importPlaylist
    // Uploads the chosen playlist export and queues its tracks. A file holding several
    // playlists comes back for selection first; the same upload is then resent with the picks.
    //
    // No parameters
    function importPlaylist() {
        var input = Shared.getEl('filePlaylist');
        var file = input && input.files && input.files[0];
        if (!file) {
            Shared.setStatus('queueStatus', 'Choose a playlist export file first.', true);
            return;
        }

        var btn = Shared.getEl('btnImportPlaylist');
        if (btn) btn.disabled = true;

        var playlistIds = [];
        view.querySelectorAll('.yta-playlist-choice:checked').forEach(function(cb) { playlistIds.push(cb.value); });

        var upload = _pendingUpload
            ? Promise.resolve(_pendingUpload)
            : readFileAsBase64(file).then(function(content) {
                _pendingUpload = { FileName: file.name, Content: content };
                return _pendingUpload;
            });

        Shared.setStatus('queueStatus', 'Reading ' + file.name + '...', false);

        upload
            .then(function(body) {
                return Shared.apiRequest('Queue/Playlist', 'POST', {
                    FileName: body.FileName,
                    Content: body.Content,
                    PlaylistIds: playlistIds,
                    UserId: _ownerSelector ? _ownerSelector.getValue() : ''
                });
            })
            .then(function(result) {
                if (result && result.NeedsSelection) {
                    Shared.setStatus('queueStatus', '', false);
                    renderPlaylistChoices(result.Playlists || []);
                    return;
                }

                var count = (result && result.Items) ? result.Items.length : 0;
                var msg = 'Added ' + count + ' track' + (count !== 1 ? 's' : '') + ' from ' + ((result && result.Source) || 'the playlist');
                if (result && result.PlaylistName) msg += ' (' + result.PlaylistName + ')';
                msg += '.';
                var left = [];
                if (result && result.AlreadyInLibrary) left.push(result.AlreadyInLibrary + ' already in your library');
                if (result && result.AlreadyQueued) left.push(result.AlreadyQueued + ' already queued');
                if (result && result.DuplicateTracks) left.push(result.DuplicateTracks + ' duplicate or lesser version(s)');
                if (left.length) msg += ' Left out ' + left.join(', ') + '.';
                if (result && result.LibraryUpgrades) msg += ' ' + result.LibraryUpgrades + ' will replace a single in your library with the album version.';
                if (result && result.PlaylistsTracked) msg += ' ' + result.PlaylistsTracked + ' playlist' + (result.PlaylistsTracked !== 1 ? 's' : '') + ' will fill in as songs reach the library.';
                if (result && result.SkippedRows) msg += ' Skipped ' + result.SkippedRows + ' entr' + (result.SkippedRows !== 1 ? 'ies' : 'y') + ' with no title.';
                Shared.setStatus('queueStatus', msg, false);
                input.value = '';
                resetPlaylistImport();
                loadQueue();
                loadPlaylistTargets();
            })
            .catch(function(err) {
                Shared.setStatus('queueStatus', (err && err.Error) || 'Failed to import the playlist.', true);
                if (btn) btn.disabled = false;
            });
    }

    // loadPlaylistTargets
    // Shows the Jellyfin playlists being filled by imports and how many songs each has so far.
    //
    // No parameters
    function loadPlaylistTargets() {
        Shared.apiRequest('Playlists', 'GET')
            .then(function(targets) {
                targets = targets || [];
                var section = Shared.getEl('playlistTargetsSection');
                var body = Shared.getEl('playlistTargets');
                if (!section || !body) return;

                section.hidden = targets.length === 0;
                var esc = Shared.escapeHtml;
                body.innerHTML = targets.map(function(t) {
                    var state = (t.Total - t.Added) + ' waiting for download or import';
                    return '<div class="jpk-table-row">'
                        + '<div class="jpk-table-cell"><div class="jpk-table-item-info">'
                        + '<div class="jpk-table-item-title">' + esc(t.Name) + '</div>'
                        + '<div class="jpk-table-item-sub">' + esc(t.UserName) + ' \u00b7 ' + t.Added + ' of ' + t.Total + ' songs \u00b7 ' + esc(state) + '</div>'
                        + '</div></div>'
                        + '<div class="jpk-table-cell jpk-table-cell-status">'
                        + '<button is="emby-button" type="button" class="raised jpk-button-destructive yta-playlist-forget" data-id="' + esc(t.Id) + '" title="Stop adding songs; the Jellyfin playlist is kept"><span>Stop</span></button>'
                        + '</div></div>';
                }).join('');

                body.querySelectorAll('.yta-playlist-forget').forEach(function(btn) {
                    btn.addEventListener('click', function() {
                        if (!confirm('Stop adding songs to this playlist? The Jellyfin playlist and its songs are kept.')) return;
                        Shared.apiRequest('Playlists/' + encodeURIComponent(btn.getAttribute('data-id')), 'DELETE')
                            .then(loadPlaylistTargets);
                    });
                });
            })
            .catch(function() { /* playlist progress is informational */ });
    }

    // syncPlaylists
    // Adds pending playlist songs that are already in the library, without waiting for a scan.
    //
    // No parameters
    function syncPlaylists() {
        var btn = Shared.getEl('btnSyncPlaylists');
        if (btn) btn.classList.add('spinning');
        Shared.apiRequest('Playlists/Sync', 'POST')
            .then(function(result) {
                var added = (result && result.Added) || 0;
                Shared.setStatus('queueStatus', 'Added ' + added + ' song' + (added !== 1 ? 's' : '') + ' to playlists.', false);
            })
            .catch(function() {
                Shared.setStatus('queueStatus', 'Playlist sync failed.', true);
            })
            .then(function() {
                if (btn) btn.classList.remove('spinning');
                loadPlaylistTargets();
            });
    }

    function processQueue() {
        var checkboxes = view.querySelectorAll('.yta-queue-check:checked');
        var selectedIds = [];
        checkboxes.forEach(function(cb) { selectedIds.push(cb.getAttribute('data-id')); });

        if (selectedIds.length === 0) {
            Shared.setStatus('queueStatus', 'Select items to download.', true);
            return;
        }

        Shared.setStatus('queueStatus', 'Starting download of ' + selectedIds.length + ' item(s)...', false);
        Shared.apiRequest('Queue/Process', 'POST', { Ids: selectedIds })
            .then(function() {
                // The server downloads in the background; poll until progress appears.
                _processingGraceUntil = Date.now() + 120000;
                Shared.setStatus('queueStatus', 'Download started.', false);
                loadQueue();
            })
            .catch(function(err) {
                Shared.setStatus('queueStatus', (err && err.Error) || 'Failed to start download.', true);
                loadQueue();
            });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function() {
        _sharedPromise.then(function() {
            LibraryMenu.setTabs('youtubeaudio', 0, getTabs);
            loadQueue();
            loadPlaylistSources();
            loadPlaylistTargets();
        });
    });

    // bindSourceTabs
    // Shows the form for the chosen source card (YouTube, Apple Music) and remembers the last
    // one used in this browser.
    function bindSourceTabs() {
        var group = view.querySelector('#sourceChoices');
        if (!group) return;

        function show(source) {
            view.querySelectorAll('.yta-source-panel, .yta-source-help').forEach(function(el) {
                el.hidden = el.getAttribute('data-source').split(' ').indexOf(source) === -1;
            });
            var input = view.querySelector('#filePlaylist');
            if (input) {
                var accept = source === 'spotify' ? '.csv,.zip' : '.txt,.xml';
                if (input.getAttribute('accept') !== accept && input.value) resetPlaylistImport();
                input.setAttribute('accept', accept);
            }
            try { localStorage.setItem('yta-download-source', source); } catch (e) { /* storage unavailable */ }
        }

        var choices = Shared.createChoiceGroup(group, { onChange: show });
        var saved = null;
        try { saved = localStorage.getItem('yta-download-source'); } catch (e) { /* storage unavailable */ }
        var initial = group.querySelector('input[value="' + saved + '"]') ? saved : 'youtube';
        choices.set(initial);
        show(initial);
    }

    view.addEventListener('viewhide', function() {
        if (_pollInterval) {
            clearInterval(_pollInterval);
            _pollInterval = null;
        }
    });

    _sharedPromise.then(function() {
        bindSourceTabs();

        var btnQueue = Shared.getEl('btnQueue');
        if (btnQueue) btnQueue.addEventListener('click', queueUrl);

        var ownerContainer = Shared.getEl('playlistOwner');
        if (ownerContainer && !_ownerSelector) {
            _ownerSelector = Shared.createUserSelector({
                emptyLabel: "Don't create playlists",
                value: (window.ApiClient && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : ''
            });
            ownerContainer.appendChild(_ownerSelector.element);
        }

        var replaceSearch = Shared.getEl('txtReplaceSearch');
        if (replaceSearch) {
            var replaceTimeout = null;
            replaceSearch.addEventListener('input', function() {
                if (replaceTimeout) clearTimeout(replaceTimeout);
                replaceTimeout = setTimeout(searchReplaceSongs, 250);
            });
            replaceSearch.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') e.preventDefault();
            });
        }

        var replaceUrl = Shared.getEl('txtReplaceUrl');
        if (replaceUrl) {
            replaceUrl.addEventListener('input', updateReplaceButton);
            replaceUrl.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') { e.preventDefault(); queueReplacement(); }
            });
        }

        var btnReplace = Shared.getEl('btnReplace');
        if (btnReplace) btnReplace.addEventListener('click', queueReplacement);

        var btnSyncPlaylists = Shared.getEl('btnSyncPlaylists');
        if (btnSyncPlaylists) btnSyncPlaylists.addEventListener('click', syncPlaylists);

        var playlistInput = Shared.getEl('filePlaylist');
        var btnImportPlaylist = Shared.getEl('btnImportPlaylist');
        if (playlistInput && btnImportPlaylist) {
            playlistInput.addEventListener('change', function() {
                resetPlaylistImport();
                btnImportPlaylist.disabled = !(playlistInput.files && playlistInput.files.length);
            });
            btnImportPlaylist.addEventListener('click', importPlaylist);
        }

        var btnProcess = Shared.getEl('btnProcess');
        if (btnProcess) btnProcess.addEventListener('click', processQueue);

        var btnRefresh = Shared.getEl('btnRefreshQueue');
        if (btnRefresh) btnRefresh.addEventListener('click', loadQueue);

        var btnDeleteSelected = Shared.getEl('btnDeleteSelectedQueue');
        if (btnDeleteSelected) btnDeleteSelected.addEventListener('click', deleteSelectedQueue);

        var chkSelectAll = Shared.getEl('chkSelectAllQueue');
        if (chkSelectAll) {
            chkSelectAll.addEventListener('change', function() {
                view.querySelectorAll('.yta-queue-check').forEach(function(cb) {
                    cb.checked = chkSelectAll.checked;
                });
                updateQueueSelectedCount();
            });
        }

        var urlInput = Shared.getEl('txtYouTubeUrl');
        if (urlInput) {
            urlInput.addEventListener('keydown', function(e) {
                if (e.key === 'Enter') { e.preventDefault(); queueUrl(); }
            });
            urlInput.addEventListener('input', function() {
                if (btnQueue) btnQueue.disabled = !urlInput.value.trim();
            });
        }

        // Search and filter handlers
        var searchInput = view.querySelector('#queueSearch');
        if (searchInput) {
            var searchTimeout = null;
            searchInput.addEventListener('input', function() {
                if (searchTimeout) clearTimeout(searchTimeout);
                searchTimeout = setTimeout(renderQueue, 200);
            });
        }

        var filterSelect = view.querySelector('#queueFilter');
        if (filterSelect) {
            filterSelect.addEventListener('change', renderQueue);
        }
    });
}
