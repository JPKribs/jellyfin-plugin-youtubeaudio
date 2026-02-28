export default function (view) {
    'use strict';

    var getTabs;
    var Shared = null;
    var _sharedPromise = import('/web/configurationpage?name=youtubeaudio_shared.js').then(function(mod) {
        getTabs = mod.getTabs;
        Shared = mod.createShared(view);
    });

    var _allItems = [];
    var _pollInterval = null;

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
                var title = (item.Title || '').toLowerCase();
                var url = (item.Url || '').toLowerCase();
                if (title.indexOf(searchVal) === -1 && url.indexOf(searchVal) === -1) return false;
            }

            return true;
        });
    }

    // ============================================
    // RENDERING (ServerSync pt-row pattern)
    // ============================================

    function renderQueue() {
        var body = view.querySelector('#queueBody');
        var footer = view.querySelector('#queueFooter');
        if (!body) return;

        var items = getFilteredItems();

        if (items.length === 0) {
            body.innerHTML = '<div class="pt-empty">Queue is empty. Add a URL above to get started.</div>';
            if (footer) footer.textContent = '';
        } else {
            var html = '';
            for (var i = 0; i < items.length; i++) {
                html += renderRow(items[i]);
            }
            body.innerHTML = html;
            if (footer) footer.textContent = items.length + ' item' + (items.length !== 1 ? 's' : '');

            // Bind row checkboxes
            body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                cb.addEventListener('change', updateQueueSelectedCount);
            });

        }

        // Reset selection state after re-render
        updateQueueSelectedCount();
        var chkAll = view.querySelector('#chkSelectAllQueue');
        if (chkAll) chkAll.checked = false;

        // Auto-poll while downloads are active
        var hasActiveDownloads = _allItems.some(function(i) { return i.StatusCode === 1; });
        if (hasActiveDownloads && !_pollInterval) {
            _pollInterval = setInterval(loadQueue, 3000);
        } else if (!hasActiveDownloads && _pollInterval) {
            clearInterval(_pollInterval);
            _pollInterval = null;
        }
    }

    function renderRow(item) {
        var esc = Shared.escapeHtml;
        var displayTitle = item.Title || item.Url;

        var errorPreview = '';
        if (item.StatusCode === 4 && item.ErrorMessage) {
            errorPreview = '<div class="yta-item-error" title="' +
                esc(item.ErrorMessage) + '">' +
                esc(item.ErrorMessage) + '</div>';
        }

        return '<div class="pt-row" data-id="' + esc(item.Id) + '">'
            + '<div class="pt-cell pt-cell-checkbox">'
            + '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="pt-row-checkbox yta-queue-check" data-id="' + esc(item.Id) + '" /><span class="checkboxLabel"></span></label>'
            + '</div>'
            + '<div class="pt-cell">'
            + '<div class="yta-item-info">'
            + '<div class="yta-item-title" title="' + esc(item.Url) + '">' + esc(displayTitle) + '</div>'
            + '<div class="yta-item-url">' + esc(item.Url) + '</div>'
            + errorPreview
            + '</div>'
            + '</div>'
            + '<div class="pt-cell pt-cell-status">'
            + Shared.getStatusBadge(item.StatusCode, item.Status)
            + '</div>'
            + '</div>';
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

    function processQueue() {
        var checkboxes = view.querySelectorAll('.yta-queue-check:checked');
        var selectedIds = [];
        checkboxes.forEach(function(cb) { selectedIds.push(cb.getAttribute('data-id')); });

        if (selectedIds.length === 0) {
            Shared.setStatus('queueStatus', 'Select items to download.', true);
            return;
        }

        Shared.setStatus('queueStatus', 'Downloading ' + selectedIds.length + ' item(s)...', false);
        Shared.apiRequest('Queue/Process', 'POST', { Ids: selectedIds })
            .then(function() {
                Shared.setStatus('queueStatus', 'Download complete.', false);
                loadQueue();
            })
            .catch(function(err) {
                Shared.setStatus('queueStatus', (err && err.Error) || 'Download failed.', true);
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
        });
    });

    view.addEventListener('viewhide', function() {
        if (_pollInterval) {
            clearInterval(_pollInterval);
            _pollInterval = null;
        }
    });

    _sharedPromise.then(function() {
        var btnQueue = Shared.getEl('btnQueue');
        if (btnQueue) btnQueue.addEventListener('click', queueUrl);

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
