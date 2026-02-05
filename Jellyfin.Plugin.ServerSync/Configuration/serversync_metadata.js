// ============================================
// METADATASYNC - PAGE CONTROLLER
// ============================================

// ============================================
// TAB NAVIGATION
// ============================================

function getTabs() {
    return [
        { href: 'configurationpage?name=serversync_settings', name: 'Settings' },
        { href: 'configurationpage?name=serversync_content', name: 'Content' },
        { href: 'configurationpage?name=serversync_history', name: 'History' },
        { href: 'configurationpage?name=serversync_metadata', name: 'Metadata' },
        { href: 'configurationpage?name=serversync_users', name: 'Users' }
    ];
}

export default function (view, params) {
    'use strict';

    // ============================================
    // SHARED UTILITIES
    // ============================================

    var ServerSyncShared = {
        pluginId: 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286',
        localServerName: null,

        // --- Formatting ---

        formatRelativeTime: function(date) {
            var now = new Date();
            var diff = now - date;
            var minutes = Math.floor(diff / 60000);
            var hours = Math.floor(minutes / 60);
            var days = Math.floor(hours / 24);

            if (days > 0) return days + ' day' + (days > 1 ? 's' : '') + ' ago';
            if (hours > 0) return hours + ' hour' + (hours > 1 ? 's' : '') + ' ago';
            if (minutes > 0) return minutes + ' minute' + (minutes > 1 ? 's' : '') + ' ago';
            return 'Just now';
        },

        escapeHtml: function(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        },

        // --- Alerts ---

        showAlert: function(message) {
            Dashboard.alert(message);
        },

        // --- Configuration ---

        getConfig: function() {
            return ApiClient.getPluginConfiguration(this.pluginId);
        },

        fetchLocalServerName: function() {
            var self = this;
            return ApiClient.getPublicSystemInfo().then(function(info) {
                self.localServerName = info.ServerName || 'Local';
                return self.localServerName;
            }).catch(function() {
                self.localServerName = 'Local';
                return self.localServerName;
            });
        },

        // --- API Requests ---

        apiRequest: function(endpoint, method, data) {
            var options = {
                url: ApiClient.getUrl('ServerSync/' + endpoint),
                type: method || 'GET',
                dataType: 'json'
            };

            if (data) {
                options.contentType = 'application/json';
                options.data = JSON.stringify(data);
            }

            return ApiClient.fetch(options).then(function(response) {
                return response;
            }).catch(function(error) {
                if (error && error.message && error.message.indexOf('JSON') !== -1) {
                    return null;
                }
                throw error;
            });
        },

        // --- DOM Utilities ---

        bindEvent: function(id, event, handler, moduleName) {
            var el = view.querySelector('#' + id);
            if (el) {
                el.addEventListener(event, handler);
            } else if (moduleName) {
                console.error(moduleName + ': #' + id + ' not found');
            }
            return el;
        },

        bindClick: function(id, handler, moduleName) {
            return this.bindEvent(id, 'click', handler, moduleName);
        },

        // Poll a scheduled task for progress and update button UI
        pollTaskProgress: function(btn, taskKey, label, onComplete) {
            var progressBar = btn.querySelector('.btn-progress');
            if (!progressBar) {
                progressBar = document.createElement('div');
                progressBar.className = 'btn-progress';
                btn.appendChild(progressBar);
            }
            progressBar.style.width = '0%';
            btn.disabled = true;

            var pollInterval = setInterval(function() {
                ApiClient.getScheduledTasks().then(function(tasks) {
                    var task = tasks.find(function(t) { return t.Key === taskKey; });
                    if (!task) {
                        clearInterval(pollInterval);
                        btn.querySelector('span').textContent = label;
                        progressBar.style.width = '0%';
                        btn.disabled = false;
                        if (onComplete) onComplete();
                        return;
                    }

                    if (task.State === 'Running') {
                        var pct = Math.round(task.CurrentProgressPercentage || 0);
                        btn.querySelector('span').textContent = label + ' ' + pct + '%';
                        progressBar.style.width = pct + '%';
                    } else if (task.State === 'Idle') {
                        clearInterval(pollInterval);
                        btn.querySelector('span').textContent = label;
                        progressBar.style.width = '0%';
                        btn.disabled = false;
                        if (onComplete) onComplete();
                    }
                }).catch(function() {
                    clearInterval(pollInterval);
                    btn.querySelector('span').textContent = label;
                    progressBar.style.width = '0%';
                    btn.disabled = false;
                    if (onComplete) onComplete();
                });
            }, 1500);

            return pollInterval;
        }
    };


    // ============================================
    // PAGINATED TABLE COMPONENT
    // ============================================

    var PaginatedTable = function(options) {
        this.options = {
            containerId: null,
            endpoint: '',
            columns: [],
            selection: { enabled: false, idKey: 'id' },
            pagination: { pageSize: 50 },
            filters: { options: [] },
            search: { enabled: true, placeholder: 'Search...', debounceMs: 300 },
            actions: {},
            emptyState: { message: 'No items found' }
        };

        this._mergeOptions(options);

        this.state = {
            items: [],
            totalCount: 0,
            currentPage: 1,
            pageSize: this.options.pagination.pageSize,
            searchQuery: '',
            filterValue: '',
            selectedIds: new Set(),
            isLoading: false,
            hasMore: true
        };

        this.elements = {};
        this.searchTimeout = null;
        this._init();
    };

    PaginatedTable.prototype = {

        // --- Initialization ---

        _init: function() {
            this._createStructure();
            this._bindEvents();
        },

        _mergeOptions: function(options) {
            var self = this;
            Object.keys(options).forEach(function(key) {
                if (typeof options[key] === 'object' && options[key] !== null && !Array.isArray(options[key])) {
                    self.options[key] = Object.assign({}, self.options[key], options[key]);
                } else {
                    self.options[key] = options[key];
                }
            });
        },

        _createStructure: function() {
            var container = view.querySelector('#' + this.options.containerId);
            if (!container) {
                console.error('PaginatedTable: Container not found:', this.options.containerId);
                return;
            }
            container.innerHTML = this._buildHTML();
            this._cacheElements(container);
        },

        _cacheElements: function(container) {
            this.elements = {
                container: container,
                search: container.querySelector('.pt-search'),
                filter: container.querySelector('.pt-filter'),
                selectAll: container.querySelector('.pt-select-all'),
                selectedCount: container.querySelector('.pt-selected-count'),
                bulkActions: container.querySelector('.pt-bulk-actions'),
                reloadBtn: container.querySelector('.pt-reload-btn'),
                body: container.querySelector('.pt-body'),
                loadingMore: container.querySelector('.pt-loading-more'),
                itemCount: container.querySelector('.pt-item-count')
            };
        },

        // --- HTML Building ---

        _buildHTML: function() {
            var opts = this.options;
            var html = '<div class="pt-wrapper">';

            // Top row: Search + Filter
            html += '<div class="pt-controls">';
            if (opts.search && opts.search.enabled !== false) {
                html += '<input type="text" class="pt-search" placeholder="' +
                    ServerSyncShared.escapeHtml(opts.search.placeholder || 'Search...') + '" />';
            }
            if (opts.filters && opts.filters.options && opts.filters.options.length > 0) {
                html += '<div class="pt-filter-wrapper">';
                html += '<select class="pt-filter">';
                html += '<option value="">All</option>';
                opts.filters.options.forEach(function(opt) {
                    var style = opt.hidden ? ' style="display:none"' : '';
                    var id = opt.id ? ' id="' + opt.id + '"' : '';
                    html += '<option value="' + ServerSyncShared.escapeHtml(opt.value) + '"' + style + id + '>' +
                        ServerSyncShared.escapeHtml(opt.label) + '</option>';
                });
                html += '</select>';
                html += '<span class="pt-filter-arrow">&#9662;</span>';
                html += '</div>';
            }
            html += '</div>';

            // Bottom row: Selection controls + Bulk actions + Reload button
            if (opts.selection && opts.selection.enabled) {
                html += '<div class="pt-selection-header">';
                html += '<label class="pt-select-all-label">';
                html += '<input type="checkbox" class="pt-select-all" />';
                html += '<span class="pt-checkbox-custom"></span>';
                html += '<span class="pt-select-all-text">Select All</span>';
                html += '</label>';
                html += '<span class="pt-selected-count">0 selected</span>';
                html += '<div class="pt-bulk-actions"></div>';
                html += '<span class="pt-header-spacer"></span>';
                html += '<button type="button" class="pt-reload-btn" title="Reload">';
                html += '<span class="pt-reload-icon">&#8635;</span>';
                html += '</button>';
                html += '</div>';
            }

            // Table body
            html += '<div class="pt-body"></div>';

            // Loading indicator
            html += '<div class="pt-loading-more" style="display:none;">Loading more...</div>';

            // Footer
            html += '<div class="pt-footer">';
            html += '<span class="pt-item-count"></span>';
            html += '</div>';

            html += '</div>';
            return html;
        },

        // --- Event Binding ---

        _bindEvents: function() {
            var self = this;

            if (this.elements.search) {
                this.elements.search.addEventListener('input', function(e) {
                    self._handleSearchInput(e.target.value);
                });
            }

            if (this.elements.filter) {
                this.elements.filter.addEventListener('change', function(e) {
                    self.setFilter(e.target.value);
                });
            }

            if (this.elements.selectAll) {
                this.elements.selectAll.addEventListener('change', function(e) {
                    self._toggleSelectAll(e.target.checked);
                });
            }

            if (this.elements.reloadBtn) {
                this.elements.reloadBtn.addEventListener('click', function() {
                    self._handleReload();
                });
            }

            if (this.elements.body) {
                this.elements.body.addEventListener('scroll', function() {
                    self._handleScroll();
                });
            }
        },

        // --- Event Handlers ---

        _handleSearchInput: function(value) {
            var self = this;
            if (this.searchTimeout) {
                clearTimeout(this.searchTimeout);
            }
            var debounceMs = this.options.search.debounceMs || 300;
            this.searchTimeout = setTimeout(function() {
                self.setSearch(value);
            }, debounceMs);
        },

        _handleScroll: function() {
            var body = this.elements.body;
            if (!body || this.state.isLoading || !this.state.hasMore) return;

            var scrollTop = body.scrollTop;
            var scrollHeight = body.scrollHeight;
            var clientHeight = body.clientHeight;

            // Load more when within 100px of bottom
            if (scrollTop + clientHeight >= scrollHeight - 100) {
                this._loadMore();
            }
        },

        _handleReload: function() {
            var self = this;
            var btn = this.elements.reloadBtn;

            if (btn) {
                btn.classList.add('spinning');
                btn.disabled = true;
            }

            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.state.selectedIds.clear();

            this.load().then(function() {
                if (btn) {
                    btn.classList.remove('spinning');
                    btn.disabled = false;
                }
                if (self.options.actions && self.options.actions.onReload) {
                    self.options.actions.onReload();
                }
            }).catch(function() {
                if (btn) {
                    btn.classList.remove('spinning');
                    btn.disabled = false;
                }
            });
        },

        // --- Data Loading ---

        reload: function() {
            console.log('PaginatedTable reload called, clearing state');
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.state.selectedIds.clear();
            return this.load();
        },

        load: function() {
            var self = this;
            var state = this.state;
            var opts = this.options;

            if (state.isLoading) return Promise.resolve();
            state.isLoading = true;
            this._setLoading(true);

            // Build query params
            var params = [];
            params.push('skip=' + ((state.currentPage - 1) * state.pageSize));
            params.push('take=' + state.pageSize);

            if (state.searchQuery) {
                params.push('search=' + encodeURIComponent(state.searchQuery));
            }

            if (state.filterValue) {
                if (opts.filters && opts.filters.buildParams) {
                    var filterParams = opts.filters.buildParams(state.filterValue);
                    Object.keys(filterParams).forEach(function(key) {
                        if (filterParams[key] !== null && filterParams[key] !== undefined) {
                            params.push(key + '=' + encodeURIComponent(filterParams[key]));
                        }
                    });
                } else {
                    params.push('filter=' + encodeURIComponent(state.filterValue));
                }
            }

            var endpoint = opts.endpoint + (params.length ? '?' + params.join('&') : '');

            return ServerSyncShared.apiRequest(endpoint, 'GET').then(function(result) {
                var newItems = result.Items || [];
                state.totalCount = result.TotalCount || 0;

                // First page replaces, subsequent pages append
                if (state.currentPage === 1) {
                    state.items = newItems;
                } else {
                    state.items = state.items.concat(newItems);
                }

                state.hasMore = state.items.length < state.totalCount;

                self._render();
                state.isLoading = false;
                self._setLoading(false);
                return result;
            }).catch(function(err) {
                console.error('PaginatedTable load error:', err);
                state.isLoading = false;
                self._setLoading(false);
                throw err;
            });
        },

        _loadMore: function() {
            if (!this.state.hasMore || this.state.isLoading) return;

            this.state.currentPage++;

            if (this.elements.loadingMore) {
                this.elements.loadingMore.style.display = 'block';
            }

            var self = this;
            this.load().finally(function() {
                if (self.elements.loadingMore) {
                    self.elements.loadingMore.style.display = 'none';
                }
            });
        },

        _setLoading: function(loading) {
            if (this.elements.container) {
                if (loading && this.state.currentPage === 1) {
                    this.elements.container.classList.add('pt-loading');
                } else {
                    this.elements.container.classList.remove('pt-loading');
                }
            }
        },

        // --- Rendering ---

        _render: function() {
            this._renderBody();
            this._updateItemCount();
            this._updateSelectionUI();
        },

        _renderBody: function() {
            var self = this;
            var state = this.state;
            var opts = this.options;
            var body = this.elements.body;

            if (!body) return;

            if (state.items.length === 0) {
                body.innerHTML = '<div class="pt-empty">' +
                    ServerSyncShared.escapeHtml(opts.emptyState.message) + '</div>';
                return;
            }

            body.innerHTML = state.items.map(function(item) {
                return self._renderRow(item);
            }).join('');

            this._bindRowEvents();
        },

        _renderRow: function(item) {
            var self = this;
            var opts = this.options;
            var itemId = this._getItemId(item);
            var visibleColumns = opts.columns.filter(function(col) { return !col.hidden; });

            var html = '<div class="pt-row" data-id="' + ServerSyncShared.escapeHtml(String(itemId)) + '">';

            // Checkbox cell
            if (opts.selection && opts.selection.enabled) {
                var checked = this.state.selectedIds.has(String(itemId)) ? ' checked' : '';
                html += '<div class="pt-cell pt-cell-checkbox">';
                html += '<input type="checkbox" class="pt-row-checkbox" data-id="' +
                    ServerSyncShared.escapeHtml(String(itemId)) + '"' + checked + ' />';
                html += '</div>';
            }

            // Data cells
            visibleColumns.forEach(function(col) {
                var value = item[col.key];
                var cellClass = 'pt-cell';
                if (col.type === 'status') cellClass += ' pt-cell-status';
                if (col.className) cellClass += ' ' + col.className;
                var content = '';

                if (col.type === 'status') {
                    var displayStatus = self._getDisplayStatus(item, value);
                    var statusClass = self._getStatusClass(item, value);
                    content = '<button type="button" class="pt-status-badge pt-status-btn ' + statusClass + '" data-id="' +
                        ServerSyncShared.escapeHtml(String(itemId)) + '">' +
                        ServerSyncShared.escapeHtml(displayStatus) + '</button>';
                } else if (col.type === 'custom' && col.render) {
                    content = col.render(item, value);
                } else {
                    content = value !== null && value !== undefined ? ServerSyncShared.escapeHtml(String(value)) : '';
                }

                html += '<div class="' + cellClass + '">' + content + '</div>';
            });

            html += '</div>';
            return html;
        },

        _getDisplayStatus: function(item, value) {
            if (this.options.getDisplayStatus) {
                return this.options.getDisplayStatus(item, value);
            }
            return value || '';
        },

        _getStatusClass: function(item, value) {
            if (this.options.getStatusClass) {
                return this.options.getStatusClass(item, value);
            }
            return value || '';
        },

        // --- Row Events ---

        _bindRowEvents: function() {
            var self = this;
            var body = this.elements.body;
            if (!body) return;

            body.querySelectorAll('.pt-row').forEach(function(row) {
                var handleRowAction = function(e) {
                    if (e.target.type === 'checkbox' || e.target.classList.contains('pt-row-checkbox') ||
                        e.target.classList.contains('pt-status-btn')) {
                        return;
                    }

                    var id = row.dataset.id;
                    var item = self._getItemById(id);
                    if (item && self.options.actions && self.options.actions.onRowClick) {
                        e.preventDefault();
                        e.stopPropagation();
                        self.options.actions.onRowClick(item);
                    }
                };

                row.addEventListener('click', handleRowAction);
            });

            // Status badge buttons
            body.querySelectorAll('.pt-status-btn').forEach(function(btn) {
                btn.addEventListener('click', function(e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var id = btn.dataset.id;
                    var item = self._getItemById(id);
                    if (item && self.options.actions && self.options.actions.onRowClick) {
                        self.options.actions.onRowClick(item);
                    }
                });
            });

            // Row checkboxes
            body.querySelectorAll('.pt-row-checkbox').forEach(function(checkbox) {
                checkbox.addEventListener('change', function(e) {
                    e.stopPropagation();
                    var id = checkbox.dataset.id;
                    if (checkbox.checked) {
                        self.state.selectedIds.add(id);
                    } else {
                        self.state.selectedIds.delete(id);
                    }
                    self._updateSelectionUI();
                    self._notifySelectionChange();
                });

                checkbox.addEventListener('click', function(e) {
                    e.stopPropagation();
                });
            });
        },

        // --- Selection Management ---

        _getItemId: function(item) {
            var idKey = this.options.selection && this.options.selection.idKey || 'id';
            if (typeof idKey === 'function') {
                return idKey(item);
            }
            return item[idKey];
        },

        _getItemById: function(id) {
            var self = this;
            return this.state.items.find(function(item) {
                return String(self._getItemId(item)) === String(id);
            });
        },

        _updateItemCount: function() {
            if (this.elements.itemCount) {
                var loaded = this.state.items.length;
                var total = this.state.totalCount;

                if (total === 0) {
                    this.elements.itemCount.textContent = '';
                } else if (loaded >= total) {
                    this.elements.itemCount.textContent = total + ' items';
                } else {
                    this.elements.itemCount.textContent = 'Showing ' + loaded + ' of ' + total + ' items';
                }
            }
        },

        _updateSelectionUI: function() {
            var count = this.state.selectedIds.size;

            if (this.elements.selectedCount) {
                this.elements.selectedCount.textContent = count + ' selected';
            }

            if (this.elements.selectAll) {
                var allSelected = count > 0 && count === this.state.items.length;
                var someSelected = count > 0 && count < this.state.items.length;
                this.elements.selectAll.checked = allSelected;
                this.elements.selectAll.indeterminate = someSelected;
            }
        },

        _toggleSelectAll: function(checked) {
            var self = this;

            this.state.selectedIds.clear();

            if (checked) {
                this.state.items.forEach(function(item) {
                    self.state.selectedIds.add(String(self._getItemId(item)));
                });
            }

            if (this.elements.body) {
                this.elements.body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                    cb.checked = checked;
                });
            }

            this._updateSelectionUI();
            this._notifySelectionChange();
        },

        _notifySelectionChange: function() {
            if (this.options.selection && this.options.selection.onSelectionChange) {
                this.options.selection.onSelectionChange(this.getSelectedIds());
            }
        },

        // --- Public API ---

        getSelectedIds: function() {
            return Array.from(this.state.selectedIds);
        },

        getSelectedItems: function() {
            var self = this;
            return this.state.items.filter(function(item) {
                return self.state.selectedIds.has(String(self._getItemId(item)));
            });
        },

        clearSelection: function() {
            this.state.selectedIds.clear();
            this._updateSelectionUI();
            this._notifySelectionChange();

            if (this.elements.body) {
                this.elements.body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                    cb.checked = false;
                });
            }
            if (this.elements.selectAll) {
                this.elements.selectAll.checked = false;
                this.elements.selectAll.indeterminate = false;
            }
        },

        refresh: function() {
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            return this.load();
        },

        setFilter: function(value) {
            this.state.filterValue = value;
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        setSearch: function(query) {
            this.state.searchQuery = query;
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        getItems: function() {
            return this.state.items;
        },

        getTotalCount: function() {
            return this.state.totalCount;
        },

        getBulkActionsContainer: function() {
            return this.elements.bulkActions;
        },

        setFilterOptionVisible: function(optionId, visible) {
            if (this.elements.filter) {
                var option = this.elements.filter.querySelector('#' + optionId);
                if (option) {
                    option.style.display = visible ? 'block' : 'none';
                }
            }
        },

        setFilterValue: function(value) {
            this.state.filterValue = value;
            if (this.elements.filter) {
                this.elements.filter.value = value;
            }
        }
    };


    // ============================================
    // METADATA SYNC TABLE MODULE
    // ============================================

    var MetadataSyncTableModule = {
        table: null,
        currentModalItem: null,
        currentConfig: null,
        _initialized: false,

        // --- Initialization ---

        init: function(config) {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            this.table = new PaginatedTable({
                containerId: 'metadataSyncItemsTableContainer',
                endpoint: 'MetadataItems',

                columns: [
                    {
                        key: 'item',
                        label: 'Item',
                        type: 'custom',
                        render: function(item) {
                            var itemName = item.ItemName || 'Unknown';
                            var sourceLib = item.SourceLibraryName || 'Unknown';
                            var localLib = item.LocalLibraryName || 'Unknown';
                            var libraryDisplay = sourceLib + ' → ' + localLib;

                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' + ServerSyncShared.escapeHtml(itemName) + '">' +
                                ServerSyncShared.escapeHtml(itemName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(libraryDisplay) + '</div>' +
                                errorPreview +
                                '</div>';
                        }
                    },
                    {
                        key: 'changes',
                        label: 'Changes',
                        type: 'custom',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.ChangesSummary || 'Changes pending');
                        }
                    },
                    {
                        key: 'Status',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: 'Id',
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: {
                    pageSize: 50
                },

                filters: {
                    options: [
                        { value: 'Synced', label: 'Synced' },
                        { value: 'Queued', label: 'Queued' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                search: {
                    placeholder: 'Search items...'
                },

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.Id);
                    },
                    onReload: function() {
                        self.loadMetadataStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No metadata items found. Run a refresh to scan for metadata.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'MetadataSyncTableModule'); };

            // Action buttons
            bind('btnRefreshMetadataItems', function() { self.refreshMetadataTable(); });
            bind('btnTriggerMetadataSync', function() { self.triggerMetadataSync(); });
            bind('btnRetryMetadataErrors', function() { self.retryErrors(); });
            bind('btnResetMetadataDatabase', function() { self.resetMetadataDatabase(); });

            // Modal buttons
            bind('btnMetadataSyncModalIgnore', function() { self.modalIgnore(); });
            bind('btnMetadataSyncModalQueue', function() { self.modalQueue(); });
            bind('btnMetadataSyncModalClose', function() { self.closeModal(); });
        },

        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnMetadataBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnMetadataBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>';

            view.querySelector('#btnMetadataBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnMetadataBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        // --- Status & Health Loading ---

        loadMetadataStatus: function() {
            return ServerSyncShared.apiRequest('MetadataStatus', 'GET').then(function(status) {
                view.querySelector('#metadataSyncedCount').textContent = status.Synced || 0;
                view.querySelector('#metadataQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#metadataErroredCount').textContent = status.Errored || 0;
                view.querySelector('#metadataIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#metadataStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#metadataStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#metadataStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#metadataStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));

                // Show/hide Retry Errors button based on error count
                var retryBtn = view.querySelector('#btnRetryMetadataErrors');
                if ((status.Errored || 0) > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function(err) {
                console.log('Metadata status not available:', err);
            });
        },

        loadHealthStats: function() {
            var self = this;
            return Promise.all([
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('MetadataStatus', 'GET')
            ]).then(function(results) {
                var config = results[0];
                var status = results[1];

                // Last metadata sync time
                var lastSyncEl = view.querySelector('#metadataHealthLastSync');
                if (config.LastMetadataSyncTime) {
                    var lastSync = new Date(config.LastMetadataSyncTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                // Library mapping count
                var libraryCountEl = view.querySelector('#metadataHealthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';
            }).catch(function() {
                // Ignore errors
            });
        },

        loadMetadataItems: function() {
            return this.table.reload();
        },

        // --- Action Handlers ---

        refreshMetadataTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshMetadataItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerMetadataRefresh', 'POST').then(function() {
                ServerSyncShared.pollTaskProgress(btn, 'ServerSyncRefreshMetadataTable', 'Refresh', function() {
                    self.loadMetadataStatus();
                    self.loadMetadataItems();
                    self.loadHealthStats();
                });
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start metadata refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        triggerMetadataSync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerMetadataSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerMetadataSync', 'POST').then(function() {
                ServerSyncShared.pollTaskProgress(btn, 'ServerSyncMissingMetadata', 'Sync', function() {
                    self.loadMetadataStatus();
                    self.loadMetadataItems();
                    self.loadHealthStats();
                });
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start metadata sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored metadata items queued for retry');
                self.loadMetadataStatus();
                self.loadMetadataItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        resetMetadataDatabase: function() {
            var self = this;

            if (!confirm('Are you sure you want to reset the metadata sync database?\n\nThis will delete ALL metadata tracking data and you will need to re-sync everything. This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('ResetMetadataSyncDatabase', 'POST').then(function() {
                ServerSyncShared.showAlert('Metadata sync database has been reset');
                self.loadMetadataStatus();
                self.loadMetadataItems();
                self.loadHealthStats();
            }).catch(function(err) {
                console.error('ResetMetadataSyncDatabase error:', err);
                ServerSyncShared.showAlert('Failed to reset metadata sync database');
            });
        },

        // --- Bulk Actions ---

        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnMetadataBulkIgnore');
            var queueBtn = view.querySelector('#btnMetadataBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        bulkIgnore: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('MetadataItems/Ignore', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore items');
            });
        },

        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('MetadataItems/Queue', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        // --- Modal Management ---

        showItemDetail: function(itemId) {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/' + itemId).then(function(item) {
                if (!item) {
                    ServerSyncShared.showAlert('Item not found');
                    return;
                }

                self.currentModalItem = item;

                // Set title
                view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || 'Unknown';

                // Status badge
                var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                statusBadge.textContent = item.Status;
                statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                // Server mapping display
                var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#metadataSyncModalServerMapping').textContent = sourceServerName + ' → ' + localServerName;

                // Last sync
                if (item.LastSyncTime) {
                    view.querySelector('#metadataSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                } else {
                    view.querySelector('#metadataSyncModalLastSync').textContent = '-';
                }

                // Library mapping
                var sourceLib = item.SourceLibraryName || 'Unknown';
                var localLib = item.LocalLibraryName || 'Unknown';
                view.querySelector('#metadataSyncModalLibrary').textContent = sourceLib + ' → ' + localLib;

                // Error section
                var errorSection = view.querySelector('#metadataSyncModalErrorSection');
                if (item.Status === 'Errored' && item.ErrorMessage) {
                    view.querySelector('#metadataSyncModalError').textContent = item.ErrorMessage;
                    errorSection.classList.remove('hidden');
                } else {
                    errorSection.classList.add('hidden');
                }

                // Set server names in table headers
                view.querySelector('#metadataSyncModalSourceHeader').textContent = sourceServerName;
                view.querySelector('#metadataSyncModalLocalHeader').textContent = localServerName;

                // Build changes summary badges
                self.buildChangesSummary(item);

                // Populate paths
                view.querySelector('#metadataSyncModalSourcePath').textContent = item.SourcePath || '-';
                view.querySelector('#metadataSyncModalLocalPath').textContent = item.LocalPath || '-';

                // Build property comparison table with subsections
                self.buildPropertyTable(item);

                view.querySelector('#metadataSyncItemDetailModal').classList.remove('hidden');
            }).catch(function(err) {
                console.error('Failed to load metadata item details:', err);
                ServerSyncShared.showAlert('Failed to load item details');
            });
        },

        buildChangesSummary: function(item) {
            var container = view.querySelector('#metadataSyncModalChangesSummary');
            var config = this.currentConfig || {};
            var html = '';

            // Get config settings - each section can be individually enabled/disabled
            var metadataEnabled = config.MetadataSyncMetadata !== false;
            var genresEnabled = config.MetadataSyncGenres !== false;
            var tagsEnabled = config.MetadataSyncTags !== false;
            var studiosEnabled = config.MetadataSyncStudios !== false;
            var peopleEnabled = config.MetadataSyncPeople === true;
            var imagesEnabled = config.MetadataSyncImages !== false;

            // Parse metadata to check for specific changes
            var sourceMetadata = this.parseJsonSafe(item.SourceMetadataValue) || {};
            var localMetadata = this.parseJsonSafe(item.LocalMetadataValue) || {};

            // Metadata badge (only if enabled)
            if (metadataEnabled) {
                var hasMetadataChanges = item.HasMetadataChanges === true;
                html += '<span class="metadataSyncModal-changesBadge ' + (hasMetadataChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'Metadata: ' + (hasMetadataChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            // Genres badge (only if enabled)
            if (genresEnabled) {
                var sourceGenres = sourceMetadata.Genres || [];
                var localGenres = localMetadata.Genres || [];
                var hasGenresChanges = JSON.stringify(sourceGenres.slice().sort()) !== JSON.stringify(localGenres.slice().sort());
                html += '<span class="metadataSyncModal-changesBadge ' + (hasGenresChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'Genres: ' + (hasGenresChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            // Tags badge (only if enabled)
            if (tagsEnabled) {
                var sourceTags = sourceMetadata.Tags || [];
                var localTags = localMetadata.Tags || [];
                var hasTagsChanges = JSON.stringify(sourceTags.slice().sort()) !== JSON.stringify(localTags.slice().sort());
                html += '<span class="metadataSyncModal-changesBadge ' + (hasTagsChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'Tags: ' + (hasTagsChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            // Images badge (only if enabled)
            if (imagesEnabled) {
                var hasImagesChanges = item.HasImagesChanges === true;
                html += '<span class="metadataSyncModal-changesBadge ' + (hasImagesChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'Images: ' + (hasImagesChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            // People badge (only if enabled)
            if (peopleEnabled) {
                var hasPeopleChanges = item.HasPeopleChanges === true;
                html += '<span class="metadataSyncModal-changesBadge ' + (hasPeopleChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'People: ' + (hasPeopleChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            // Studios badge (only if enabled)
            if (studiosEnabled) {
                var hasStudiosChanges = item.HasStudiosChanges === true;
                html += '<span class="metadataSyncModal-changesBadge ' + (hasStudiosChanges ? 'has-changes' : 'no-changes') + '">';
                html += 'Studios: ' + (hasStudiosChanges ? 'Changes' : 'Synced');
                html += '</span>';
            }

            container.innerHTML = html;
        },

        buildPropertyTable: function(item) {
            var self = this;
            var tbody = view.querySelector('#metadataSyncModalTableBody');
            var config = this.currentConfig || {};
            var html = '';

            // Get config settings - each section can be individually enabled/disabled
            var metadataEnabled = config.MetadataSyncMetadata !== false;
            var genresEnabled = config.MetadataSyncGenres !== false;
            var tagsEnabled = config.MetadataSyncTags !== false;
            var studiosEnabled = config.MetadataSyncStudios !== false;
            var peopleEnabled = config.MetadataSyncPeople === true;
            var imagesEnabled = config.MetadataSyncImages !== false;

            // Parse JSON data
            var sourceMetadata = self.parseJsonSafe(item.SourceMetadataValue) || {};
            var localMetadata = self.parseJsonSafe(item.LocalMetadataValue) || {};
            var sourceImages = self.parseJsonSafe(item.SourceImagesValue);
            var localImages = self.parseJsonSafe(item.LocalImagesValue);
            var sourcePeople = self.parseJsonSafe(item.SourcePeopleValue);
            var localPeople = self.parseJsonSafe(item.LocalPeopleValue);
            var sourceStudios = self.parseJsonSafe(item.SourceStudiosValue);
            var localStudios = self.parseJsonSafe(item.LocalStudiosValue);

            // --- METADATA SECTION (core fields) ---
            if (metadataEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Metadata</td></tr>';
                html += self.buildCoreMetadataRows(sourceMetadata, localMetadata);
            }

            // --- GENRES SECTION ---
            if (genresEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Genres</td></tr>';
                html += self.buildArrayComparisonRow('Genres', sourceMetadata.Genres, localMetadata.Genres);
            }

            // --- TAGS SECTION ---
            if (tagsEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Tags</td></tr>';
                html += self.buildArrayComparisonRow('Tags', sourceMetadata.Tags, localMetadata.Tags);
            }

            // --- STUDIOS SECTION ---
            if (studiosEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Studios</td></tr>';
                html += self.buildArrayComparisonRow('Studios', sourceStudios, localStudios);
            }

            // --- PEOPLE SECTION ---
            if (peopleEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">People</td></tr>';
                html += self.buildPeopleComparisonRow(sourcePeople, localPeople);
            }

            // --- IMAGES SECTION ---
            if (imagesEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Images</td></tr>';
                html += self.buildImagesRows(sourceImages, localImages, item);
            }

            if (html === '') {
                html = '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No sync categories are enabled</td></tr>';
            }

            tbody.innerHTML = html;
        },

        parseJsonSafe: function(jsonString) {
            if (!jsonString) return null;
            try {
                return JSON.parse(jsonString);
            } catch (e) {
                console.error('Failed to parse JSON:', e);
                return null;
            }
        },

        buildCoreMetadataRows: function(source, local) {
            var self = this;
            var html = '';

            // Define core metadata fields (excluding arrays that get their own sections)
            var metadataFields = [
                { key: 'Name', label: 'Name' },
                { key: 'OriginalTitle', label: 'Original Title' },
                { key: 'SortName', label: 'Sort Name' },
                { key: 'ForcedSortName', label: 'Forced Sort Name' },
                { key: 'Overview', label: 'Overview', truncate: true },
                { key: 'Tagline', label: 'Tagline' },
                { key: 'OfficialRating', label: 'Parental Rating' },
                { key: 'CustomRating', label: 'Custom Rating' },
                { key: 'CommunityRating', label: 'Community Rating' },
                { key: 'CriticRating', label: 'Critic Rating' },
                { key: 'PremiereDate', label: 'Release Date', isDate: true },
                { key: 'EndDate', label: 'End Date', isDate: true },
                { key: 'ProductionYear', label: 'Year' },
                { key: 'AspectRatio', label: 'Aspect Ratio' },
                { key: 'Video3DFormat', label: '3D Format' },
                { key: 'IndexNumber', label: 'Index Number' },
                { key: 'ParentIndexNumber', label: 'Parent Index Number' },
                { key: 'PreferredMetadataCountryCode', label: 'Country/Region' },
                { key: 'PreferredMetadataLanguage', label: 'Preferred Language' },
                { key: 'LockData', label: 'Lock Item', isBoolean: true },
                { key: 'LockedFields', label: 'Locked Fields', isArray: true }
            ];

            metadataFields.forEach(function(field) {
                var sourceVal = source[field.key];
                var localVal = local[field.key];

                // Always show all fields, displaying "-" for null/empty values
                var sourceDisplay = self.formatMetadataValue(sourceVal, field);
                var localDisplay = self.formatMetadataValue(localVal, field);

                // Determine if changed (normalize for comparison)
                var isChanged = self.normalizeForComparison(sourceVal, field) !== self.normalizeForComparison(localVal, field);
                var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                // After sync shows source value (sync direction is source -> local)
                var mergedDisplay = sourceDisplay;

                html += '<tr class="' + rowClass + '">';
                html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(field.label) + '</td>';
                html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + mergedDisplay + '</td>';
                html += '</tr>';
            });

            // Provider IDs (dictionary - render one row per provider key)
            var sourceProviders = source.ProviderIds || {};
            var localProviders = local.ProviderIds || {};
            var allProviderKeys = Object.keys(sourceProviders).concat(Object.keys(localProviders));
            var uniqueKeys = [];
            allProviderKeys.forEach(function(k) {
                if (uniqueKeys.indexOf(k) === -1) uniqueKeys.push(k);
            });
            uniqueKeys.sort();

            uniqueKeys.forEach(function(key) {
                var srcVal = sourceProviders[key] != null ? String(sourceProviders[key]) : '';
                var lclVal = localProviders[key] != null ? String(localProviders[key]) : '';
                var srcDisplay = srcVal || '-';
                var lclDisplay = lclVal || '-';
                var isChanged = srcVal !== lclVal;
                var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + rowClass + '">';
                html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(key) + '</td>';
                html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(lclDisplay) + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                html += '</tr>';
            });

            if (uniqueKeys.length === 0) {
                html += '<tr>';
                html += '<td class="historyCompareTable-property">Provider IDs</td>';
                html += '<td class="historyCompareTable-value">-</td>';
                html += '<td class="historyCompareTable-value">-</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">-</td>';
                html += '</tr>';
            }

            return html;
        },

        buildArrayComparisonRow: function(label, sourceArray, localArray) {
            var html = '';
            var sourceItems = Array.isArray(sourceArray) ? sourceArray : [];
            var localItems = Array.isArray(localArray) ? localArray : [];

            var sourceCount = sourceItems.length;
            var localCount = localItems.length;

            // Count row
            var countChanged = sourceCount !== localCount;
            var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
            html += '<tr class="' + countRowClass + '">';
            html += '<td class="historyCompareTable-property">Count</td>';
            html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
            html += '<td class="historyCompareTable-value">' + localCount + '</td>';
            html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
            html += '</tr>';

            // Items row (show list)
            var sourceDisplay = sourceItems.length > 0 ? ServerSyncShared.escapeHtml(sourceItems.join(', ')) : '-';
            var localDisplay = localItems.length > 0 ? ServerSyncShared.escapeHtml(localItems.join(', ')) : '-';

            var sourceSorted = sourceItems.slice().sort().join(',');
            var localSorted = localItems.slice().sort().join(',');
            var itemsChanged = sourceSorted !== localSorted;
            var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

            html += '<tr class="' + itemsRowClass + '">';
            html += '<td class="historyCompareTable-property">Items</td>';
            html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
            html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
            html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
            html += '</tr>';

            return html;
        },

        buildPeopleComparisonRow: function(sourcePeople, localPeople) {
            var html = '';

            // Extract just the names from the people arrays
            var sourceNames = [];
            var localNames = [];

            if (Array.isArray(sourcePeople)) {
                sourcePeople.forEach(function(person) {
                    if (person && person.Name) {
                        sourceNames.push(person.Name);
                    }
                });
            }

            if (Array.isArray(localPeople)) {
                localPeople.forEach(function(person) {
                    if (person && person.Name) {
                        localNames.push(person.Name);
                    }
                });
            }

            var sourceCount = sourceNames.length;
            var localCount = localNames.length;

            // Count row
            var countChanged = sourceCount !== localCount;
            var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
            html += '<tr class="' + countRowClass + '">';
            html += '<td class="historyCompareTable-property">Count</td>';
            html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
            html += '<td class="historyCompareTable-value">' + localCount + '</td>';
            html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
            html += '</tr>';

            // Items row (show list of names)
            var sourceDisplay = sourceNames.length > 0 ? ServerSyncShared.escapeHtml(sourceNames.join(', ')) : '-';
            var localDisplay = localNames.length > 0 ? ServerSyncShared.escapeHtml(localNames.join(', ')) : '-';

            var sourceSorted = sourceNames.slice().sort().join(',');
            var localSorted = localNames.slice().sort().join(',');
            var itemsChanged = sourceSorted !== localSorted;
            var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

            html += '<tr class="' + itemsRowClass + '">';
            html += '<td class="historyCompareTable-property">Items</td>';
            html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
            html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
            html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
            html += '</tr>';

            return html;
        },

        formatMetadataValue: function(value, field) {
            // Handle booleans first (before isEmpty check, since false is a valid value)
            if (field.isBoolean) {
                if (value === true) return 'Yes';
                if (value === false) return 'No';
                return '-';
            }

            if (this.isEmpty(value)) return '-';

            if (field.isArray && Array.isArray(value)) {
                if (value.length === 0) return '-';
                return ServerSyncShared.escapeHtml(value.join(', '));
            }

            if (field.isDate) {
                return this.formatDateOnly(value);
            }

            if (field.truncate && typeof value === 'string' && value.length > 100) {
                return ServerSyncShared.escapeHtml(value.substring(0, 100) + '...');
            }

            return ServerSyncShared.escapeHtml(String(value));
        },

        formatDateOnly: function(dateStr) {
            if (!dateStr) return '-';
            try {
                var date = new Date(dateStr);
                // Return just the date portion to avoid timezone issues
                return date.toISOString().split('T')[0];
            } catch (e) {
                return ServerSyncShared.escapeHtml(String(dateStr));
            }
        },

        normalizeForComparison: function(value, field) {
            // Handle booleans first (false is a valid value, not empty)
            if (field.isBoolean) {
                if (value === true) return 'true';
                if (value === false) return 'false';
                return '';
            }

            if (this.isEmpty(value)) return '';

            if (field.isArray && Array.isArray(value)) {
                return value.sort().join(',');
            }

            if (field.isDate) {
                // Normalize dates to just the date portion
                try {
                    var date = new Date(value);
                    return date.toISOString().split('T')[0];
                } catch (e) {
                    return String(value);
                }
            }

            return String(value);
        },

        isEmpty: function(value) {
            if (value === null || value === undefined) return true;
            if (value === '') return true;
            if (Array.isArray(value) && value.length === 0) return true;
            return false;
        },

        buildImagesRows: function(sourceImages, localImages, item) {
            var self = this;
            var html = '';

            // Collect all image types from both source and local
            var allTypes = new Set();
            if (sourceImages && typeof sourceImages === 'object') {
                Object.keys(sourceImages).forEach(function(type) { allTypes.add(type); });
            }
            if (localImages && typeof localImages === 'object') {
                Object.keys(localImages).forEach(function(type) { allTypes.add(type); });
            }

            // Show each image type as a row
            var typesArray = Array.from(allTypes).sort();
            typesArray.forEach(function(imageType) {
                var srcImgs = sourceImages && sourceImages[imageType] ? sourceImages[imageType] : [];
                var localImgs = localImages && localImages[imageType] ? localImages[imageType] : [];

                var srcCount = Array.isArray(srcImgs) ? srcImgs.length : 0;
                var localCount = Array.isArray(localImgs) ? localImgs.length : 0;

                // Calculate size for source images (if available)
                var srcSize = 0;
                if (Array.isArray(srcImgs)) {
                    srcImgs.forEach(function(img) {
                        if (img && img.Size) srcSize += img.Size;
                    });
                }

                // Calculate size for local images
                var localSize = 0;
                if (Array.isArray(localImgs)) {
                    localImgs.forEach(function(img) {
                        if (img && img.Size) localSize += img.Size;
                    });
                }

                // Format: size (count) - hide count if 0 or 1
                var srcDisplay = self.formatImageDisplay(srcSize, srcCount);
                var localDisplay = self.formatImageDisplay(localSize, localCount);

                // Changed if count differs OR size differs (different image)
                var isChanged = srcCount !== localCount || srcSize !== localSize;
                var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + rowClass + '">';
                html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(imageType) + '</td>';
                html += '<td class="historyCompareTable-value">' + srcDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + srcDisplay + '</td>';
                html += '</tr>';
            });

            if (typesArray.length === 0) {
                html += '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No images</td></tr>';
            }

            return html;
        },

        formatImageDisplay: function(size, count) {
            if (count === 0) return '-';
            // If size is 0 or not available, just show count
            if (!size || size === 0) {
                return count === 1 ? '1 image' : count + ' images';
            }
            var sizeStr = this.formatBytes(size);
            // Only show count if more than 1 image
            if (count > 1) {
                return sizeStr + ' (' + count + ')';
            }
            return sizeStr;
        },

        formatBytes: function(bytes) {
            if (!bytes || bytes === 0) return '0 B';
            var units = ['B', 'KB', 'MB', 'GB'];
            var i = 0;
            while (bytes >= 1024 && i < units.length - 1) {
                bytes /= 1024;
                i++;
            }
            return bytes.toFixed(i > 0 ? 1 : 0) + ' ' + units[i];
        },

        closeModal: function() {
            view.querySelector('#metadataSyncItemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            // Refresh table and status on modal close
            this.table.refresh();
            this.loadMetadataStatus();
            this.loadHealthStats();
        },

        modalIgnore: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Ignored');
            }
        },

        modalQueue: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Queued');
            }
        },

        updateItemStatus: function(itemId, status) {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/UpdateStatus', 'POST', { Id: itemId, Status: status }).then(function() {
                self.closeModal();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        }
    };


    // ============================================
    // PAGE CONTROLLER
    // ============================================

    var MetadataPageController = {
        currentConfig: null,

        init: function() {
            var self = this;
            self.loadConfig();
        },

        loadConfig: function() {
            var self = this;

            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                // Initialize metadata sync table module
                MetadataSyncTableModule.currentConfig = config;
                MetadataSyncTableModule.init(config);

                // Load initial data
                MetadataSyncTableModule.loadMetadataStatus();
                MetadataSyncTableModule.loadMetadataItems();
                MetadataSyncTableModule.loadHealthStats();
            });
        }
    };


    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        console.log('ServerSync Metadata: viewshow event fired');

        // Set up Jellyfin tabs - index 3 for Metadata tab
        LibraryMenu.setTabs('serversync', 3, getTabs);

        // Initialize the page
        MetadataPageController.init();
    });

    view.addEventListener('viewhide', function () {
        console.log('ServerSync Metadata: viewhide event fired');
    });
}
