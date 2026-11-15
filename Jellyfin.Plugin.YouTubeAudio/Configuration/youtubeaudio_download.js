// youtubeaudio_download.js
// Drives the Download tab, where songs are queued from each source and the queue is downloaded.

import {
    STATUS_STATES,
    createPage,
    createChoiceGroup,
    createMediaCard,
    createCheckboxList,
    createFilePicker,
    createFilteredList,
    confirmDialog,
    promptDialog
} from './configurationpage?name=youtubeaudio_shared.js';

// QUEUE_STATES
// The queue statuses the Download tab shows, as count card filters colored by meaning.
var QUEUE_STATES = [
    { value: '0', label: 'Queued', color: 'orange' },
    { value: '1', label: 'Downloading', color: 'purple' },
    { value: '2', label: 'Downloaded', color: 'green' },
    { value: '4', label: 'Error', color: 'red' }
];

// countLabel
// Names a count with the right plural.
//
// Inputs
// count [number]: How many.
// one [string]: The word for one.
// many [string]: The word for several.
//
// Output
// [string]: Such as "1 song" or "3 songs".
function countLabel(count, one, many) {
    return count + ' ' + (count === 1 ? one : many);
}

// visibleQueueItems
// Leaves out imported items, which belong to the Import tab.
//
// Inputs
// items [object[]]: Every queue item.
//
// Output
// [object[]]: The items the Download tab shows.
function visibleQueueItems(items) {
    return (items || []).filter(function (item) { return item.StatusCode !== 3; });
}

// playlistResultMessage
// Says what a playlist upload queued and what it left out.
//
// Inputs
// result [object]: The server's PlaylistImportResult.
//
// Output
// [string]: The status message.
function playlistResultMessage(result) {
    result = result || {};
    var count = result.Items ? result.Items.length : 0;
    var msg = 'Added ' + countLabel(count, 'track', 'tracks') + ' from ' + (result.Source || 'the playlist');
    if (result.PlaylistName) msg += ' (' + result.PlaylistName + ')';
    msg += '.';
    var left = [];
    if (result.AlreadyInLibrary) left.push(result.AlreadyInLibrary + ' already in the library');
    if (result.AlreadyQueued) left.push(result.AlreadyQueued + ' already queued');
    if (result.DuplicateTracks) left.push(countLabel(result.DuplicateTracks, 'duplicate or lesser version', 'duplicates or lesser versions'));
    if (left.length) msg += ' Left out ' + left.join(', ') + '.';
    if (result.LibraryUpgrades) msg += ' ' + countLabel(result.LibraryUpgrades, 'song replaces', 'songs replace') + ' a library single with the album version.';
    if (result.PlaylistsTracked) msg += ' ' + countLabel(result.PlaylistsTracked, 'playlist fills', 'playlists fill') + ' in as songs reach the library.';
    if (result.SkippedRows) msg += ' Skipped ' + countLabel(result.SkippedRows, 'entry', 'entries') + ' with no title.';
    return msg;
}

export default function (view) {
    'use strict';

    var Shared = null;

    var _allItems = [];
    var _sourceNames = { replace: 'Replacement' };
    var _pendingUpload = null;
    var _ownerSelector = null;
    var _filePicker = null;
    var _playlistChoices = null;
    var _queueList = null;
    var _targetList = null;
    var _replaceList = null;
    var _poller = null;
    var _processingGraceUntil = 0;
    var _replaceSongId = null;

    // reportError
    // Shows the server's reason, or a fallback, on the queue status line.
    //
    // Inputs
    // err [object]: The failed request.
    // fallback [string]: The message when the server gave no reason.
    function reportError(err, fallback) {
        Shared.errorText(err, fallback).then(function (text) { Shared.setStatus('queueStatus', text, true); });
    }

    // isPolling
    // Says whether downloads run now, or a batch started within the grace window while yt-dlp is still being set up.
    //
    // Output
    // [boolean]: Whether the queue should poll.
    function isPolling() {
        return _allItems.some(function (i) { return i.StatusCode === 1; }) || Date.now() < _processingGraceUntil;
    }

    // loadQueue
    // Reads the queue and redraws it, keeping the selection.
    //
    // Output
    // [Promise]: Settles once the queue is drawn.
    function loadQueue() {
        return Shared.apiRequest('Queue', 'GET')
            .then(function (response) { _allItems = (response && response.Items) || response || []; })
            .catch(function () { _allItems = []; })
            .then(renderQueue);
    }

    // renderQueue
    // Hands the queue to the list, and starts or stops polling.
    function renderQueue() {
        if (_queueList) _queueList.setItems(visibleQueueItems(_allItems));
        if (_poller) {
            if (isPolling()) _poller.start(false);
            else _poller.stop();
        }
    }

    // renderQueueRow
    // Draws one queue item as a Base media card with its status, its notes, and a Change Link action.
    //
    // Inputs
    // item [object]: The queue item.
    //
    // Output
    // [object]: The media card handle.
    function renderQueueRow(item) {
        var title = item.Title || item.Url;
        var sub = item.Url;

        // Playlist tracks show their artist, and have no URL until their YouTube match is found.
        if (item.Source) {
            if (item.Artist) title = item.Artist + ' – ' + title;
            var sourceName = _sourceNames[item.Source] || item.Source;
            sub = sourceName + ' · ' + (item.Url || 'YouTube match is found when downloaded');
        }

        var failed = item.StatusCode === 4;
        var notes = [];
        if (item.MatchNote && !failed) notes.push(item.MatchNote);
        if (item.ReplacesFileCount > 0) notes.push('Replaces the single in the library when imported.');

        return createMediaCard({
            shape: 'square',
            icon: 'music_note',
            title: title,
            subtitle: sub,
            badges: item.MatchNote && !failed ? [{ label: 'Check Match', color: 'orange' }] : [],
            status: { label: item.Status, color: STATUS_STATES[item.StatusCode] || 'Queued' },
            detail: notes.join(' ') || null,
            error: failed ? (item.ErrorMessage || null) : null,
            actions: item.StatusCode !== 1 ? [{ label: 'Change Link', icon: 'link', value: 'link' }] : [],
            onAction: function () { changeLink(item.Id); }
        });
    }

    // buildQueueList
    // Builds the queue as a Base Filtered List with status cards, a search, selection, and bulk actions.
    function buildQueueList() {
        var host = Shared.getEl('queueList');
        if (!host || _queueList) return;
        _queueList = createFilteredList({
            items: [],
            rowKey: 'Id',
            render: renderQueueRow,
            search: { keys: ['Title', 'Artist', 'Url'], placeholder: 'Search queue', label: 'Search queue' },
            filters: [{
                name: 'Status',
                label: 'Statuses',
                kind: 'cards',
                options: QUEUE_STATES,
                test: function (item, values) { return values.indexOf(String(item.StatusCode)) !== -1; }
            }],
            selectable: 'multiple',
            bulkActions: [
                { id: 'download', label: 'Download', icon: 'download', variant: 'primary', onClick: processQueue },
                { id: 'delete', label: 'Delete', icon: 'delete', variant: 'destructive', onClick: deleteSelectedQueue }
            ],
            actions: [
                { id: 'cancel', label: 'Cancel Downloads', icon: 'stop', variant: 'destructive', onClick: cancelDownloads, visible: isPolling },
                { id: 'refresh', label: 'Refresh', icon: 'refresh', onClick: loadQueue }
            ],
            emptyText: 'No songs queued yet. Add one above.',
            label: 'Queue'
        });
        host.appendChild(_queueList.element);
    }

    // changeLink
    // Asks for a new YouTube link for a queue item, then downloads from it with the item's details kept.
    //
    // Inputs
    // id [string]: The queue item.
    function changeLink(id) {
        promptDialog([{
            name: 'url',
            label: 'YouTube URL',
            placeholder: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
            description: 'The upload to download instead. The song keeps its details.',
            required: true
        }], { title: 'Change Link', submitText: 'Use Link' }).then(function (values) {
            if (!values) return;
            Shared.apiRequest('Queue/' + encodeURIComponent(id) + '/Url', 'POST', { Url: (values.url || '').trim() })
                .then(function () {
                    _processingGraceUntil = Date.now() + 120000;
                    Shared.setStatus('queueStatus', 'Link changed. Downloading from the new link.', false);
                    loadQueue();
                })
                .catch(function (err) { reportError(err, 'Could not use that link.'); });
        });
    }

    // deleteSelectedQueue
    // Removes the selected items from the queue, after a confirm.
    //
    // Inputs
    // items [object[]]: The selected queue items.
    function deleteSelectedQueue(items) {
        var ids = items.map(function (i) { return i.Id; });
        if (!ids.length) return;
        var things = countLabel(ids.length, 'item', 'items');
        confirmDialog('Delete ' + things + ' from the queue? This cannot be undone.', {
            title: 'Delete Items',
            confirmText: 'Delete',
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.setStatus('queueStatus', 'Deleting…', false, { timeout: 0 });
            Shared.apiRequest('Queue/BatchDelete', 'POST', { Ids: ids })
                .then(function (result) {
                    var deleted = (result && result.Deleted) || 0;
                    var errors = (result && result.Errors) || 0;
                    var msg = 'Deleted ' + countLabel(deleted, 'item', 'items') + '.';
                    if (errors > 0) msg += ' Could not delete ' + countLabel(errors, 'item', 'items') + '.';
                    Shared.setStatus('queueStatus', msg, errors > 0);
                    _queueList.clearSelection();
                })
                .catch(function (err) { reportError(err, 'Could not delete the items.'); })
                .then(loadQueue);
        });
    }

    // processQueue
    // Starts downloading the selected items in the background.
    //
    // Inputs
    // items [object[]]: The selected queue items.
    function processQueue(items) {
        var ids = items.map(function (i) { return i.Id; });
        if (!ids.length) return;
        Shared.setStatus('queueStatus', 'Starting…', false, { timeout: 0 });
        Shared.apiRequest('Queue/Process', 'POST', { Ids: ids })
            .then(function () {
                _processingGraceUntil = Date.now() + 120000;
                Shared.setStatus('queueStatus', 'Download started.', false);
                _queueList.clearSelection();
            })
            .catch(function (err) { reportError(err, 'Could not start the download.'); })
            .then(loadQueue);
    }

    // cancelDownloads
    // Stops the running batch, after a confirm. Songs not finished stay queued.
    function cancelDownloads() {
        confirmDialog('Stop the running downloads? Songs that have not finished stay in the queue.', {
            title: 'Cancel Downloads',
            confirmText: 'Stop',
            destructive: true
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('Queue/Cancel', 'POST')
                .then(function (result) {
                    _processingGraceUntil = 0;
                    Shared.setStatus('queueStatus', result && result.Cancelled ? 'Downloads cancelled.' : 'No downloads to cancel.', false);
                    loadQueue();
                })
                .catch(function (err) { reportError(err, 'Could not cancel the downloads.'); });
        });
    }

    // queueUrl
    // Queues the YouTube link in the field.
    function queueUrl() {
        var input = Shared.getEl('txtYouTubeUrl');
        var url = ((input || {}).value || '').trim();
        if (!url) {
            Shared.setStatus('queueStatus', 'Enter a YouTube URL.', true);
            return;
        }

        Shared.apiRequest('Queue', 'POST', { Url: url })
            .then(function (items) {
                Shared.setStatus('queueStatus', 'Added ' + countLabel(items ? items.length : 0, 'item', 'items') + ' to the queue.', false);
                input.value = '';
                var qBtn = Shared.getEl('btnQueue');
                if (qBtn) qBtn.disabled = true;
                loadQueue();
            })
            .catch(function (err) { reportError(err, 'Could not queue the link.'); });
    }

    // loadPlaylistSources
    // Reads the playlist export formats, so queue rows can name the service each song came from.
    function loadPlaylistSources() {
        Shared.apiRequest('Playlists/Sources', 'GET')
            .then(function (sources) {
                (sources || []).forEach(function (source) { _sourceNames[source.Id] = source.Name; });
                if (_queueList) _queueList.redraw();
            })
            .catch(function () { return null; });
    }

    // buildReplaceList
    // Builds the library song search as a Base Filtered List that picks one song.
    function buildReplaceList() {
        var host = Shared.getEl('replaceResults');
        if (!host || _replaceList) return;
        _replaceList = createFilteredList({
            fetch: function (query) {
                var text = (query.search || '').trim();
                if (!text) return [];
                return Shared.apiRequest('Songs?query=' + encodeURIComponent(text), 'GET').then(function (songs) { return songs || []; });
            },
            immediate: false,
            rowKey: 'Id',
            render: function (song) {
                return createMediaCard({
                    shape: 'square',
                    icon: 'music_note',
                    itemId: song.Id,
                    title: song.Title,
                    subtitle: [song.Artist, song.Album].filter(Boolean).join(' · ')
                });
            },
            search: { placeholder: 'Search songs', label: 'Library Song' },
            selectable: 'single',
            summary: false,
            maxHeight: 240,
            emptyText: 'No library songs match.',
            onSelectionChange: function (ids) {
                _replaceSongId = ids.length ? ids[0] : null;
                updateReplaceButton();
            }
        });
        host.appendChild(_replaceList.element);
    }

    // updateReplaceButton
    // Turns on Queue Replacement once a song is picked and a link is entered.
    function updateReplaceButton() {
        var btn = Shared.getEl('btnReplace');
        var url = Shared.getEl('txtReplaceUrl');
        if (btn) btn.disabled = !(_replaceSongId && url && url.value.trim());
    }

    // queueReplacement
    // Queues the link to replace the picked library song, and starts its download.
    function queueReplacement() {
        var url = Shared.getEl('txtReplaceUrl');
        if (!_replaceSongId || !url || !url.value.trim()) return;

        var btn = Shared.getEl('btnReplace');
        if (btn) btn.disabled = true;
        Shared.apiRequest('Queue/Replace', 'POST', { ItemId: _replaceSongId, Url: url.value.trim() })
            .then(function (item) {
                Shared.setStatus('queueStatus', 'Downloading the replacement for "' + ((item && item.Title) || 'the song') + '". Import it on the Import tab.', false);
                url.value = '';
                _replaceList.clearSelection();
                _replaceList.setSearch('');
                loadQueue();
            })
            .catch(function (err) {
                reportError(err, 'Could not queue the replacement.');
                updateReplaceButton();
            });
    }

    // readFileAsBase64
    // Reads a file's raw bytes as base64, so the server detects its text encoding rather than the browser.
    //
    // Inputs
    // file [File]: The chosen export.
    //
    // Output
    // [Promise<string>]: The base64 content.
    function readFileAsBase64(file) {
        return new Promise(function (resolve, reject) {
            var reader = new FileReader();
            reader.onload = function () {
                var result = String(reader.result || '');
                resolve(result.substring(result.indexOf(',') + 1));
            };
            reader.onerror = function () { reject(reader.error); };
            reader.readAsDataURL(file);
        });
    }

    // chosenFile
    // Returns the export file the picker holds.
    //
    // Output
    // [File]: The file, or null.
    function chosenFile() {
        var files = _filePicker ? _filePicker.getValue() : [];
        return files.length ? files[0] : null;
    }

    // resetPlaylistImport
    // Forgets the pending upload and hides the playlist picker.
    function resetPlaylistImport() {
        _pendingUpload = null;
        if (_playlistChoices) {
            _playlistChoices.destroy();
            _playlistChoices = null;
        }
        var choices = Shared.getEl('playlistChoices');
        if (choices) {
            choices.hidden = true;
            choices.innerHTML = '';
        }
        var btn = Shared.getEl('btnImportPlaylist');
        if (btn) {
            btn.querySelector('span').textContent = 'Queue Playlist';
            btn.disabled = !chosenFile();
        }
    }

    // buildFilePicker
    // Builds the export picker for the chosen service's file types.
    //
    // Inputs
    // source [string]: applemusic or spotify.
    function buildFilePicker(source) {
        var host = Shared.getEl('playlistFile');
        if (!host) return;
        var accept = source === 'spotify' ? '.csv,.zip' : '.txt,.xml';
        if (_filePicker && _filePicker.input.getAttribute('accept') === accept) return;
        if (_filePicker) _filePicker.destroy();
        host.innerHTML = '';
        _filePicker = createFilePicker({ accept: accept, onChange: resetPlaylistImport });
        host.appendChild(_filePicker.element);
        resetPlaylistImport();
    }

    // renderPlaylistChoices
    // Shows a checkbox per playlist when the export holds several, all checked.
    //
    // Inputs
    // playlists [object[]]: The PlaylistChoice list from the server.
    function renderPlaylistChoices(playlists) {
        var host = Shared.getEl('playlistChoices');
        if (!host) return;
        var btn = Shared.getEl('btnImportPlaylist');
        host.innerHTML = '';
        var intro = document.createElement('div');
        intro.className = 'fieldDescription';
        intro.textContent = 'This file holds ' + playlists.length + ' playlists. Turn off any you do not want.';
        host.appendChild(intro);
        _playlistChoices = createCheckboxList({
            items: playlists,
            idKey: 'Id',
            value: playlists.map(function (p) { return p.Id; }),
            search: false,
            showSelectAll: true,
            renderRow: function (p) {
                return { label: p.Name, sublabel: countLabel(p.TrackCount, 'track', 'tracks'), note: p.IsLibrary ? 'Songs Only' : '' };
            },
            onChange: function (ids) { if (btn) btn.disabled = !ids.length; }
        });
        host.appendChild(_playlistChoices.element);
        host.hidden = false;
        if (btn) {
            btn.querySelector('span').textContent = 'Queue Selected';
            btn.disabled = false;
        }
    }

    // importPlaylist
    // Uploads the export and queues its tracks. An export with several playlists comes back to pick from first.
    function importPlaylist() {
        var file = chosenFile();
        if (!file) {
            Shared.setStatus('queueStatus', 'Select a playlist export.', true);
            return;
        }

        var btn = Shared.getEl('btnImportPlaylist');
        if (btn) btn.disabled = true;
        var playlistIds = _playlistChoices ? _playlistChoices.getValue() : [];
        var upload = _pendingUpload
            ? Promise.resolve(_pendingUpload)
            : readFileAsBase64(file).then(function (content) {
                _pendingUpload = { FileName: file.name, Content: content };
                return _pendingUpload;
            });

        Shared.setStatus('queueStatus', 'Reading ' + file.name + '…', false, { timeout: 0 });

        upload
            .then(function (body) {
                return Shared.apiRequest('Queue/Playlist', 'POST', {
                    FileName: body.FileName,
                    Content: body.Content,
                    PlaylistIds: playlistIds,
                    UserId: _ownerSelector ? _ownerSelector.getValue() : ''
                });
            })
            .then(function (result) {
                if (result && result.NeedsSelection) {
                    Shared.setStatus('queueStatus', '', false);
                    renderPlaylistChoices(result.Playlists || []);
                    return;
                }
                Shared.setStatus('queueStatus', playlistResultMessage(result), false);
                if (_filePicker) _filePicker.clear();
                resetPlaylistImport();
                loadQueue();
                loadPlaylistTargets();
            })
            .catch(function (err) {
                reportError(err, 'Could not import the playlist.');
                if (btn) btn.disabled = false;
            });
    }

    // renderTarget
    // Draws one playlist being filled as a Base media card, with its progress and a Stop action.
    //
    // Inputs
    // t [object]: The PlaylistTargetDto.
    //
    // Output
    // [object]: The media card handle.
    function renderTarget(t) {
        return createMediaCard({
            shape: 'square',
            icon: 'queue_music',
            title: t.Name,
            subtitle: t.UserName + ' · ' + t.Added + ' of ' + countLabel(t.Total, 'song', 'songs'),
            detail: (t.Total - t.Added) + ' waiting for download or import',
            actions: [{ label: 'Stop', icon: 'stop', value: 'stop' }],
            onAction: function () { stopPlaylist(t.Id); }
        });
    }

    // buildTargetList
    // Builds the playlists being filled as a Base Filtered List, with Add Library Songs beside it.
    function buildTargetList() {
        var host = Shared.getEl('playlistTargets');
        if (!host || _targetList) return;
        _targetList = createFilteredList({
            items: [],
            rowKey: 'Id',
            render: renderTarget,
            search: false,
            summary: false,
            actions: [{ id: 'sync', label: 'Add Library Songs', icon: 'sync', onClick: syncPlaylists }],
            label: 'Playlist Imports'
        });
        host.appendChild(_targetList.element);
    }

    // loadPlaylistTargets
    // Shows the playlists being filled and how many songs each has so far.
    function loadPlaylistTargets() {
        Shared.apiRequest('Playlists', 'GET')
            .then(function (targets) {
                targets = targets || [];
                var section = Shared.getEl('playlistTargetsSection');
                if (section) section.hidden = targets.length === 0;
                if (_targetList) _targetList.setItems(targets);
            })
            .catch(function () { return null; });
    }

    // stopPlaylist
    // Stops filling a playlist, after a confirm. The Jellyfin playlist and its songs stay.
    //
    // Inputs
    // id [string]: The playlist import.
    function stopPlaylist(id) {
        confirmDialog('Stop adding songs to this playlist? The Jellyfin playlist and its songs are kept.', {
            title: 'Stop Playlist Import',
            confirmText: 'Stop'
        }).then(function (ok) {
            if (!ok) return;
            Shared.apiRequest('Playlists/' + encodeURIComponent(id), 'DELETE')
                .catch(function (err) { reportError(err, 'Could not stop the playlist import.'); })
                .then(loadPlaylistTargets);
        });
    }

    // syncPlaylists
    // Adds playlist songs that are already in the library, without waiting for a scan.
    function syncPlaylists() {
        Shared.apiRequest('Playlists/Sync', 'POST')
            .then(function (result) {
                Shared.setStatus('queueStatus', 'Added ' + countLabel((result && result.Added) || 0, 'song', 'songs') + ' to playlists.', false);
            })
            .catch(function (err) { reportError(err, 'Could not add songs to the playlists.'); })
            .then(loadPlaylistTargets);
    }

    // showSource
    // Shows the form of the chosen source and remembers it in this browser.
    //
    // Inputs
    // source [string]: youtube, applemusic, spotify, or replace.
    function showSource(source) {
        view.querySelectorAll('.yta-source-panel, .yta-source-help').forEach(function (el) {
            el.hidden = el.getAttribute('data-source').split(' ').indexOf(source) === -1;
        });
        if (source === 'applemusic' || source === 'spotify') buildFilePicker(source);
        try { localStorage.setItem('yta-download-source', source); } catch (e) { return; }
    }

    // bindSourceChoices
    // Wires the source cards and opens the one used last.
    function bindSourceChoices() {
        var group = view.querySelector('#sourceChoices');
        if (!group) return;
        var choices = createChoiceGroup(group, { onChange: showSource });
        var saved = null;
        try { saved = localStorage.getItem('yta-download-source'); } catch (e) { saved = null; }
        var initial = group.querySelector('input[value="' + saved + '"]') ? saved : 'youtube';
        choices.set(initial);
        showSource(initial);
    }

    // loadPage
    // Reloads the queue and playlist progress each time the page shows, with a poller the page stops on viewhide.
    //
    // Inputs
    // shared [object]: The page's helper bag.
    // page [object]: The page handle.
    function loadPage(shared, page) {
        _poller = page.createPoller(loadQueue, 3000);
        loadQueue();
        loadPlaylistSources();
        loadPlaylistTargets();
    }

    // bindEvents
    // Wires the page's controls once, on the first viewshow.
    function bindEvents() {
        buildQueueList();
        buildReplaceList();
        buildTargetList();
        bindSourceChoices();

        var btnQueue = Shared.bindClick('btnQueue', queueUrl);
        var urlInput = Shared.getEl('txtYouTubeUrl');
        if (urlInput) {
            urlInput.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); queueUrl(); }
            });
            urlInput.addEventListener('input', function () {
                if (btnQueue) btnQueue.disabled = !urlInput.value.trim();
            });
        }

        var ownerContainer = Shared.getEl('playlistOwner');
        if (ownerContainer && !_ownerSelector) {
            _ownerSelector = Shared.createUserSelector({
                emptyLabel: 'Do not create playlists',
                value: (window.ApiClient && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : ''
            });
            ownerContainer.appendChild(_ownerSelector.element);
        }

        var replaceUrl = Shared.getEl('txtReplaceUrl');
        if (replaceUrl) {
            replaceUrl.addEventListener('input', updateReplaceButton);
            replaceUrl.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); queueReplacement(); }
            });
        }

        Shared.bindClick('btnReplace', queueReplacement);
        Shared.bindClick('btnImportPlaylist', importPlaylist);
    }

    var page = createPage(view, {
        tabIndex: 0,
        bind: bindEvents,
        load: loadPage,
        // The page's poller is already stopped, so drop it before a late answer starts it again.
        onHide: function () { _poller = null; }
    });
    Shared = page.shared;
}
