// youtubeaudio_import.js
// Drives the Import tab, where downloaded songs are tagged, given covers, and imported into the library.

import { createPage, createFilteredList, confirmDialog } from './configurationpage?name=youtubeaudio_shared.js';

// normalizeGenre
// Puts one space after every ";" in a genre list. The server splits on ";" either way.
//
// Inputs
// value [string]: The genre text.
//
// Output
// [string]: The tidied text.
function normalizeGenre(value) {
    return (value || '').replace(/\s*;\s*/g, '; ').replace(/;\s*$/, '').trim();
}

// countLabel
// Names a count with the right plural.
//
// Inputs
// count [number]: How many.
// one [string]: The word for one.
// many [string]: The word for several.
//
// Output
// [string]: Such as "1 file" or "3 files".
function countLabel(count, one, many) {
    return count + ' ' + (count === 1 ? one : many);
}

// importResultMessage
// Says what an import did.
//
// Inputs
// result [object]: The server's ImportResult.
//
// Output
// [string]: The status message.
function importResultMessage(result) {
    result = result || {};
    var imported = result.ImportedIds ? result.ImportedIds.length : 0;
    var replaced = result.ReplacedIds ? result.ReplacedIds.length : 0;
    var skipped = result.SkippedIds ? result.SkippedIds.length : 0;
    var errors = result.Errors ? result.Errors.length : 0;
    var parts = [];
    if (imported - replaced > 0) parts.push((imported - replaced) + ' imported');
    if (replaced > 0) parts.push(replaced + ' replaced');
    if (skipped > 0) parts.push(skipped + ' skipped as duplicates');
    if (result.RemovedFiles > 0) parts.push(countLabel(result.RemovedFiles, 'library single', 'library singles') + ' replaced by the album version');
    if (errors > 0) parts.push(errors + ' could not be imported');
    if (!parts.length) return 'No files imported.';
    var text = parts.join(', ') + '.';
    return text.charAt(0).toUpperCase() + text.slice(1);
}

export default function (view) {
    'use strict';

    var Shared = null;

    var _downloads = [];
    var _list = null;
    var _artistSearch = null;
    var _bulkArtistCombo = null;
    var _bulkAlbumCombo = null;
    var _coverTargets = [];
    var _coverSettings = null;
    var _rowCombos = {};

    // One shared player previews the downloaded file of whichever row is playing.
    var _player = null;
    var _playingId = null;

    // loadDownloads
    // Reads the downloaded files and hands them to the list.
    function loadDownloads() {
        if (!_artistSearch) _artistSearch = Shared.createDebouncedSearch('Artists', 300);

        Shared.apiRequest('Downloads', 'GET')
            .then(function (response) { _downloads = (response && response.Items) || response || []; })
            .catch(function () { _downloads = []; })
            .then(renderImportList);
    }

    // createAlbumSearchForArtist
    // Builds a combo box search of library albums, limited to one artist when there is one.
    //
    // Inputs
    // getArtistFn [function]: Returns the artist to limit the search to.
    //
    // Output
    // [function]: The searchFn for a combo box.
    function createAlbumSearchForArtist(getArtistFn) {
        var timeout = null;
        return function (query, callback) {
            if (timeout) clearTimeout(timeout);
            if (!query || query.length < 2) {
                callback([]);
                return;
            }
            timeout = setTimeout(function () {
                var artist = getArtistFn();
                var endpoint = 'Albums?query=' + encodeURIComponent(query);
                if (artist) endpoint += '&artist=' + encodeURIComponent(artist);
                Shared.apiRequest(endpoint, 'GET')
                    .then(function (results) { callback(results || []); })
                    .catch(function () { callback([]); });
            }, 300);
        };
    }

    // destroyRowCombos
    // Destroys the combo boxes of one row, or of every row.
    //
    // Inputs
    // id [string]: The row, or nothing for every row.
    function destroyRowCombos(id) {
        Object.keys(_rowCombos).forEach(function (key) {
            if (id && key !== id) return;
            _rowCombos[key].forEach(function (combo) { combo.destroy(); });
            delete _rowCombos[key];
        });
    }

    // rowFor
    // Finds the drawn row of a downloaded file.
    //
    // Inputs
    // id [string]: The file's queue item id.
    //
    // Output
    // [HTMLElement]: The row, or null.
    function rowFor(id) {
        return view.querySelector('.yta-import-row[data-id="' + CSS.escape(id) + '"]');
    }

    // selectedRows
    // Returns the drawn rows of the selected files.
    //
    // Output
    // [HTMLElement[]]: The rows.
    function selectedRows() {
        return (_list ? _list.getSelected() : []).map(rowFor).filter(Boolean);
    }

    // buildList
    // Builds the files as a Base Filtered List with selection, Import and Delete.
    function buildList() {
        var host = Shared.getEl('importList');
        if (!host || _list) return;
        _list = createFilteredList({
            items: [],
            rowKey: 'Id',
            render: renderImportRow,
            search: false,
            selectable: 'multiple',
            bulkActions: [
                { id: 'import', label: 'Import', icon: 'library_add', variant: 'primary', onClick: importSelected },
                { id: 'delete', label: 'Delete', icon: 'delete', variant: 'destructive', onClick: deleteSelected }
            ],
            actions: [{ id: 'refresh', label: 'Refresh', icon: 'refresh', onClick: loadDownloads }],
            emptyText: 'No downloaded files yet. Download songs on the Download tab.',
            label: 'Downloaded files',
            onSelectionChange: updateBulkButtons
        });
        host.appendChild(_list.element);
    }

    // renderImportList
    // Hands the files to the list, drops the combo boxes of files that are gone, and stops a preview whose file is gone.
    function renderImportList() {
        var ids = _downloads.map(function (d) { return d.Id; });
        Object.keys(_rowCombos).forEach(function (id) { if (ids.indexOf(id) === -1) destroyRowCombos(id); });
        if (_list) _list.setItems(_downloads);
        if (_playingId && ids.indexOf(_playingId) === -1) stopPreview();
        updatePreviewUi();
        updateBulkButtons();
    }

    // formatTime
    // Formats seconds as m:ss for the preview clock.
    //
    // Inputs
    // seconds [number]: A position or a length.
    //
    // Output
    // [string]: Such as 3:07.
    function formatTime(seconds) {
        if (!isFinite(seconds) || seconds < 0) seconds = 0;
        var s = Math.floor(seconds);
        return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
    }

    // ensurePlayer
    // Creates the shared audio element on first use and keeps the rows in step with it.
    //
    // Output
    // [HTMLAudioElement]: The player.
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
            Shared.setStatus('importStatus', 'Could not play this file in this browser.', true);
        });
        return _player;
    }

    // togglePreview
    // Plays or pauses a row's downloaded file. The token rides in the query, since an audio element cannot send headers.
    //
    // Inputs
    // id [string]: The file's queue item id.
    function togglePreview(id) {
        var player = ensurePlayer();
        if (_playingId === id) {
            if (player.paused) player.play().catch(function() {}); else player.pause();
            return;
        }

        _playingId = id;
        player.src = ApiClient.getUrl('YouTubeAudio/Downloads/' + encodeURIComponent(id) + '/Audio', { ApiKey: ApiClient.accessToken() });
        player.play().catch(function() {});
        updatePreviewUi();
    }

    // stopPreview
    // Stops playback and releases the current file.
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
    // Shows play or pause on every row, and the seek bar and clock on the row that is playing.
    function updatePreviewUi() {
        view.querySelectorAll('.yta-import-row').forEach(function(row) {
            var active = row.getAttribute('data-id') === _playingId;
            var playing = active && _player && !_player.paused;
            var btn = row.querySelector('.yta-preview-btn');
            if (!btn) return;
            btn.querySelector('.material-icons').textContent = playing ? 'pause' : 'play_arrow';
            btn.title = playing ? 'Pause' : 'Play';
            btn.setAttribute('aria-label', btn.title);
            btn.classList.toggle('active', active);

            var bar = row.querySelector('.yta-preview');
            bar.hidden = !active;
            if (!active || !_player) return;
            var duration = isFinite(_player.duration) ? _player.duration : 0;
            var seek = bar.querySelector('.yta-preview-seek');
            seek.max = duration;
            if (document.activeElement !== seek) seek.value = _player.currentTime || 0;
            bar.querySelector('.yta-preview-time').textContent = formatTime(_player.currentTime) + ' / ' + formatTime(duration);
        });
    }

    // renderCover
    // Draws a row's cover thumbnail, or an add picture icon when the file has none. The token rides in the query, since an image cannot send headers.
    //
    // Inputs
    // id [string]: The file's queue item id.
    // hasCover [boolean]: Whether the file has a cover.
    //
    // Output
    // [string]: The HTML.
    function renderCover(id, hasCover) {
        if (!hasCover) return '<span class="material-icons" aria-hidden="true">add_photo_alternate</span>';
        var url = ApiClient.getUrl('YouTubeAudio/Downloads/' + encodeURIComponent(id) + '/Cover', { ApiKey: ApiClient.accessToken(), v: Date.now() });
        return '<img alt="" src="' + Shared.escapeHtml(url) + '" />';
    }

    // renderImportRow
    // Draws one file as an edit row of its tags, with its cover, preview, and save controls, and wires them.
    //
    // Inputs
    // d [object]: The downloaded file.
    // index [number]: Its place in the list.
    // check [HTMLInputElement]: The row's selection checkbox, placed at the start of the first line.
    //
    // Output
    // [HTMLElement]: The row.
    function renderImportRow(d, index, check) {
        var esc = Shared.escapeHtml;
        var row = document.createElement('div');
        row.className = 'base-edit-row yta-import-row';
        row.setAttribute('data-id', d.Id);
        row.innerHTML =
            '<div class="base-edit-line yta-import-head">'
            + '<button type="button" class="yta-cover-btn" title="Set Cover" aria-label="Set Cover">' + renderCover(d.Id, d.HasCover) + '</button>'
            + '<button type="button" class="base-row-btn yta-wand-btn" title="Generate Cover" aria-label="Generate Cover"><span class="material-icons" aria-hidden="true">auto_fix_high</span></button>'
            + '<div class="yta-preview-group">'
            + '<button type="button" class="base-row-btn yta-preview-btn" title="Play" aria-label="Play"><span class="material-icons" aria-hidden="true">play_arrow</span></button>'
            + '<div class="yta-preview" hidden>'
            + '<input type="range" class="yta-preview-seek" min="0" max="0" step="0.1" value="0" aria-label="Seek" />'
            + '<span class="yta-preview-time">0:00 / 0:00</span>'
            + '</div>'
            + '</div>'
            + (d.InLibrary ? '<span title="The library already has this song. Importing it adds a second copy.">' + Shared.badge('orange', 'In Library') + '</span>' : '')
            + '<span class="base-edit-secondary">' + esc(Shared.formatSize(d.FileSize)) + '</span>'
            + '<button type="button" class="base-row-btn save" title="Save Tags" aria-label="Save Tags"><span class="material-icons" aria-hidden="true">save</span></button>'
            + '</div>'
            + '<div class="base-edit-line">'
            + '<div class="base-field yta-grow-6"><label>Title</label>'
            + '<input type="text" class="base-edit-input yta-field-title" value="' + esc(d.MetadataTitle) + '" placeholder="Title" aria-label="Title" /></div>'
            + '<div class="base-field yta-grow-1"><label>Track</label>'
            + '<input type="number" class="base-edit-input yta-field-trackNumber" value="' + esc(d.TrackNumber || '') + '" placeholder="1" aria-label="Track" /></div>'
            + '</div>'
            + '<div class="base-edit-line">'
            + '<div class="base-field yta-artist-cell"><label>Artist</label></div>'
            + '<div class="base-field"><label>Album Artist</label>'
            + '<input type="text" class="base-edit-input yta-field-albumArtist" value="' + esc(d.AlbumArtist) + '" placeholder="Artist" aria-label="Album Artist" /></div>'
            + '<div class="base-field"><label>Featured Artists</label>'
            + '<input type="text" class="base-edit-input yta-field-feat" value="' + esc(d.FeaturedArtist) + '" placeholder="Artist B; Artist C" aria-label="Featured Artists" /></div>'
            + '</div>'
            + '<div class="base-edit-line">'
            + '<div class="base-field yta-album-cell yta-grow-3"><label>Album</label></div>'
            + '<div class="base-field yta-grow-1"><label>Year</label>'
            + '<input type="number" class="base-edit-input yta-field-year" value="' + esc(d.Year || '') + '" placeholder="2024" aria-label="Year" /></div>'
            + '<div class="base-field yta-grow-3"><label>Genre</label>'
            + '<input type="text" class="base-edit-input yta-field-genre" value="' + esc(d.Genre) + '" placeholder="Classical; Baroque" aria-label="Genre" /></div>'
            + '</div>';

        var head = row.querySelector('.yta-import-head');
        if (check) head.insertBefore(check, head.firstChild);

        var saveBtn = row.querySelector('.base-row-btn.save');
        function markDirty() { saveBtn.classList.add('dirty'); }

        destroyRowCombos(d.Id);
        var artistCell = row.querySelector('.yta-artist-cell');
        var artistCombo = Shared.createSearchableComboBox({
            placeholder: 'Search artists',
            searchFn: _artistSearch,
            onInput: markDirty
        });
        artistCombo.setValue(d.Artist || '');
        artistCell.appendChild(artistCombo.element);
        artistCell._combo = artistCombo;

        var albumCell = row.querySelector('.yta-album-cell');
        var albumCombo = Shared.createSearchableComboBox({
            placeholder: 'Search albums',
            searchFn: createAlbumSearchForArtist(function () { return artistCombo.getValue(); }),
            onInput: markDirty,
            onSelect: function (value) {
                markDirty();
                takeAlbumCover(row, value);
            }
        });
        albumCombo.setValue(d.Album || '');
        albumCell.appendChild(albumCombo.element);
        albumCell._combo = albumCombo;
        _rowCombos[d.Id] = [artistCombo, albumCombo];

        saveBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            saveRowTags(row);
        });
        row.querySelectorAll('.base-edit-input').forEach(function (input) {
            input.addEventListener('input', markDirty);
        });
        row.querySelector('.yta-cover-btn').addEventListener('click', function (e) {
            e.stopPropagation();
            pickCover([d.Id]);
        });
        row.querySelector('.yta-wand-btn').addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            generateCoverFor(row);
        });
        row.querySelector('.yta-preview-btn').addEventListener('click', function (e) {
            e.stopPropagation();
            togglePreview(d.Id);
        });
        row.querySelector('.yta-preview-seek').addEventListener('input', function (e) {
            if (_player && _playingId === d.Id) _player.currentTime = parseFloat(e.target.value) || 0;
        });

        return row;
    }

    // updateBulkButtons
    // Turns the bulk edit buttons on while files are selected.
    function updateBulkButtons() {
        var none = !_list || _list.getSelected().length === 0;
        var applyBtn = Shared.getEl('btnApplyBulk');
        if (applyBtn) applyBtn.disabled = none;
        var coverBtn = Shared.getEl('btnBulkCover');
        if (coverBtn) coverBtn.disabled = none;
    }

    // readRowTags
    // Reads a row's tag fields into the shape the Tags route takes, and tidies its genre field.
    //
    // Inputs
    // row [HTMLElement]: The file's row.
    //
    // Output
    // [object]: The TagUpdateRequest.
    function readRowTags(row) {
        var artistCell = row.querySelector('.yta-artist-cell');
        var albumCell = row.querySelector('.yta-album-cell');
        var data = {
            Id: row.getAttribute('data-id'),
            Title: row.querySelector('.yta-field-title').value,
            Artist: artistCell._combo ? artistCell._combo.getValue() : '',
            FeaturedArtist: row.querySelector('.yta-field-feat').value || '',
            AlbumArtist: row.querySelector('.yta-field-albumArtist').value || '',
            Album: albumCell._combo ? albumCell._combo.getValue() : '',
            TrackNumber: parseInt(row.querySelector('.yta-field-trackNumber').value, 10) || null,
            Year: parseInt(row.querySelector('.yta-field-year').value, 10) || null,
            Genre: normalizeGenre(row.querySelector('.yta-field-genre').value)
        };
        row.querySelector('.yta-field-genre').value = data.Genre;
        return data;
    }

    // flashSaved
    // Marks a row's save button as saved for a moment.
    //
    // Inputs
    // row [HTMLElement]: The file's row.
    function flashSaved(row) {
        var btn = row.querySelector('.base-row-btn.save');
        if (!btn) return;
        btn.classList.remove('dirty');
        btn.classList.add('success');
        setTimeout(function () { btn.classList.remove('success'); }, 1000);
    }

    // saveRowTags
    // Writes one row's tags to its file.
    //
    // Inputs
    // row [HTMLElement]: The file's row.
    function saveRowTags(row) {
        Shared.apiRequest('Tags', 'POST', readRowTags(row))
            .then(function () { flashSaved(row); })
            .catch(function (err) {
                Shared.errorText(err, 'Could not save the tags.').then(function (text) { Shared.setStatus('importStatus', text, true); });
            });
    }

    // applyBulkEdit
    // Copies every filled bulk field into the selected rows, then saves each row in turn.
    function applyBulkEdit() {
        var rows = selectedRows();
        if (rows.length === 0) {
            Shared.setStatus('importStatus', 'Select the files to change.', true);
            return;
        }

        var bulk = {
            artist: _bulkArtistCombo ? _bulkArtistCombo.getValue() : '',
            feat: Shared.getEl('bulkFeat').value,
            albumArtist: Shared.getEl('bulkAlbumArtist').value,
            album: _bulkAlbumCombo ? _bulkAlbumCombo.getValue() : '',
            genre: normalizeGenre(Shared.getEl('bulkGenre').value),
            year: Shared.getEl('bulkYear').value
        };
        if (!bulk.artist && !bulk.feat && !bulk.albumArtist && !bulk.album && !bulk.genre && !bulk.year) {
            Shared.setStatus('importStatus', 'Enter a value to apply.', true);
            return;
        }

        rows.forEach(function (row) {
            if (bulk.artist) row.querySelector('.yta-artist-cell')._combo.setValue(bulk.artist);
            if (bulk.feat) row.querySelector('.yta-field-feat').value = bulk.feat;
            if (bulk.albumArtist) row.querySelector('.yta-field-albumArtist').value = bulk.albumArtist;
            if (bulk.album) row.querySelector('.yta-album-cell')._combo.setValue(bulk.album);
            if (bulk.genre) row.querySelector('.yta-field-genre').value = bulk.genre;
            if (bulk.year) row.querySelector('.yta-field-year').value = bulk.year;
        });

        var saved = 0;
        var errors = 0;
        function saveNext(index) {
            if (index >= rows.length) {
                var msg = 'Applied to ' + countLabel(saved, 'file', 'files') + '.';
                if (errors > 0) msg += ' Could not save ' + countLabel(errors, 'file', 'files') + '.';
                Shared.setStatus('importStatus', msg, errors > 0);
                return;
            }
            var row = rows[index];
            Shared.apiRequest('Tags', 'POST', readRowTags(row))
                .then(function () {
                    saved++;
                    flashSaved(row);
                })
                .catch(function () { errors++; })
                .then(function () { saveNext(index + 1); });
        }

        Shared.setStatus('importStatus', 'Applying…', false, { timeout: 0 });
        saveNext(0);
    }

    // importSelected
    // Imports the selected files into the library.
    //
    // Inputs
    // items [object[]]: The selected files.
    function importSelected(items) {
        var ids = items.map(function (d) { return d.Id; });
        if (!ids.length) return;
        Shared.setStatus('importStatus', 'Importing…', false, { timeout: 0 });
        Shared.apiRequest('Import', 'POST', { Ids: ids })
            .then(function (result) {
                var skipped = result && result.SkippedIds ? result.SkippedIds.length : 0;
                var errors = result && result.Errors ? result.Errors.length : 0;
                Shared.setStatus('importStatus', importResultMessage(result), errors > 0 || skipped > 0);
                _list.clearSelection();
                loadDownloads();
            })
            .catch(function (err) {
                Shared.errorText(err, 'Could not import the files.').then(function (text) { Shared.setStatus('importStatus', text, true); });
            });
    }

    // pickCover
    // Opens the file chooser, whose picture becomes the cover of the given files.
    //
    // Inputs
    // ids [string[]]: The files to set the cover on.
    function pickCover(ids) {
        var input = view.querySelector('#coverFileInput');
        if (!input || ids.length === 0) return;
        _coverTargets = ids;
        input.value = '';
        input.click();
    }

    // squareCover
    // Crops a picture to a centered square of at most 1200 px, so every cover is square whatever its shape.
    //
    // Inputs
    // file [File]: The chosen picture.
    //
    // Output
    // [Promise<string>]: The cover as base64 JPEG.
    function squareCover(file) {
        return new Promise(function(resolve, reject) {
            if (!/^image\/(jpeg|png|webp)$/.test(file.type)) {
                reject(new Error('Select a JPEG, PNG, or WebP picture.'));
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
                reject(new Error('Could not open that file as a picture.'));
            };
            img.src = url;
        });
    }

    // uploadCover
    // Embeds the chosen picture in each target file.
    //
    // Inputs
    // file [File]: The chosen picture.
    function uploadCover(file) {
        var ids = _coverTargets.slice();
        _coverTargets = [];
        squareCover(file).then(function(data) {
            embedCover(ids, data);
        }).catch(function(err) {
            Shared.setStatus('importStatus', err.message, true);
        });
    }

    // generateCoverFor
    // Draws a cover from the row's fields, with the text and colors set under Album Art in Settings, and embeds it.
    //
    // Inputs
    // row [HTMLElement]: The file's row.
    function generateCoverFor(row) {
        var albumCell = row.querySelector('.yta-album-cell');
        var artistCell = row.querySelector('.yta-artist-cell');
        var album = (albumCell && albumCell._combo ? albumCell._combo.getValue() : '').trim();
        var artist = (artistCell && artistCell._combo ? artistCell._combo.getValue() : '').trim();
        var albumArtist = (row.querySelector('.yta-field-albumArtist').value || '').trim() || artist;
        var year = (row.querySelector('.yta-field-year').value || '').trim();

        var settings = _coverSettings || {};
        var values = { Artist: albumArtist, Album: album, Year: year };
        var order = String(settings.GeneratedCoverTextOrder || 'Album,Artist,Year').split(',');
        var shown = String(settings.GeneratedCoverText == null ? 'Album' : settings.GeneratedCoverText).split(',');
        var lines = order.filter(function(k) { return shown.indexOf(k) !== -1 && values[k]; }).map(function(k) { return values[k]; });

        var colors = settings.GeneratedCoverColors || 'Album';
        var colorKey = colors === 'AlbumYear' ? album + '|' + year
            : colors === 'AlbumArtistYear' ? albumArtist + '|' + year
            : album;
        if (!colorKey.replace(/\|/g, '').trim()) {
            Shared.setStatus('importStatus', 'Enter an album name to generate its cover.', true);
            return;
        }

        var data = generateCover(lines, colorKey, 1200).toDataURL('image/jpeg', 0.92).split(',')[1];
        embedCover([row.getAttribute('data-id')], data);
    }

    // generateCover
    // Draws a square cover from three colors hashed from colorKey, blended softly, with the text lines centered on top. The same key always gives the same colors.
    //
    // Inputs
    // lines [string[]]: The text lines, top to bottom, which may be empty.
    // colorKey [string]: The text the colors are drawn from.
    // size [number]: The side length in pixels.
    //
    // Output
    // [HTMLCanvasElement]: The cover.
    function generateCover(lines, colorKey, size) {
        var hash = 2166136261;
        var key = String(colorKey || '').toLowerCase();
        for (var i = 0; i < key.length; i++) {
            hash ^= key.charCodeAt(i);
            hash = Math.imul(hash, 16777619) >>> 0;
        }
        function next() {
            hash ^= hash << 13; hash >>>= 0;
            hash ^= hash >>> 17;
            hash ^= hash << 5; hash >>>= 0;
            return hash / 4294967296;
        }
        var base = Math.floor(next() * 360);
        var colors = [0, 1, 2].map(function(n) {
            var hue = (base + n * (70 + Math.floor(next() * 50))) % 360;
            return 'hsl(' + hue + ',' + (55 + Math.floor(next() * 25)) + '%,' + (38 + Math.floor(next() * 20)) + '%)';
        });

        var canvas = document.createElement('canvas');
        canvas.width = canvas.height = size;
        var ctx = canvas.getContext('2d');
        ctx.fillStyle = colors[0];
        ctx.fillRect(0, 0, size, size);
        ctx.filter = 'blur(' + Math.round(size * 0.12) + 'px)';
        colors.forEach(function(color) {
            ctx.fillStyle = color;
            ctx.beginPath();
            ctx.arc(size * (0.15 + next() * 0.7), size * (0.15 + next() * 0.7), size * (0.35 + next() * 0.25), 0, Math.PI * 2);
            ctx.fill();
        });
        ctx.filter = 'none';
        if (!lines || lines.length === 0) return canvas;

        var maxWidth = size * 0.78;
        var maxHeight = size * 0.72;
        var scale = 1;
        var layout;
        function font(n) {
            return (n === 0 ? '700 ' : '500 ') + Math.round(size * 0.13 * scale * (n === 0 ? 1 : 0.5)) + 'px "Noto Sans", "Helvetica Neue", Arial, sans-serif';
        }
        function wrapAll() {
            return lines.map(function(text, n) {
                ctx.font = font(n);
                var out = [], line = '';
                String(text).split(/\s+/).forEach(function(word) {
                    var test = line ? line + ' ' + word : word;
                    if (ctx.measureText(test).width > maxWidth && line) { out.push(line); line = word; } else { line = test; }
                });
                out.push(line);
                return { n: n, rows: out, height: Math.round(size * 0.13 * scale * (n === 0 ? 1 : 0.5)) * 1.18 };
            });
        }
        while (true) {
            layout = wrapAll();
            var total = layout.reduce(function(sum, l) { return sum + l.rows.length * l.height; }, 0);
            var tooWide = layout.some(function(l) { ctx.font = font(l.n); return l.rows.some(function(r) { return ctx.measureText(r).width > maxWidth; }); });
            if ((total <= maxHeight && !tooWide) || scale < 0.3) break;
            scale *= 0.9;
        }

        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.shadowColor = 'rgba(0,0,0,0.35)';
        ctx.shadowBlur = size * 0.02;
        var height = layout.reduce(function(sum, l) { return sum + l.rows.length * l.height; }, 0);
        var y = (size - height) / 2;
        layout.forEach(function(l) {
            ctx.font = font(l.n);
            l.rows.forEach(function(r) {
                ctx.fillText(r, size / 2, y + l.height / 2);
                y += l.height;
            });
        });
        return canvas;
    }

    // takeAlbumCover
    // Copies the cover of a picked library album into this file, so the new song matches the rest of the album.
    //
    // Inputs
    // row [HTMLElement]: The file's row.
    // album [string]: The picked album.
    function takeAlbumCover(row, album) {
        var id = row.getAttribute('data-id');
        var artistCell = row.querySelector('.yta-artist-cell');
        var albumArtist = (row.querySelector('.yta-field-albumArtist').value || '').trim()
            || (artistCell && artistCell._combo ? artistCell._combo.getValue() : '');
        Shared.apiRequest('Downloads/' + encodeURIComponent(id) + '/Cover/FromAlbum', 'POST', { Album: album, AlbumArtist: albumArtist })
            .then(function(result) {
                if (!result || !result.Found) return;
                var btn = row.querySelector('.yta-cover-btn');
                if (btn) btn.innerHTML = renderCover(id, true);
                var d = _downloads.find(function(x) { return x.Id === id; });
                if (d) d.HasCover = true;
                Shared.setStatus('importStatus', 'Cover taken from the library album "' + album + '".', false);
            })
            .catch(function() { return null; });
    }

    // embedCover
    // Embeds a square cover in each target file in turn, and refreshes the thumbnails in place so unsaved edits stay.
    //
    // Inputs
    // ids [string[]]: The files.
    // data [string]: The cover as base64 JPEG.
    function embedCover(ids, data) {
        Shared.setStatus('importStatus', 'Setting covers…', false, { timeout: 0 });
        var failed = 0;
        function next(index) {
            if (index >= ids.length) {
                var done = ids.length - failed;
                Shared.setStatus('importStatus', 'Cover set on ' + countLabel(done, 'file', 'files') + '.' + (failed ? ' Could not set it on ' + countLabel(failed, 'file', 'files') + '.' : ''), failed > 0);
                return;
            }
            var id = ids[index];
            Shared.apiRequest('Downloads/' + encodeURIComponent(id) + '/Cover', 'POST', { Data: data })
                .then(function() {
                    var drawn = rowFor(id);
                    var btn = drawn && drawn.querySelector('.yta-cover-btn');
                    if (btn) btn.innerHTML = renderCover(id, true);
                    var d = _downloads.find(function(x) { return x.Id === id; });
                    if (d) d.HasCover = true;
                })
                .catch(function() { failed++; })
                .then(function() { next(index + 1); });
        }
        next(0);
    }

    // deleteSelected
    // Deletes the selected downloaded files, after a confirm.
    //
    // Inputs
    // items [object[]]: The selected files.
    function deleteSelected(items) {
        var ids = items.map(function (d) { return d.Id; });
        if (!ids.length) return;
        confirmDialog('Delete ' + countLabel(ids.length, 'downloaded file', 'downloaded files') + '? This cannot be undone.', {
            title: 'Delete Files',
            confirmText: 'Delete',
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.setStatus('importStatus', 'Deleting…', false, { timeout: 0 });
            Shared.apiRequest('Downloads/BatchDelete', 'POST', { Ids: ids })
                .then(function (result) {
                    var deleted = (result && result.Deleted) || 0;
                    var errors = (result && result.Errors) || 0;
                    var msg = 'Deleted ' + countLabel(deleted, 'file', 'files') + '.';
                    if (errors > 0) msg += ' Could not delete ' + countLabel(errors, 'file', 'files') + '.';
                    Shared.setStatus('importStatus', msg, errors > 0);
                    _list.clearSelection();
                })
                .catch(function (err) {
                    return Shared.errorText(err, 'Could not delete the files.').then(function (text) { Shared.setStatus('importStatus', text, true); });
                })
                .then(loadDownloads);
        });
    }

    // loadPage
    // Reloads the files and the cover settings each time the page shows.
    function loadPage() {
        loadDownloads();
        Shared.getConfig().then(function (config) { _coverSettings = config; }).catch(function () { return null; });
    }

    // bindEvents
    // Builds the list and the bulk editor, and wires the page's controls, once on the first viewshow.
    function bindEvents() {
        if (!_artistSearch) _artistSearch = Shared.createDebouncedSearch('Artists', 300);
        buildList();

        var bulkArtistCell = Shared.getEl('bulkArtistCell');
        if (bulkArtistCell && !_bulkArtistCombo) {
            _bulkArtistCombo = Shared.createSearchableComboBox({
                placeholder: 'Search artists',
                searchFn: _artistSearch
            });
            bulkArtistCell.appendChild(_bulkArtistCombo.element);
        }

        var bulkAlbumCell = Shared.getEl('bulkAlbumCell');
        if (bulkAlbumCell && !_bulkAlbumCombo) {
            _bulkAlbumCombo = Shared.createSearchableComboBox({
                placeholder: 'Search albums',
                searchFn: createAlbumSearchForArtist(function () { return _bulkArtistCombo ? _bulkArtistCombo.getValue() : ''; })
            });
            bulkAlbumCell.appendChild(_bulkAlbumCombo.element);
        }

        Shared.bindClick('btnApplyBulk', applyBulkEdit);
        Shared.bindClick('btnBulkCover', function () { pickCover(_list ? _list.getSelected() : []); });

        var coverInput = Shared.getEl('coverFileInput');
        if (coverInput) coverInput.addEventListener('change', function () {
            if (coverInput.files && coverInput.files[0]) uploadCover(coverInput.files[0]);
        });
    }

    var page = createPage(view, {
        tabIndex: 1,
        bind: bindEvents,
        load: loadPage,
        onHide: function () {
            stopPreview();
        }
    });
    Shared = page.shared;
}
