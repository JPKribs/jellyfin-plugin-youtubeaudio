export default function (view) {
    'use strict';

    var getTabs;
    var Shared = null;
    var _sharedPromise = import('/web/configurationpage?name=youtubeaudio_shared.js').then(function(mod) {
        getTabs = mod.getTabs;
        Shared = mod.createShared(view);
    });

    var _downloads = [];
    var _artistSearch = null;
    var _bulkArtistCombo = null;
    var _bulkAlbumCombo = null;
    var _coverTargets = [];
    var _activeComboBoxes = []; // Track all combo-boxes for cleanup

    // One shared player previews the downloaded file of whichever row is playing.
    var _player = null;
    var _playingId = null;

    // Tidy genre display so every ";" is followed by one space ("Rock;Pop" -> "Rock; Pop").
    // Cosmetic only; the server splits on ";" and writes one GENRE field per genre regardless.
    function normalizeGenre(value) {
        return (value || '').replace(/\s*;\s*/g, '; ').replace(/;\s*$/, '').trim();
    }

    // ============================================
    // DATA LOADING
    // ============================================

    function loadDownloads() {
        var refreshBtn = view.querySelector('#btnRefreshDownloads');
        if (refreshBtn) refreshBtn.classList.add('spinning');

        _sharedPromise.then(function() {
            if (!_artistSearch) _artistSearch = Shared.createDebouncedSearch('Artists', 300);

            Shared.apiRequest('Downloads', 'GET')
                .then(function(response) {
                    _downloads = (response && response.Items) || response || [];
                    renderImportList();
                    if (refreshBtn) refreshBtn.classList.remove('spinning');
                })
                .catch(function() {
                    _downloads = [];
                    renderImportList();
                    if (refreshBtn) refreshBtn.classList.remove('spinning');
                });
        });
    }

    // ============================================
    // ALBUM SEARCH (filtered by artist)
    // ============================================

    function createAlbumSearchForArtist(getArtistFn) {
        var timeout = null;
        return function(query, callback) {
            if (timeout) clearTimeout(timeout);
            if (!query || query.length < 2) {
                callback([]);
                return;
            }
            timeout = setTimeout(function() {
                var artist = getArtistFn();
                var endpoint = 'Albums?query=' + encodeURIComponent(query);
                if (artist) {
                    endpoint += '&artist=' + encodeURIComponent(artist);
                }
                Shared.apiRequest(endpoint, 'GET')
                    .then(function(results) { callback(results || []); })
                    .catch(function() { callback([]); });
            }, 300);
        };
    }

    // ============================================
    // COMBO-BOX CLEANUP
    // ============================================

    function destroyActiveComboBoxes() {
        for (var i = 0; i < _activeComboBoxes.length; i++) {
            _activeComboBoxes[i].destroy();
        }
        _activeComboBoxes = [];
    }

    // ============================================
    // RENDERING (two-line card layout per row)
    // ============================================

    function renderImportList() {
        var body = view.querySelector('#importBody');
        var footer = view.querySelector('#importFooter');
        if (!body) return;

        // Destroy previous combo-boxes before re-rendering
        destroyActiveComboBoxes();

        if (_downloads.length === 0) {
            body.innerHTML = '<div class="jpk-table-empty">No downloaded files ready for import. Download some audio first.</div>';
            if (footer) footer.textContent = '';
            updateSelectedCount();
            return;
        }

        var html = '';
        for (var i = 0; i < _downloads.length; i++) {
            html += renderImportRow(_downloads[i]);
        }
        body.innerHTML = html;
        if (footer) footer.textContent = _downloads.length + ' file' + (_downloads.length !== 1 ? 's' : '');

        // Initialize combo-boxes for each row
        body.querySelectorAll('.jpk-table-row').forEach(function(row) {
            var artistCell = row.querySelector('.yta-artist-cell');
            var albumCell = row.querySelector('.yta-album-cell');
            var saveBtn = row.querySelector('.jpk-row-btn.save');
            function markDirty() { if (saveBtn) saveBtn.classList.add('dirty'); }

            // Artist combo-box
            var artistCombo = Shared.createSearchableComboBox({
                placeholder: 'Type to search artists...',
                searchFn: _artistSearch,
                onInput: markDirty
            });
            artistCombo.setValue(artistCell.getAttribute('data-value'));
            artistCell.appendChild(artistCombo.element);
            artistCell._combo = artistCombo;
            _activeComboBoxes.push(artistCombo);

            // Album combo-box (filtered by artist in this row)
            var albumSearchFn = createAlbumSearchForArtist(function() {
                return artistCombo.getValue();
            });
            var albumCombo = Shared.createSearchableComboBox({
                placeholder: 'Type to search albums...',
                searchFn: albumSearchFn,
                onInput: markDirty
            });
            albumCombo.setValue(albumCell.getAttribute('data-value'));
            albumCell.appendChild(albumCombo.element);
            albumCell._combo = albumCombo;
            _activeComboBoxes.push(albumCombo);
        });

        // Bind save handlers and dirty-state tracking
        body.querySelectorAll('.jpk-table-row').forEach(function(row) {
            var saveBtn = row.querySelector('.jpk-row-btn.save');
            if (saveBtn) {
                saveBtn.addEventListener('click', function(e) {
                    e.stopPropagation();
                    saveRowTags(row);
                });
            }

            // Mark save button dirty on any input change
            row.querySelectorAll('.jpk-edit-input').forEach(function(input) {
                input.addEventListener('input', function() {
                    if (saveBtn) saveBtn.classList.add('dirty');
                });
            });
        });

        // Bind row checkboxes
        body.querySelectorAll('.jpk-table-row-checkbox').forEach(function(cb) {
            cb.addEventListener('change', updateSelectedCount);
        });

        // Bind preview controls
        body.querySelectorAll('.jpk-table-row').forEach(function(row) {
            var id = row.getAttribute('data-id');
            row.querySelector('.yta-cover-btn').addEventListener('click', function(e) {
                e.stopPropagation();
                pickCover([id]);
            });
            row.querySelector('.yta-preview-btn').addEventListener('click', function(e) {
                e.stopPropagation();
                togglePreview(id);
            });
            row.querySelector('.yta-preview-seek').addEventListener('input', function(e) {
                if (_player && _playingId === id) _player.currentTime = parseFloat(e.target.value) || 0;
            });
        });

        // A row that was playing and has since been imported or deleted stops playing.
        if (_playingId && !_downloads.some(function(d) { return d.Id === _playingId; })) stopPreview();
        updatePreviewUi();

        updateSelectedCount();
    }

    // ============================================
    // PREVIEW
    // ============================================

    // formatTime
    // Formats seconds as m:ss for the preview clock.
    //
    // Param: seconds | position or duration in seconds
    function formatTime(seconds) {
        if (!isFinite(seconds) || seconds < 0) seconds = 0;
        var s = Math.floor(seconds);
        return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
    }

    // ensurePlayer
    // Creates the shared audio element on first use and keeps the rows in sync with it.
    //
    // No parameters
    function ensurePlayer() {
        if (_player) return _player;
        _player = new Audio();
        _player.preload = 'auto';
        ['play', 'pause', 'timeupdate', 'loadedmetadata', 'durationchange'].forEach(function(ev) {
            _player.addEventListener(ev, updatePreviewUi);
        });
        _player.addEventListener('ended', function() {
            _player.currentTime = 0;
            updatePreviewUi();
        });
        _player.addEventListener('error', function() {
            if (!_playingId) return;
            stopPreview();
            Dashboard.alert('This file could not be played in this browser.');
        });
        return _player;
    }

    // togglePreview
    // Plays or pauses a row's downloaded file; starting another row stops the current one.
    //
    // Param: id | queue item id of the row
    function togglePreview(id) {
        var player = ensurePlayer();
        if (_playingId === id) {
            if (player.paused) player.play().catch(function() {}); else player.pause();
            return;
        }

        _playingId = id;
        // An <audio> element cannot send headers, so the token rides in the query (ApiKey; the
        // older api_key is rejected by current Jellyfin).
        player.src = ApiClient.getUrl('YouTubeAudio/Downloads/' + encodeURIComponent(id) + '/Audio', { ApiKey: ApiClient.accessToken() });
        player.play().catch(function() {});
        updatePreviewUi();
    }

    // stopPreview
    // Stops playback and releases the current file.
    //
    // No parameters
    function stopPreview() {
        _playingId = null;
        if (_player) {
            _player.pause();
            _player.removeAttribute('src');
            _player.load();
        }
        updatePreviewUi();
    }

    // updatePreviewUi
    // Shows play/pause on every row, and the seek bar and clock on the row that is playing.
    //
    // No parameters
    function updatePreviewUi() {
        view.querySelectorAll('#importBody .jpk-table-row').forEach(function(row) {
            var active = row.getAttribute('data-id') === _playingId;
            var playing = active && _player && !_player.paused;
            var btn = row.querySelector('.yta-preview-btn');
            if (!btn) return;
            btn.querySelector('.material-icons').textContent = playing ? 'pause' : 'play_arrow';
            btn.title = playing ? 'Pause preview' : 'Play preview';
            btn.classList.toggle('active', active);

            var bar = row.querySelector('.yta-preview');
            bar.hidden = !active;
            if (!active || !_player) return;
            var duration = isFinite(_player.duration) ? _player.duration : 0;
            var seek = bar.querySelector('.yta-preview-seek');
            seek.max = duration || 0;
            if (document.activeElement !== seek) seek.value = _player.currentTime || 0;
            bar.querySelector('.yta-preview-time').textContent = formatTime(_player.currentTime) + ' / ' + formatTime(duration);
        });
    }

    // renderCover
    // The row's cover thumbnail, or an "add picture" icon when the file has none. An <img>
    // cannot send headers, so the token rides in the query, like the audio preview.
    function renderCover(id, hasCover) {
        if (!hasCover) return '<span class="material-icons">add_photo_alternate</span>';
        var url = ApiClient.getUrl('YouTubeAudio/Downloads/' + encodeURIComponent(id) + '/Cover', { ApiKey: ApiClient.accessToken(), v: Date.now() });
        return '<img alt="" src="' + Shared.escapeHtml(url) + '" />';
    }

    function renderImportRow(d) {
        var esc = Shared.escapeHtml;
        return '<div class="jpk-table-row jpk-edit-row" data-id="' + esc(d.Id) + '">'
            // Line 1: Checkbox | Preview (button, then seek bar while playing) | Size | Save
            + '<div class="jpk-edit-line">'
            + '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="jpk-table-row-checkbox yta-file-check" data-id="' + esc(d.Id) + '" /><span class="checkboxLabel"></span></label>'
            + '<button type="button" class="yta-cover-btn" title="Set cover art" aria-label="Set cover art">' + renderCover(d.Id, d.HasCover) + '</button>'
            + '<div class="yta-preview-group">'
            + '<button type="button" class="jpk-row-btn yta-preview-btn" title="Play preview"><span class="material-icons">play_arrow</span></button>'
            + '<div class="yta-preview" hidden>'
            + '<input type="range" class="yta-preview-seek" min="0" max="0" step="0.1" value="0" aria-label="Seek" />'
            + '<span class="yta-preview-time">0:00 / 0:00</span>'
            + '</div>'
            + '</div>'
            + (d.InLibrary ? '<span class="yta-in-library" title="This song is already in your music library. Importing it adds a second copy.">Already in your library</span>' : '')
            + '<span class="jpk-edit-secondary">' + Shared.formatSize(d.FileSize) + '</span>'
            + '<button type="button" class="jpk-row-btn save" title="Save tags"><span class="material-icons">save</span></button>'
            + '</div>'
            // Line 2: Title (6/7) | Track # (1/7)
            + '<div class="jpk-edit-line">'
            + '<div class="jpk-field" style="flex:6">'
            + '<label>Title</label>'
            + '<input type="text" class="jpk-edit-input yta-field-title" value="' + esc(d.MetadataTitle) + '" placeholder="Title" />'
            + '</div>'
            + '<div class="jpk-field" style="flex:1">'
            + '<label>Track #</label>'
            + '<input type="number" class="jpk-edit-input yta-field-trackNumber" value="' + (d.TrackNumber || '') + '" placeholder="#" />'
            + '</div>'
            + '</div>'
            // Line 3: Artist | Album Artist | Feat. Artists (equal) — matches the bulk-edit bar order
            + '<div class="jpk-edit-line">'
            + '<div class="jpk-field yta-artist-cell" data-value="' + esc(d.Artist) + '">'
            + '<label>Artist</label>'
            + '</div>'
            + '<div class="jpk-field">'
            + '<label>Album Artist</label>'
            + '<input type="text" class="jpk-edit-input yta-field-albumArtist" value="' + esc(d.AlbumArtist) + '" placeholder="Defaults to Artist" />'
            + '</div>'
            + '<div class="jpk-field">'
            + '<label>Feat. Artists</label>'
            + '<input type="text" class="jpk-edit-input yta-field-feat" value="' + esc(d.FeaturedArtist) + '" placeholder="Artist B; Artist C" />'
            + '</div>'
            + '</div>'
            // Line 4: Album (3/7) | Year (1/7) | Genre (3/7)
            + '<div class="jpk-edit-line">'
            + '<div class="jpk-field yta-album-cell" style="flex:3" data-value="' + esc(d.Album) + '">'
            + '<label>Album</label>'
            + '</div>'
            + '<div class="jpk-field" style="flex:1">'
            + '<label>Year</label>'
            + '<input type="number" class="jpk-edit-input yta-field-year" value="' + (d.Year || '') + '" placeholder="Year" />'
            + '</div>'
            + '<div class="jpk-field" style="flex:3">'
            + '<label>Genre</label>'
            + '<input type="text" class="jpk-edit-input yta-field-genre" value="' + esc(d.Genre) + '" placeholder="Classical; Baroque" />'
            + '</div>'
            + '</div>'
            + '</div>';
    }

    // ============================================
    // SELECTION
    // ============================================

    function updateSelectedCount() {
        var checked = view.querySelectorAll('.yta-file-check:checked').length;
        var countEl = view.querySelector('#selectedCount');
        if (countEl) {
            countEl.textContent = checked > 0 ? checked + ' selected' : '';
        }

        // Toggle Import/Delete button visibility
        var importBtn = view.querySelector('#btnImportSelected');
        if (importBtn) importBtn.style.visibility = checked > 0 ? '' : 'hidden';

        var deleteBtn = view.querySelector('#btnDeleteSelected');
        if (deleteBtn) deleteBtn.style.visibility = checked > 0 ? '' : 'hidden';

        // Disable Apply to Selected when nothing selected
        var applyBtn = view.querySelector('#btnApplyBulk');
        if (applyBtn) applyBtn.disabled = checked === 0;
        var coverBtn = view.querySelector('#btnBulkCover');
        if (coverBtn) coverBtn.disabled = checked === 0;
    }

    // ============================================
    // ACTIONS
    // ============================================

    function saveRowTags(row) {
        if (!row) return;
        var id = row.getAttribute('data-id');
        var artistCell = row.querySelector('.yta-artist-cell');
        var albumCell = row.querySelector('.yta-album-cell');

        var data = {
            Id: id,
            Title: row.querySelector('.yta-field-title').value,
            Artist: artistCell._combo ? artistCell._combo.getValue() : '',
            FeaturedArtist: row.querySelector('.yta-field-feat').value || '',
            AlbumArtist: row.querySelector('.yta-field-albumArtist').value || '',
            Album: albumCell._combo ? albumCell._combo.getValue() : '',
            TrackNumber: parseInt(row.querySelector('.yta-field-trackNumber').value) || null,
            Year: parseInt(row.querySelector('.yta-field-year').value) || null,
            Genre: normalizeGenre(row.querySelector('.yta-field-genre').value)
        };
        row.querySelector('.yta-field-genre').value = data.Genre;

        Shared.apiRequest('Tags', 'POST', data)
            .then(function() {
                var btn = row.querySelector('.jpk-row-btn.save');
                btn.classList.remove('dirty');
                btn.classList.add('success');
                setTimeout(function() { btn.classList.remove('success'); }, 1000);
            })
            .catch(function(err) { Dashboard.alert((err && err.Error) || 'Failed to save tags.'); });
    }

    function applyBulkEdit() {
        var checkboxes = view.querySelectorAll('.yta-file-check:checked');
        if (checkboxes.length === 0) {
            Shared.setStatus('importStatus', 'Select at least one file.', true);
            return;
        }

        var bulkArtist = _bulkArtistCombo ? _bulkArtistCombo.getValue() : '';
        var bulkFeat = view.querySelector('#bulkFeat').value;
        var bulkAlbumArtist = view.querySelector('#bulkAlbumArtist').value;
        var bulkAlbum = _bulkAlbumCombo ? _bulkAlbumCombo.getValue() : '';
        var bulkGenre = normalizeGenre(view.querySelector('#bulkGenre').value);
        var bulkYear = view.querySelector('#bulkYear').value;

        // Only apply fields that have values
        if (!bulkArtist && !bulkFeat && !bulkAlbumArtist && !bulkAlbum && !bulkGenre && !bulkYear) {
            Shared.setStatus('importStatus', 'Enter at least one bulk value to apply.', true);
            return;
        }

        var rows = [];
        checkboxes.forEach(function(cb) {
            var row = cb.closest('.jpk-table-row');
            if (row) rows.push(row);
        });

        // Update UI fields in each selected row
        rows.forEach(function(row) {
            if (bulkArtist) {
                var artistCell = row.querySelector('.yta-artist-cell');
                if (artistCell && artistCell._combo) artistCell._combo.setValue(bulkArtist);
            }
            if (bulkFeat) {
                var featInput = row.querySelector('.yta-field-feat');
                if (featInput) featInput.value = bulkFeat;
            }
            if (bulkAlbumArtist) {
                var albumArtistInput = row.querySelector('.yta-field-albumArtist');
                if (albumArtistInput) albumArtistInput.value = bulkAlbumArtist;
            }
            if (bulkAlbum) {
                var albumCell = row.querySelector('.yta-album-cell');
                if (albumCell && albumCell._combo) albumCell._combo.setValue(bulkAlbum);
            }
            if (bulkGenre) {
                var genreInput = row.querySelector('.yta-field-genre');
                if (genreInput) genreInput.value = bulkGenre;
            }
            if (bulkYear) {
                var yearInput = row.querySelector('.yta-field-year');
                if (yearInput) yearInput.value = bulkYear;
            }
        });

        // Save tags for each selected row sequentially
        var saved = 0;
        var errors = 0;

        function saveNext(index) {
            if (index >= rows.length) {
                var msg = 'Applied to ' + saved + ' file(s).';
                if (errors > 0) msg += ' ' + errors + ' error(s).';
                Shared.setStatus('importStatus', msg, errors > 0);
                return;
            }

            var row = rows[index];
            var id = row.getAttribute('data-id');
            var artistCell = row.querySelector('.yta-artist-cell');
            var albumCell = row.querySelector('.yta-album-cell');

            var data = {
                Id: id,
                Title: row.querySelector('.yta-field-title').value,
                Artist: artistCell._combo ? artistCell._combo.getValue() : '',
                FeaturedArtist: row.querySelector('.yta-field-feat').value || '',
                AlbumArtist: row.querySelector('.yta-field-albumArtist').value || '',
                Album: albumCell._combo ? albumCell._combo.getValue() : '',
                TrackNumber: parseInt(row.querySelector('.yta-field-trackNumber').value) || null,
                Year: parseInt(row.querySelector('.yta-field-year').value) || null,
                Genre: normalizeGenre(row.querySelector('.yta-field-genre').value)
            };
            row.querySelector('.yta-field-genre').value = data.Genre;

            Shared.apiRequest('Tags', 'POST', data)
                .then(function() {
                    saved++;
                    var btn = row.querySelector('.jpk-row-btn.save');
                    if (btn) {
                        btn.classList.add('success');
                        setTimeout(function() { btn.classList.remove('success'); }, 1000);
                    }
                    saveNext(index + 1);
                })
                .catch(function() {
                    errors++;
                    saveNext(index + 1);
                });
        }

        Shared.setStatus('importStatus', 'Applying to ' + rows.length + ' file(s)...', false);
        saveNext(0);
    }

    function importSelected() {
        var checkboxes = view.querySelectorAll('.yta-file-check:checked');
        var selectedIds = [];
        checkboxes.forEach(function(cb) { selectedIds.push(cb.getAttribute('data-id')); });

        if (selectedIds.length === 0) {
            Shared.setStatus('importStatus', 'Select at least one file to import.', true);
            return;
        }

        Shared.setStatus('importStatus', 'Importing ' + selectedIds.length + ' file(s)...', false);

        Shared.apiRequest('Import', 'POST', { Ids: selectedIds })
            .then(function(result) {
                var imported = result && result.ImportedIds ? result.ImportedIds.length : 0;
                var replaced = result && result.ReplacedIds ? result.ReplacedIds.length : 0;
                var skipped = result && result.SkippedIds ? result.SkippedIds.length : 0;
                var errCount = result && result.Errors ? result.Errors.length : 0;
                var parts = [];
                if (imported > 0) {
                    var fresh = imported - replaced;
                    if (fresh > 0) parts.push(fresh + ' imported');
                    if (replaced > 0) parts.push(replaced + ' replaced');
                }
                if (skipped > 0) parts.push(skipped + ' skipped (duplicate)');
                if (result && result.RemovedFiles > 0) parts.push(result.RemovedFiles + ' single(s) in the library replaced by the album version');
                if (errCount > 0) parts.push(errCount + ' error(s)');
                var msg = parts.length > 0 ? parts.join(', ') + '.' : 'No files imported.';
                Shared.setStatus('importStatus', msg, errCount > 0 || skipped > 0);
                loadDownloads();
            })
            .catch(function(err) {
                Shared.setStatus('importStatus', (err && err.Error) || 'Import failed.', true);
            });
    }

    // ============================================
    // COVER ART
    // ============================================

    // pickCover
    // Opens the file picker; the chosen picture becomes the cover of the given files.
    function pickCover(ids) {
        var input = view.querySelector('#coverFileInput');
        if (!input || ids.length === 0) return;
        _coverTargets = ids;
        input.value = '';
        input.click();
    }

    // squareCover
    // Crops a picture to a centred square (at most 1200 px) and returns it as base64 JPEG, so
    // every cover is 1:1 whatever shape was uploaded.
    function squareCover(file) {
        return new Promise(function(resolve, reject) {
            if (!/^image\/(jpeg|png|webp)$/.test(file.type)) {
                reject(new Error('Choose a JPEG, PNG or WebP picture.'));
                return;
            }
            var url = URL.createObjectURL(file);
            var img = new Image();
            img.onload = function() {
                var side = Math.min(img.naturalWidth, img.naturalHeight);
                var size = Math.min(side, 1200);
                var canvas = document.createElement('canvas');
                canvas.width = size;
                canvas.height = size;
                canvas.getContext('2d').drawImage(img, (img.naturalWidth - side) / 2, (img.naturalHeight - side) / 2, side, side, 0, 0, size, size);
                URL.revokeObjectURL(url);
                resolve(canvas.toDataURL('image/jpeg', 0.9).split(',')[1]);
            };
            img.onerror = function() {
                URL.revokeObjectURL(url);
                reject(new Error('That file could not be opened as a picture.'));
            };
            img.src = url;
        });
    }

    // uploadCover
    // Embeds the chosen picture in each target file, one at a time, and refreshes their
    // thumbnails in place (re-rendering would drop unsaved edits).
    function uploadCover(file) {
        var ids = _coverTargets.slice();
        _coverTargets = [];
        squareCover(file).then(function(data) {
            Shared.setStatus('importStatus', 'Setting cover art on ' + ids.length + ' file(s)...', false);
            var failed = 0;
            function next(index) {
                if (index >= ids.length) {
                    var done = ids.length - failed;
                    Shared.setStatus('importStatus', 'Cover art set on ' + done + ' file(s).' + (failed ? ' ' + failed + ' failed.' : ''), failed > 0);
                    return;
                }
                var id = ids[index];
                Shared.apiRequest('Downloads/' + encodeURIComponent(id) + '/Cover', 'POST', { Data: data })
                    .then(function() {
                        var btn = view.querySelector('.jpk-table-row[data-id="' + CSS.escape(id) + '"] .yta-cover-btn');
                        if (btn) btn.innerHTML = renderCover(id, true);
                        var d = _downloads.find(function(x) { return x.Id === id; });
                        if (d) d.HasCover = true;
                    })
                    .catch(function() { failed++; })
                    .then(function() { next(index + 1); });
            }
            next(0);
        }).catch(function(err) {
            Shared.setStatus('importStatus', err.message, true);
        });
    }

    function deleteSelected() {
        var checkboxes = view.querySelectorAll('.yta-file-check:checked');
        var selectedIds = [];
        checkboxes.forEach(function(cb) { selectedIds.push(cb.getAttribute('data-id')); });

        if (selectedIds.length === 0) {
            Shared.setStatus('importStatus', 'Select at least one file to delete.', true);
            return;
        }

        if (!confirm('Delete ' + selectedIds.length + ' downloaded file(s)? This cannot be undone.')) return;

        Shared.setStatus('importStatus', 'Deleting ' + selectedIds.length + ' file(s)...', false);

        Shared.apiRequest('Downloads/BatchDelete', 'POST', { Ids: selectedIds })
            .then(function(result) {
                var deleted = result && result.Deleted || 0;
                var errCount = result && result.Errors || 0;
                var msg = 'Deleted ' + deleted + ' file(s).';
                if (errCount > 0) msg += ' ' + errCount + ' error(s).';
                Shared.setStatus('importStatus', msg, errCount > 0);
                loadDownloads();
            })
            .catch(function() {
                Shared.setStatus('importStatus', 'Failed to delete files.', true);
                loadDownloads();
            });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function() {
        _sharedPromise.then(function() {
            LibraryMenu.setTabs('youtubeaudio', 1, getTabs);
            loadDownloads();
        });
    });

    view.addEventListener('viewhide', function() {
        destroyActiveComboBoxes();
        stopPreview();
    });

    _sharedPromise.then(function() {
        // Initialize bulk artist combo-box
        if (!_artistSearch) _artistSearch = Shared.createDebouncedSearch('Artists', 300);
        var bulkArtistCell = view.querySelector('#bulkArtistCell');
        if (bulkArtistCell && !_bulkArtistCombo) {
            _bulkArtistCombo = Shared.createSearchableComboBox({
                placeholder: 'Type to search artists...',
                searchFn: _artistSearch
            });
            bulkArtistCell.appendChild(_bulkArtistCombo.element);
        }

        // Initialize bulk album combo-box (filtered by bulk artist)
        var bulkAlbumCell = view.querySelector('#bulkAlbumCell');
        if (bulkAlbumCell && !_bulkAlbumCombo) {
            var bulkAlbumSearchFn = createAlbumSearchForArtist(function() {
                return _bulkArtistCombo ? _bulkArtistCombo.getValue() : '';
            });
            _bulkAlbumCombo = Shared.createSearchableComboBox({
                placeholder: 'Type to search albums...',
                searchFn: bulkAlbumSearchFn
            });
            bulkAlbumCell.appendChild(_bulkAlbumCombo.element);
        }

        var btnApplyBulk = Shared.getEl('btnApplyBulk');
        if (btnApplyBulk) btnApplyBulk.addEventListener('click', applyBulkEdit);

        var btnBulkCover = Shared.getEl('btnBulkCover');
        if (btnBulkCover) btnBulkCover.addEventListener('click', function() {
            var ids = [];
            view.querySelectorAll('.yta-file-check:checked').forEach(function(cb) { ids.push(cb.getAttribute('data-id')); });
            pickCover(ids);
        });

        var coverInput = Shared.getEl('coverFileInput');
        if (coverInput) coverInput.addEventListener('change', function() {
            if (coverInput.files && coverInput.files[0]) uploadCover(coverInput.files[0]);
        });

        var btnImport = Shared.getEl('btnImportSelected');
        if (btnImport) btnImport.addEventListener('click', importSelected);

        var btnDelete = Shared.getEl('btnDeleteSelected');
        if (btnDelete) btnDelete.addEventListener('click', deleteSelected);

        var btnRefresh = Shared.getEl('btnRefreshDownloads');
        if (btnRefresh) btnRefresh.addEventListener('click', loadDownloads);

        var chkSelectAll = Shared.getEl('chkSelectAll');
        if (chkSelectAll) {
            chkSelectAll.addEventListener('change', function() {
                view.querySelectorAll('.yta-file-check').forEach(function(cb) {
                    cb.checked = chkSelectAll.checked;
                });
                updateSelectedCount();
            });
        }
    });
}
