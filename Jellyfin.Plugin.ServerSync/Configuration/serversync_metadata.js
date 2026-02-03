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

        saveConfig: function(config) {
            return ApiClient.updatePluginConfiguration(this.pluginId, config);
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

        setVisible: function(elementOrId, visible) {
            var el = typeof elementOrId === 'string' ? view.querySelector('#' + elementOrId) : elementOrId;
            if (el) {
                if (visible) {
                    el.classList.remove('hidden');
                } else {
                    el.classList.add('hidden');
                }
            }
        },

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
                html += '<input is="emby-input" type="text" class="pt-search" placeholder="' +
                    ServerSyncShared.escapeHtml(opts.search.placeholder || 'Search...') + '" />';
            }
            if (opts.filters && opts.filters.options && opts.filters.options.length > 0) {
                html += '<select is="emby-select" class="pt-filter">';
                html += '<option value="">All</option>';
                opts.filters.options.forEach(function(opt) {
                    var style = opt.hidden ? ' style="display:none"' : '';
                    var id = opt.id ? ' id="' + opt.id + '"' : '';
                    html += '<option value="' + ServerSyncShared.escapeHtml(opt.value) + '"' + style + id + '>' +
                        ServerSyncShared.escapeHtml(opt.label) + '</option>';
                });
                html += '</select>';
            }
            html += '</div>';

            // Bottom row: Selection controls + Bulk actions + Reload button
            if (opts.selection && opts.selection.enabled) {
                html += '<div class="pt-selection-header">';
                html += '<label class="pt-select-all-container">';
                html += '<input type="checkbox" class="pt-select-all" />';
                html += '<span>Select All</span>';
                html += '</label>';
                html += '<span class="pt-selected-count">0 selected</span>';
                html += '<div class="pt-bulk-actions"></div>';
                html += '<span class="pt-header-spacer"></span>';
                html += '<button is="emby-button" type="button" class="pt-reload-btn" title="Reload table data">';
                html += '<span class="material-icons pt-reload-icon">refresh</span>';
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

                            return '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' + ServerSyncShared.escapeHtml(itemName) + '">' +
                                ServerSyncShared.escapeHtml(itemName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(libraryDisplay) + '</div>' +
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
                '<button is="emby-button" type="button" id="btnMetadataBulkIgnore" class="raised" disabled><span>Ignore</span></button>' +
                '<button is="emby-button" type="button" id="btnMetadataBulkQueue" class="raised button-primary" disabled><span>Queue</span></button>';

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

                // Pending count from status
                var pendingCountEl = view.querySelector('#metadataHealthPendingCount');
                var pending = (status.Queued || 0);
                pendingCountEl.textContent = pending;
                pendingCountEl.className = pending > 0 ? 'healthValue warning' : 'healthValue';
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
            btn.querySelector('span').textContent = 'Refreshing...';

            ServerSyncShared.apiRequest('TriggerMetadataRefresh', 'POST').then(function() {
                ServerSyncShared.showAlert('Metadata refresh task started');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
                self.loadMetadataStatus();
                self.loadMetadataItems();
                self.loadHealthStats();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start metadata refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        triggerMetadataSync: function() {
            var btn = view.querySelector('#btnTriggerMetadataSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerMetadataSync', 'POST').then(function() {
                ServerSyncShared.showAlert('Metadata sync task started');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
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
                view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || 'Unknown Item';

                // Status badge
                var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                statusBadge.textContent = item.Status;
                statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                // Error section
                var errorSection = view.querySelector('#metadataSyncModalErrorSection');
                if (item.Status === 'Errored' && item.ErrorMessage) {
                    view.querySelector('#metadataSyncModalError').textContent = item.ErrorMessage;
                    errorSection.classList.remove('hidden');
                } else {
                    errorSection.classList.add('hidden');
                }

                // Item info
                var sourceLib = item.SourceLibraryName || 'Unknown';
                var localLib = item.LocalLibraryName || 'Unknown';
                view.querySelector('#metadataSyncModalItem').innerHTML =
                    '<strong>Library:</strong> ' + ServerSyncShared.escapeHtml(sourceLib) + ' → ' + ServerSyncShared.escapeHtml(localLib);

                // Set server names in table headers
                var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#metadataSyncModalSourceHeader').textContent = sourceServerName;
                view.querySelector('#metadataSyncModalLocalHeader').textContent = localServerName;

                // Build property comparison table
                self.buildPropertyTable(item);

                // Last sync
                var lastSyncSection = view.querySelector('#metadataSyncModalLastSyncSection');
                if (item.LastSyncTime) {
                    view.querySelector('#metadataSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                    lastSyncSection.classList.remove('hidden');
                } else {
                    lastSyncSection.classList.add('hidden');
                }

                view.querySelector('#metadataSyncItemDetailModal').classList.remove('hidden');
            }).catch(function(err) {
                console.error('Failed to load metadata item details:', err);
                ServerSyncShared.showAlert('Failed to load item details');
            });
        },

        buildPropertyTable: function(item) {
            var tbody = view.querySelector('#metadataSyncModalTableBody');
            var html = '';

            var properties = item.Properties || [];

            if (properties.length === 0) {
                html = '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No property data available</td></tr>';
            } else {
                properties.forEach(function(prop) {
                    var isChanged = prop.SourceValue !== prop.LocalValue;
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(prop.Name) + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(prop.SourceValue || '-') + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(prop.LocalValue || '-') + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' +
                        ServerSyncShared.escapeHtml(prop.MergedValue || prop.SourceValue || '-') + '</td>';
                    html += '</tr>';
                });
            }

            tbody.innerHTML = html;
        },

        closeModal: function() {
            view.querySelector('#metadataSyncItemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
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
