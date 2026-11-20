// youtubeaudio_import.js
// Drives the Import tab, where downloaded songs are tagged, given covers, and imported into the library.

import { countText, createPage, createAudioPlayer, createFilteredList, createImagePicker, confirmDialog } from './configurationpage?name=youtubeaudio_shared.js';

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

// importResultMessage
// Says what an import did.
//
// Inputs
// t [object]: The translations handle.
// result [object]: The server's ImportResult.
//
// Output
// [string]: The status message.
function importResultMessage(t, result) {
    result = result || {};
    var imported = result.ImportedIds ? result.ImportedIds.length : 0;
    var replaced = result.ReplacedIds ? result.ReplacedIds.length : 0;
    var skipped = result.SkippedIds ? result.SkippedIds.length : 0;
    var errors = result.Errors ? result.Errors.length : 0;
    var parts = [];
    if (imported - replaced > 0) parts.push(countText(t, imported - replaced, 'MessageImportedOne', 'MessageImportedMany'));
    if (replaced > 0) parts.push(countText(t, replaced, 'MessageReplacedOne', 'MessageReplacedMany'));
    if (skipped > 0) parts.push(countText(t, skipped, 'MessageSkippedDuplicatesOne', 'MessageSkippedDuplicatesMany'));
    if (result.RemovedFiles > 0) parts.push(countText(t, result.RemovedFiles, 'MessageSinglesReplacedOne', 'MessageSinglesReplacedMany'));
    if (errors > 0) parts.push(countText(t, errors, 'ErrorImportFilesOne', 'ErrorImportFilesMany'));
    return parts.length ? parts.join(' ') : t.tr('MessageNoFilesImported');
}

export default function (view) {
    'use strict';

    var Shared = null;
    var t = null;

    var _downloads = [];
    var _list = null;
    var _artistSearch = null;
    var _bulkArtistCombo = null;
    var _bulkAlbumCombo = null;
    var _bulkCover = null;
    var _coverSettings = null;
    var _rowParts = {};

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

    // destroyRowParts
    // Destroys the Base controls of one row, or of every row, which pauses its player.
    //
    // Inputs
    // id [string]: The row, or nothing for every row.
    function destroyRowParts(id) {
        Object.keys(_rowParts).forEach(function (key) {
            if (id && key !== id) return;
            _rowParts[key].forEach(function (part) { part.destroy(); });
            delete _rowParts[key];
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
    // Builds the files as a Base Filtered List with selection, Import and Delete, drawn as one panel per file.
    function buildList() {
        var host = Shared.getEl('importList');
        if (!host || _list) return;
        _list = createFilteredList({
            items: [],
            rowKey: 'Id',
            render: renderImportRow,
            search: false,
            selectable: 'multiple',
            layout: 'plain',
            bulkActions: [
                { id: 'import', label: t.tr('Import'), icon: 'library_add', variant: 'primary', onClick: importSelected },
                { id: 'delete', label: t.tr('Delete'), icon: 'delete', variant: 'destructive', onClick: deleteSelected }
            ],
            actions: [{ id: 'refresh', label: t.tr('Refresh'), icon: 'refresh', onClick: loadDownloads }],
            emptyText: t.tr('MessageNoDownloads'),
            label: t.tr('LabelDownloadedFiles'),
            onSelectionChange: updateBulkButtons
        });
        host.appendChild(_list.element);
    }

    // renderImportList
    // Hands the files to the list, and drops the controls of files that are gone.
    function renderImportList() {
        var ids = _downloads.map(function (d) { return d.Id; });
        Object.keys(_rowParts).forEach(function (id) { if (ids.indexOf(id) === -1) destroyRowParts(id); });
        if (_list) _list.setItems(_downloads);
        updateBulkButtons();
    }

    // fileUrl
    // Returns the address of a downloaded file's audio or cover. The token rides in the query, since audio and images cannot send headers.
    //
    // Inputs
    // id [string]: The file's queue item id.
    // part [string]: Audio or Cover.
    //
    // Output
    // [string]: The address.
    function fileUrl(id, part) {
        var query = { ApiKey: ApiClient.accessToken() };
        if (part === 'Cover') query.v = Date.now();
        return ApiClient.getUrl('YouTubeAudio/Downloads/' + encodeURIComponent(id) + '/' + part, query);
    }

    // showCover
    // Shows a file's new cover in its row's picker, and remembers that the file has one.
    //
    // Inputs
    // id [string]: The file's queue item id.
    function showCover(id) {
        var row = rowFor(id);
        if (row && row._cover) row._cover.setValue(fileUrl(id, 'Cover'));
        var d = _downloads.find(function (x) { return x.Id === id; });
        if (d) d.HasCover = true;
    }

    // coverData
    // Returns a picked cover as base64 JPEG or PNG, the types the server embeds. Any other type is drawn again as JPEG.
    //
    // Inputs
    // value [object]: The Image Picker's value.
    //
    // Output
    // [Promise<string>]: The base64 data.
    function coverData(value) {
        if (/^image\/(jpeg|png)$/.test(value.type)) return Promise.resolve(value.base64);
        return new Promise(function (resolve, reject) {
            var img = new Image();
            img.onload = function () {
                var canvas = document.createElement('canvas');
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
                canvas.getContext('2d').drawImage(img, 0, 0);
                resolve(canvas.toDataURL('image/jpeg', 0.9).split(',')[1]);
            };
            img.onerror = function () { reject(new Error(t.tr('ErrorOpenPicture'))); };
            img.src = value.dataUrl;
        });
    }

    // createCoverPicker
    // Builds a square Base Image Picker that crops a picked picture to its center at most 1200 px.
    //
    // Inputs
    // opts [object]: Extra Image Picker options, such as value and onChange.
    //
    // Output
    // [object]: The picker handle.
    function createCoverPicker(opts) {
        return createImagePicker(Object.assign({
            emptyText: t.tr('MessageNoCover'),
            shape: 'square',
            crop: true,
            maxSize: 1200,
            accept: 'image/jpeg,image/png,image/webp',
            maxBytes: 10485760,
            wrongTypeText: t.tr('ErrorPictureType')
        }, opts));
    }

    // renderImportRow
    // Draws one file as a panel, with its name, size, and save controls on top, its cover beside its tag fields, and its preview player under them, and wires them.
    //
    // Inputs
    // d [object]: The downloaded file.
    // index [number]: Its place in the list.
    // check [HTMLInputElement]: The row's selection checkbox, placed at the start of the header.
    //
    // Output
    // [HTMLElement]: The row.
    function renderImportRow(d, index, check) {
        var esc = Shared.escapeHtml;
        var key = 'yta-' + String(d.Id).replace(/[^A-Za-z0-9_-]/g, '');
        function field(name, area, label, type, value, placeholder) {
            return '<div class="base-field yta-import-' + area + '"><label for="' + key + '-' + name + '">' + label + '</label>'
                + '<input type="' + type + '" id="' + key + '-' + name + '" class="base-edit-input yta-field-' + name + '" value="' + esc(value) + '" placeholder="' + placeholder + '" /></div>';
        }
        var generate = esc(t.tr('ButtonGenerateCover'));
        var saveTags = esc(t.tr('ButtonSaveTags'));
        var artistText = esc(t.tr('Artist'));
        var row = document.createElement('div');
        row.className = 'base-panel yta-import-row';
        row.setAttribute('data-id', d.Id);
        row.innerHTML =
            '<div class="base-panel-header yta-import-head">'
            + '<span class="yta-import-name"></span>'
            + '<div class="yta-import-tools">'
            + '<span class="base-edit-secondary">' + esc(Shared.formatSize(d.FileSize)) + '</span>'
            + (d.InLibrary ? '<span title="' + esc(t.tr('InLibraryHelp')) + '">' + Shared.badge('orange', t.tr('LabelInLibrary')) + '</span>' : '')
            + '<button type="button" class="base-row-btn yta-wand-btn" title="' + generate + '" aria-label="' + generate + '"><span class="material-icons" aria-hidden="true">auto_fix_high</span></button>'
            + '<button type="button" class="base-row-btn save" title="' + saveTags + '" aria-label="' + saveTags + '"><span class="material-icons" aria-hidden="true">save</span></button>'
            + '</div>'
            + '</div>'
            + '<div class="base-panel-body yta-import-body">'
            + '<div class="yta-cover-cell"></div>'
            + '<div class="yta-import-fields">'
            + field('title', 'title', esc(t.tr('LabelTitle')), 'text', d.MetadataTitle, esc(t.tr('LabelTitle')))
            + field('trackNumber', 'track', esc(t.tr('Track')), 'number', d.TrackNumber || '', '1')
            + '<div class="base-field yta-import-artist yta-artist-cell"><label>' + artistText + '</label></div>'
            + field('albumArtist', 'album-artist', esc(t.tr('AlbumArtist')), 'text', d.AlbumArtist, artistText)
            + field('feat', 'feat', esc(t.tr('LabelFeaturedArtists')), 'text', d.FeaturedArtist, esc(t.tr('PlaceholderArtists')))
            + '<div class="base-field yta-import-album yta-album-cell"><label>' + esc(t.tr('Album')) + '</label></div>'
            + field('genre', 'genre', esc(t.tr('Genre')), 'text', d.Genre, esc(t.tr('PlaceholderGenres')))
            + field('year', 'year', esc(t.tr('LabelYear')), 'number', d.Year || '', '2024')
            + '</div>'
            + '<div class="yta-player-cell"></div>'
            + '</div>';

        var name = row.querySelector('.yta-import-name');
        var titleInput = row.querySelector('.yta-field-title');
        function showName() { name.textContent = titleInput.value.trim() || d.FileName || ''; }
        showName();
        titleInput.addEventListener('input', showName);

        var head = row.querySelector('.yta-import-head');
        if (check) head.insertBefore(check, head.firstChild);

        var saveBtn = row.querySelector('.base-row-btn.save');
        function markDirty() { saveBtn.classList.add('dirty'); }

        destroyRowParts(d.Id);

        var player = createAudioPlayer({
            src: fileUrl(d.Id, 'Audio'),
            size: 'small',
            label: t.tr('Preview'),
            errorText: t.tr('ErrorPlayFile')
        });
        row.querySelector('.yta-player-cell').appendChild(player.element);

        var cover = createCoverPicker({
            value: d.HasCover ? fileUrl(d.Id, 'Cover') : null,
            chooseText: t.tr('ButtonSetCover'),
            // An embedded cover is only replaced, so the picker offers no Remove.
            removable: false,
            onChange: function (value) {
                if (value) embedCover([d.Id], value);
            }
        });
        row.querySelector('.yta-cover-cell').appendChild(cover.element);
        row._cover = cover;

        var artistCell = row.querySelector('.yta-artist-cell');
        var artistCombo = Shared.createSearchableComboBox({
            placeholder: t.tr('PlaceholderSearchArtists'),
            searchFn: _artistSearch,
            onInput: markDirty
        });
        artistCombo.setValue(d.Artist || '');
        artistCell.appendChild(artistCombo.element);
        artistCell._combo = artistCombo;

        var albumCell = row.querySelector('.yta-album-cell');
        var albumCombo = Shared.createSearchableComboBox({
            placeholder: t.tr('PlaceholderSearchAlbums'),
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
        _rowParts[d.Id] = [player, cover, artistCombo, albumCombo];

        saveBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            saveRowTags(row);
        });
        row.querySelectorAll('.base-edit-input').forEach(function (input) {
            input.addEventListener('input', markDirty);
        });
        row.querySelector('.yta-wand-btn').addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            generateCoverFor(row);
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
        if (coverBtn) coverBtn.disabled = none || !(_bulkCover && _bulkCover.getValue());
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
                Shared.errorText(err, t.tr('ErrorSaveTags')).then(function (text) { Shared.setStatus('importStatus', text, true); });
            });
    }

    // applyBulkEdit
    // Copies every filled bulk field into the selected rows, then saves each row in turn.
    function applyBulkEdit() {
        var rows = selectedRows();
        if (rows.length === 0) {
            Shared.setStatus('importStatus', t.tr('MessageSelectFiles'), true);
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
            Shared.setStatus('importStatus', t.tr('MessageEnterValue'), true);
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
                var msg = countText(t, saved, 'MessageAppliedOne', 'MessageAppliedMany');
                if (errors > 0) msg += ' ' + countText(t, errors, 'ErrorSaveFilesOne', 'ErrorSaveFilesMany');
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

        Shared.setStatus('importStatus', t.tr('MessageApplying'), false, { timeout: 0 });
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
        Shared.setStatus('importStatus', t.tr('MessageImporting'), false, { timeout: 0 });
        Shared.apiRequest('Import', 'POST', { Ids: ids })
            .then(function (result) {
                var skipped = result && result.SkippedIds ? result.SkippedIds.length : 0;
                var errors = result && result.Errors ? result.Errors.length : 0;
                Shared.setStatus('importStatus', importResultMessage(t, result), errors > 0 || skipped > 0);
                _list.clearSelection();
                loadDownloads();
            })
            .catch(function (err) {
                Shared.errorText(err, t.tr('ErrorImportFiles')).then(function (text) { Shared.setStatus('importStatus', text, true); });
            });
    }

    // setBulkCover
    // Embeds the cover picked in Bulk Updates in every selected file.
    function setBulkCover() {
        var value = _bulkCover ? _bulkCover.getValue() : null;
        var ids = _list ? _list.getSelected() : [];
        if (!value || typeof value === 'string' || ids.length === 0) return;
        embedCover(ids, value);
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
            Shared.setStatus('importStatus', t.tr('MessageEnterAlbumForCover'), true);
            return;
        }

        var data = generateCover(lines, colorKey, 1200).toDataURL('image/jpeg', 0.92).split(',')[1];
        sendCover([row.getAttribute('data-id')], data);
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
                showCover(id);
                Shared.setStatus('importStatus', t.format('MessageCoverFromAlbum', album), false);
            })
            .catch(function() { return null; });
    }

    // embedCover
    // Embeds a picked cover in each target file.
    //
    // Inputs
    // ids [string[]]: The files.
    // value [object]: The Image Picker's value.
    function embedCover(ids, value) {
        coverData(value)
            .then(function (data) { sendCover(ids, data); })
            .catch(function (err) { Shared.setStatus('importStatus', err.message, true); });
    }

    // sendCover
    // Embeds a square cover in each target file in turn, and shows it in each row so unsaved edits stay.
    //
    // Inputs
    // ids [string[]]: The files.
    // data [string]: The cover as base64 JPEG or PNG.
    function sendCover(ids, data) {
        Shared.setStatus('importStatus', t.tr('MessageSettingCovers'), false, { timeout: 0 });
        var failed = 0;
        function next(index) {
            if (index >= ids.length) {
                var done = ids.length - failed;
                Shared.setStatus('importStatus', countText(t, done, 'MessageCoverSetOne', 'MessageCoverSetMany') + (failed ? ' ' + countText(t, failed, 'ErrorSetCoverOne', 'ErrorSetCoverMany') : ''), failed > 0);
                return;
            }
            var id = ids[index];
            Shared.apiRequest('Downloads/' + encodeURIComponent(id) + '/Cover', 'POST', { Data: data })
                .then(function () { showCover(id); })
                .catch(function () { failed++; })
                .then(function () { next(index + 1); });
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
        confirmDialog(countText(t, ids.length, 'ConfirmDeleteFilesOne', 'ConfirmDeleteFilesMany'), {
            title: t.tr('HeaderDeleteFiles'),
            confirmText: t.tr('Delete'),
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.setStatus('importStatus', t.tr('MessageDeleting'), false, { timeout: 0 });
            Shared.apiRequest('Downloads/BatchDelete', 'POST', { Ids: ids })
                .then(function (result) {
                    var deleted = (result && result.Deleted) || 0;
                    var errors = (result && result.Errors) || 0;
                    var msg = countText(t, deleted, 'MessageDeletedFilesOne', 'MessageDeletedFilesMany');
                    if (errors > 0) msg += ' ' + countText(t, errors, 'ErrorDeleteFilesOne', 'ErrorDeleteFilesMany');
                    Shared.setStatus('importStatus', msg, errors > 0);
                    _list.clearSelection();
                })
                .catch(function (err) {
                    return Shared.errorText(err, t.tr('ErrorDeleteFiles')).then(function (text) { Shared.setStatus('importStatus', text, true); });
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
        t = Shared.t;
        if (!_artistSearch) _artistSearch = Shared.createDebouncedSearch('Artists', 300);
        buildList();

        var bulkArtistCell = Shared.getEl('bulkArtistCell');
        if (bulkArtistCell && !_bulkArtistCombo) {
            _bulkArtistCombo = Shared.createSearchableComboBox({
                placeholder: t.tr('PlaceholderSearchArtists'),
                searchFn: _artistSearch
            });
            bulkArtistCell.appendChild(_bulkArtistCombo.element);
        }

        var bulkAlbumCell = Shared.getEl('bulkAlbumCell');
        if (bulkAlbumCell && !_bulkAlbumCombo) {
            _bulkAlbumCombo = Shared.createSearchableComboBox({
                placeholder: t.tr('PlaceholderSearchAlbums'),
                searchFn: createAlbumSearchForArtist(function () { return _bulkArtistCombo ? _bulkArtistCombo.getValue() : ''; })
            });
            bulkAlbumCell.appendChild(_bulkAlbumCombo.element);
        }

        Shared.bindClick('btnApplyBulk', applyBulkEdit);
        Shared.bindClick('btnBulkCover', setBulkCover);

        var bulkCoverCell = Shared.getEl('bulkCoverCell');
        if (bulkCoverCell && !_bulkCover) {
            _bulkCover = createCoverPicker({ label: t.tr('LabelCover'), onChange: updateBulkButtons });
            bulkCoverCell.appendChild(_bulkCover.element);
        }
    }

    var page = createPage(view, {
        tabIndex: 1,
        bind: bindEvents,
        load: loadPage,
        onHide: function () {
            Object.keys(_rowParts).forEach(function (id) { _rowParts[id][0].pause(); });
        }
    });
    Shared = page.shared;
}
