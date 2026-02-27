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
    var _activeComboBoxes = []; // Track all combo-boxes for cleanup

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
            body.innerHTML = '<div class="pt-empty">No downloaded files ready for import. Download some audio first.</div>';
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
        body.querySelectorAll('.pt-row').forEach(function(row) {
            var artistCell = row.querySelector('.yta-artist-cell');
            var albumCell = row.querySelector('.yta-album-cell');

            // Artist combo-box
            var artistCombo = Shared.createSearchableComboBox({
                placeholder: 'Type to search artists...',
                searchFn: _artistSearch
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
                searchFn: albumSearchFn
            });
            albumCombo.setValue(albumCell.getAttribute('data-value'));
            albumCell.appendChild(albumCombo.element);
            albumCell._combo = albumCombo;
            _activeComboBoxes.push(albumCombo);
        });

        // Bind save/delete handlers
        body.querySelectorAll('.yta-btn-save').forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                saveRowTags(btn.closest('.pt-row'));
            });
        });

        body.querySelectorAll('.yta-btn-delete').forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.stopPropagation();
                deleteRow(btn.closest('.pt-row'));
            });
        });

        // Bind row checkboxes
        body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
            cb.addEventListener('change', updateSelectedCount);
        });

        updateSelectedCount();
    }

    function renderImportRow(d) {
        var esc = Shared.escapeHtml;
        return '<div class="pt-row yta-import-row" data-id="' + esc(d.Id) + '">'
            // Checkbox
            + '<div class="yta-import-check">'
            + '<input type="checkbox" class="pt-row-checkbox yta-file-check" data-id="' + esc(d.Id) + '" />'
            + '</div>'
            // Fields area
            + '<div class="yta-import-fields">'
            // Row 1: Title + Artist
            + '<div class="yta-import-line">'
            + '<div class="yta-import-field yta-import-field-grow">'
            + '<label class="yta-import-label">Title</label>'
            + '<input type="text" class="yta-edit-input yta-field-title" value="' + esc(d.MetadataTitle) + '" placeholder="Title" />'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-grow yta-artist-cell" data-value="' + esc(d.Artist) + '">'
            + '<label class="yta-import-label">Artist</label>'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-grow">'
            + '<label class="yta-import-label">Feat.</label>'
            + '<input type="text" class="yta-edit-input yta-field-feat" value="' + esc(d.FeaturedArtist) + '" placeholder="Artist B; Artist C" />'
            + '</div>'
            + '</div>'
            // Row 2: Album Artist + Album + Track # + Year + Genre
            + '<div class="yta-import-line">'
            + '<div class="yta-import-field yta-import-field-grow">'
            + '<label class="yta-import-label">Album Artist</label>'
            + '<input type="text" class="yta-edit-input yta-field-albumArtist" value="' + esc(d.AlbumArtist) + '" placeholder="Defaults to Artist" />'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-grow yta-album-cell" data-value="' + esc(d.Album) + '">'
            + '<label class="yta-import-label">Album</label>'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-sm">'
            + '<label class="yta-import-label">Track</label>'
            + '<input type="number" class="yta-edit-input yta-field-trackNumber" value="' + (d.TrackNumber || '') + '" placeholder="#" />'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-sm">'
            + '<label class="yta-import-label">Year</label>'
            + '<input type="number" class="yta-edit-input yta-field-year" value="' + (d.Year || '') + '" placeholder="Year" />'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-med">'
            + '<label class="yta-import-label">Genre</label>'
            + '<input type="text" class="yta-edit-input yta-field-genre" value="' + esc(d.Genre) + '" placeholder="Rock; Pop" />'
            + '</div>'
            + '<div class="yta-import-field yta-import-field-size">'
            + '<label class="yta-import-label">Size</label>'
            + '<span class="yta-import-size">' + Shared.formatSize(d.FileSize) + '</span>'
            + '</div>'
            + '</div>'
            + '</div>'
            // Actions
            + '<div class="yta-import-actions">'
            + '<button type="button" class="yta-btn yta-btn-save" title="Save tags"><span class="material-icons">save</span></button>'
            + '<button type="button" class="yta-btn yta-btn-delete" title="Delete"><span class="material-icons">delete</span></button>'
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
        // Show/hide bulk edit bar
        var bulkBar = view.querySelector('#bulkEditBar');
        if (bulkBar) {
            if (checked > 0) {
                bulkBar.classList.remove('hidden');
            } else {
                bulkBar.classList.add('hidden');
            }
        }
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
            Genre: row.querySelector('.yta-field-genre').value
        };

        Shared.apiRequest('Tags', 'POST', data)
            .then(function() {
                var btn = row.querySelector('.yta-btn-save');
                btn.classList.add('yta-btn-success');
                setTimeout(function() { btn.classList.remove('yta-btn-success'); }, 1000);
            })
            .catch(function(err) { Dashboard.alert((err && err.Error) || 'Failed to save tags.'); });
    }

    function deleteRow(row) {
        if (!row) return;
        var id = row.getAttribute('data-id');
        if (!confirm('Delete this downloaded file?')) return;

        Shared.apiRequest('Downloads/' + encodeURIComponent(id), 'DELETE')
            .then(function() { loadDownloads(); })
            .catch(function(err) { Dashboard.alert((err && err.Error) || 'Failed to delete.'); });
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
        var bulkGenre = view.querySelector('#bulkGenre').value;
        var bulkYear = view.querySelector('#bulkYear').value;

        // Only apply fields that have values
        if (!bulkArtist && !bulkFeat && !bulkAlbumArtist && !bulkAlbum && !bulkGenre && !bulkYear) {
            Shared.setStatus('importStatus', 'Enter at least one bulk value to apply.', true);
            return;
        }

        var rows = [];
        checkboxes.forEach(function(cb) {
            var row = cb.closest('.pt-row');
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
                Genre: row.querySelector('.yta-field-genre').value
            };

            Shared.apiRequest('Tags', 'POST', data)
                .then(function() {
                    saved++;
                    var btn = row.querySelector('.yta-btn-save');
                    if (btn) {
                        btn.classList.add('yta-btn-success');
                        setTimeout(function() { btn.classList.remove('yta-btn-success'); }, 1000);
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
                if (errCount > 0) parts.push(errCount + ' error(s)');
                var msg = parts.length > 0 ? parts.join(', ') + '.' : 'No files imported.';
                Shared.setStatus('importStatus', msg, errCount > 0 || skipped > 0);
                loadDownloads();
            })
            .catch(function(err) {
                Shared.setStatus('importStatus', (err && err.Error) || 'Import failed.', true);
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
