// ============================================
// SERVER SYNC PLUGIN - CONTENT PAGE CONTROLLER
// ============================================

// ============================================
// TAB NAVIGATION
// ============================================

function getTabs() {
    return [
        {
            href: 'configurationpage?name=serversync_settings',
            name: 'Settings'
        },
        {
            href: 'configurationpage?name=serversync_content',
            name: 'Content'
        },
        {
            href: 'configurationpage?name=serversync_history',
            name: 'History'
        },
        {
            href: 'configurationpage?name=serversync_metadata',
            name: 'Metadata'
        },
        {
            href: 'configurationpage?name=serversync_users',
            name: 'Users'
        }
    ];
}

export default function (view, params) {
    'use strict';

    // ============================================
    // SHARED UTILITIES
    // ============================================

    var ServerSyncShared = {
        pluginId: 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286',

        // Format bytes to human readable size
        formatSize: function(bytes) {
            if (!bytes) return '0 B';
            var units = ['B', 'KB', 'MB', 'GB', 'TB'];
            var i = 0;
            while (bytes >= 1024 && i < units.length - 1) {
                bytes /= 1024;
                i++;
            }
            return bytes.toFixed(i > 0 ? 2 : 0) + ' ' + units[i];
        },

        // Format relative time (e.g., "5 minutes ago")
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

        // Get filename from path
        getFileName: function(path) {
            if (!path) return '';
            return path.split('/').pop().split('\\').pop();
        },

        // Escape HTML special characters
        escapeHtml: function(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        },

        // Show alert using Dashboard
        showAlert: function(message) {
            Dashboard.alert(message);
        },

        // Get plugin configuration
        getConfig: function() {
            return ApiClient.getPluginConfiguration(this.pluginId);
        },

        // Make API request to plugin endpoint
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

        // Set element visibility
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

        // Safe event binding
        bindEvent: function(id, event, handler, moduleName) {
            var el = view.querySelector('#' + id);
            if (el) {
                el.addEventListener(event, handler);
            } else if (moduleName) {
                console.error(moduleName + ': #' + id + ' not found');
            }
            return el;
        },

        // Safe click binding shorthand
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

        // --------------------------------------------
        // Private Methods
        // --------------------------------------------

        _getItemId: function(item) {
            var idKey = this.options.selection && this.options.selection.idKey || 'id';
            if (typeof idKey === 'function') {
                return idKey(item);
            }
            return item[idKey];
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

        _init: function() {
            this._createStructure();
            this._bindEvents();
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

        // --------------------------------------------
        // Public API
        // --------------------------------------------

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
    // SYNC TABLE MODULE
    // ============================================

    var SyncTableModule = {
        table: null,
        currentModalItem: null,
        capabilities: null,
        _initialized: false,

        // --------------------------------------------
        // Initialization
        // --------------------------------------------

        init: function() {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;

            this.table = new PaginatedTable({
                containerId: 'syncItemsTableContainer',
                endpoint: 'Items',

                columns: [
                    {
                        key: 'name',
                        label: 'Item',
                        type: 'custom',
                        render: function(item) {
                            var fileName = ServerSyncShared.getFileName(item.SourcePath);
                            var sourceLibrary = item.SourceLibraryName || 'Source';
                            var localLibrary = item.LocalLibraryName || 'Local';
                            var libraryDisplay = sourceLibrary + ' → ' + localLibrary;

                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' +
                                    ServerSyncShared.escapeHtml(item.SourcePath) + '">' +
                                    ServerSyncShared.escapeHtml(fileName) + '</div>' +
                                '<div class="syncItemPath">' +
                                    ServerSyncShared.escapeHtml(libraryDisplay) + '</div>' +
                                errorPreview +
                            '</div>';
                        }
                    },
                    {
                        key: 'details',
                        label: 'Details',
                        type: 'custom',
                        render: function(item) {
                            if (item.Status === 'Synced') {
                                return '<span style="opacity: 0.5;">No changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.SourceSizeFormatted || '');
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
                    idKey: 'SourceItemId',
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
                        { value: 'Ignored', label: 'Ignored' },
                        { value: 'Pending', label: 'Pending' },
                        { value: 'Pending:Download', label: 'Pending Download', id: 'optPendingDownload', hidden: true },
                        { value: 'Pending:Replacement', label: 'Pending Replace', id: 'optPendingReplacement', hidden: true },
                        { value: 'Pending:Deletion', label: 'Pending Delete', id: 'optPendingDeletion', hidden: true },
                        { value: 'Deleting', label: 'Deleting', id: 'optDeleting', hidden: true }
                    ],
                    buildParams: function(filterValue) {
                        if (filterValue.indexOf(':') > -1) {
                            var parts = filterValue.split(':');
                            return { status: parts[0], pendingType: parts[1] };
                        }
                        return { status: filterValue };
                    }
                },

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.SourceItemId);
                    },
                    onReload: function() {
                        self.loadSyncStatus();
                        self.loadHealthStats();
                    }
                },

                getDisplayStatus: function(item) {
                    if (item.Status === 'Pending' && item.PendingType) {
                        return 'Pending ' + item.PendingType;
                    }
                    return item.Status;
                },

                getStatusClass: function(item) {
                    if (item.Status === 'Pending' && item.PendingType) {
                        return 'Pending-' + item.PendingType;
                    }
                    return item.Status;
                },

                emptyState: {
                    message: 'No items found'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'SyncTableModule'); };

            // Action buttons
            bind('btnRefreshItems', function() { self.refreshSyncTable(); });
            bind('btnTriggerSync', function() { self.triggerSync(); });
            bind('btnRetryErrors', function() { self.retryErrors(); });
            bind('btnResetDatabase', function() { self.resetDatabase(); });

            // Modal buttons
            bind('btnModalIgnore', function() { self.modalIgnore(); });
            bind('btnModalQueue', function() { self.modalQueue(); });
            bind('btnModalDelete', function() { self.modalDelete(); });
            bind('btnModalClose', function() { self.closeModal(); });
        },

        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnBulkIgnore" class="raised" disabled><span>Ignore</span></button>' +
                '<button is="emby-button" type="button" id="btnBulkQueue" class="raised button-primary" disabled><span>Queue</span></button>' +
                '<button is="emby-button" type="button" id="btnBulkDelete" class="raised button-destructive" title="Delete from local server only" disabled><span>Delete</span></button>';

            bulkContainer.querySelector('#btnBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            bulkContainer.querySelector('#btnBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
            bulkContainer.querySelector('#btnBulkDelete').addEventListener('click', function() { self.bulkDelete(); });
        },

        // Backward compatibility properties
        get syncItems() {
            return this.table ? this.table.getItems() : [];
        },

        get filteredItems() {
            return this.table ? this.table.getItems() : [];
        },

        get selectedItems() {
            return this.table ? new Set(this.table.getSelectedIds()) : new Set();
        },

        // --------------------------------------------
        // Capabilities
        // --------------------------------------------

        loadCapabilities: function() {
            var self = this;
            return ServerSyncShared.apiRequest('Capabilities', 'GET').then(function(capabilities) {
                self.capabilities = capabilities;
                self.updateDeleteCapabilityVisibility(capabilities.CanDeleteItems);
            }).catch(function() {
                self.capabilities = { CanDeleteItems: false };
                self.updateDeleteCapabilityVisibility(false);
            });
        },

        updateDeleteCapabilityVisibility: function(canDelete) {
            var bulkDeleteBtn = view.querySelector('#btnBulkDelete');
            var modalDeleteRow = view.querySelector('#modalDeleteRow');

            if (bulkDeleteBtn) {
                bulkDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
            }
            if (modalDeleteRow) {
                modalDeleteRow.style.display = canDelete ? 'block' : 'none';
            }
        },

        // --------------------------------------------
        // Data Loading
        // --------------------------------------------

        loadHealthStats: function() {
            return Promise.all([
                ServerSyncShared.apiRequest('Stats', 'GET'),
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('PendingSize', 'GET')
            ]).then(function(results) {
                var stats = results[0];
                var config = results[1];
                var pendingSizeData = results[2];

                var lastSyncEl = view.querySelector('#healthLastSync');
                if (stats.LastSyncEndTime) {
                    var lastSync = new Date(stats.LastSyncEndTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                var libraryCountEl = view.querySelector('#healthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';

                var pendingCountEl = view.querySelector('#healthPendingCount');
                if (pendingSizeData && typeof pendingSizeData.TotalPendingBytes === 'number') {
                    pendingCountEl.textContent = ServerSyncShared.formatSize(pendingSizeData.TotalPendingBytes);
                    pendingCountEl.className = pendingSizeData.TotalPendingBytes > 0 ? 'healthValue warning' : 'healthValue';
                } else {
                    pendingCountEl.textContent = '0 B';
                    pendingCountEl.className = 'healthValue';
                }
            }).catch(function() {
                // Ignore errors
            });
        },

        loadSyncStatus: function() {
            return ServerSyncShared.apiRequest('Status', 'GET').then(function(status) {
                var syncedCount = status.Synced || 0;
                var queuedCount = status.Queued || 0;
                var erroredCount = status.Errored || 0;
                var ignoredCount = status.Ignored || 0;
                var pendingDownloadCount = status.PendingDownload || 0;
                var pendingReplacementCount = status.PendingReplacement || 0;
                var pendingDeletionCount = status.PendingDeletion || 0;
                var deletingCount = status.Deleting || 0;

                var totalPendingCount = pendingDownloadCount + pendingReplacementCount + pendingDeletionCount;

                view.querySelector('#syncedCount').textContent = syncedCount;
                view.querySelector('#statusGroupSynced').setAttribute('title', 'Synced: ' + syncedCount);

                view.querySelector('#pendingCount').textContent = totalPendingCount;
                view.querySelector('#statusGroupPending').setAttribute('title', 'Pending: ' + totalPendingCount + ' (Download: ' + pendingDownloadCount + ', Replace: ' + pendingReplacementCount + ', Delete: ' + pendingDeletionCount + ')');

                view.querySelector('#queuedCount').textContent = queuedCount;
                view.querySelector('#statusGroupQueued').setAttribute('title', 'Queued: ' + queuedCount);

                view.querySelector('#erroredCount').textContent = erroredCount;
                view.querySelector('#statusGroupErrored').setAttribute('title', 'Errored: ' + erroredCount);

                view.querySelector('#ignoredCount').textContent = ignoredCount;
                view.querySelector('#statusGroupIgnored').setAttribute('title', 'Ignored: ' + ignoredCount);

                view.querySelector('#pendingDownloadCount').textContent = pendingDownloadCount;
                view.querySelector('#statusGroupPendingDownload').setAttribute('title', 'Pending Download: ' + pendingDownloadCount);

                view.querySelector('#pendingReplacementCount').textContent = pendingReplacementCount;
                view.querySelector('#statusGroupPendingReplacement').setAttribute('title', 'Pending Replacement: ' + pendingReplacementCount);

                view.querySelector('#pendingDeletionCount').textContent = pendingDeletionCount;
                view.querySelector('#statusGroupPendingDeletion').setAttribute('title', 'Pending Deletion: ' + pendingDeletionCount);

                view.querySelector('#deletingCount').textContent = deletingCount;
                view.querySelector('#statusGroupDeleting').setAttribute('title', 'Deleting: ' + deletingCount);
            });
        },

        loadSyncItems: function() {
            return this.table.reload();
        },

        // --------------------------------------------
        // Action Handlers
        // --------------------------------------------

        triggerSync: function() {
            var btn = view.querySelector('#btnTriggerSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerSync', 'POST').then(function() {
                ServerSyncShared.showAlert('Sync task started');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        refreshSyncTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Refreshing...';

            ServerSyncShared.apiRequest('TriggerRefresh', 'POST').then(function() {
                ServerSyncShared.showAlert('Refresh task started');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
                self.loadSyncStatus();
                self.loadSyncItems();
                self.loadHealthStats();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('RetryErroredItems', 'POST', {}).then(function() {
                ServerSyncShared.showAlert('Errored items queued for retry');
                self.loadSyncStatus();
                self.loadSyncItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        resetDatabase: function() {
            var self = this;

            if (!confirm('Are you sure you want to reset the sync database?\n\nThis will delete ALL tracking data and you will need to re-sync everything. This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('ResetSyncDatabase', 'POST').then(function(response) {
                console.log('ResetSyncDatabase response:', response);
                ServerSyncShared.showAlert('Sync database has been reset');
                self.loadSyncStatus();
                self.loadSyncItems().then(function() {
                    console.log('loadSyncItems completed');
                }).catch(function(err) {
                    console.error('loadSyncItems error:', err);
                });
                self.loadHealthStats();
            }).catch(function(err) {
                console.error('ResetSyncDatabase error:', err);
                ServerSyncShared.showAlert('Failed to reset sync database');
            });
        },

        // --------------------------------------------
        // Bulk Actions
        // --------------------------------------------

        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnBulkIgnore');
            var queueBtn = view.querySelector('#btnBulkQueue');
            var deleteBtn = view.querySelector('#btnBulkDelete');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
            if (deleteBtn) deleteBtn.disabled = !hasSelection;
        },

        bulkIgnore: function() {
            this.bulkAction('IgnoreItems');
        },

        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            var items = this.table.getItems();
            var filteredIds = ids.filter(function(id) {
                var item = items.find(function(i) { return i.SourceItemId === id; });
                if (item && item.Status === 'Pending' && item.PendingType === 'Deletion') {
                    return false;
                }
                return true;
            });

            if (filteredIds.length === 0) {
                ServerSyncShared.showAlert('No items to queue (deletion items cannot be queued)');
                return;
            }

            ServerSyncShared.apiRequest('QueueItems', 'POST', { SourceItemIds: filteredIds }).then(function() {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert(filteredIds.length + ' item(s) queued');
            }).catch(function(err) {
                console.error('Bulk queue failed:', err);
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        bulkDelete: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            if (!confirm('Delete ' + ids.length + ' item(s) from the LOCAL server? This cannot be undone.\n\nNote: This only deletes from this local server, never from the source server.')) {
                return;
            }

            ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: ids }).then(function(result) {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                if (result && result.Deleted > 0) {
                    ServerSyncShared.showAlert('Deleted ' + result.Deleted + ' item(s)');
                }
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to delete items');
            });
        },

        bulkAction: function(endpoint) {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest(endpoint, 'POST', { SourceItemIds: ids }).then(function() {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) updated');
            }).catch(function(err) {
                console.error('Bulk action failed:', err);
                ServerSyncShared.showAlert('Failed to update items');
            });
        },

        // --------------------------------------------
        // Status Helpers
        // --------------------------------------------

        getDisplayStatus: function(item) {
            if (item.Status === 'Pending' && item.PendingType) {
                return 'Pending ' + item.PendingType;
            }
            return item.Status;
        },

        getStatusClass: function(item) {
            if (item.Status === 'Pending' && item.PendingType) {
                return 'Pending-' + item.PendingType;
            }
            return item.Status;
        },

        // --------------------------------------------
        // Modal: Item Detail
        // --------------------------------------------

        showItemDetail: function(sourceItemId) {
            var self = this;
            var items = this.table.getItems();
            var item = items.find(function(i) { return i.SourceItemId === sourceItemId; });
            if (!item) return;

            self.currentModalItem = item;

            // Title
            view.querySelector('#modalTitle').textContent = ServerSyncShared.getFileName(item.SourcePath);

            // Status badge
            var statusBadge = view.querySelector('#modalStatusBadge');
            var displayStatus = self.getDisplayStatus(item);
            var statusClass = self.getStatusClass(item);
            statusBadge.textContent = displayStatus;
            statusBadge.className = 'itemModal-statusBadge ' + statusClass;

            // Library mapping
            var sourceLibrary = item.SourceLibraryName || 'Source';
            var localLibrary = item.LocalLibraryName || 'Local';
            view.querySelector('#modalLibraryMapping').textContent = sourceLibrary + ' → ' + localLibrary;

            // Size
            view.querySelector('#modalSize').textContent = ServerSyncShared.formatSize(item.SourceSize);

            // Paths
            view.querySelector('#modalSourcePath').textContent = item.SourcePath;

            // Error message
            var errorSection = view.querySelector('#modalErrorSection');
            if (item.Status === 'Errored' && item.ErrorMessage) {
                view.querySelector('#modalError').textContent = item.ErrorMessage;
                errorSection.classList.remove('hidden');
            } else {
                errorSection.classList.add('hidden');
            }

            // Retry count
            var retrySection = view.querySelector('#modalRetrySection');
            if (item.RetryCount > 0) {
                view.querySelector('#modalRetryCount').textContent = item.RetryCount + ' attempt' + (item.RetryCount > 1 ? 's' : '');
                retrySection.classList.remove('hidden');
            } else {
                retrySection.classList.add('hidden');
            }

            // Last sync time
            var lastSyncSection = view.querySelector('#modalLastSyncSection');
            if (item.LastSyncTime) {
                var lastSync = new Date(item.LastSyncTime);
                view.querySelector('#modalLastSync').textContent = ServerSyncShared.formatRelativeTime(lastSync);
                lastSyncSection.classList.remove('hidden');
            } else {
                lastSyncSection.classList.add('hidden');
            }

            // Companion files
            var companionSection = view.querySelector('#modalCompanionFilesSection');
            if (item.CompanionFiles) {
                var companionList = item.CompanionFiles.split(',').map(function(f) {
                    return f.trim();
                }).filter(function(f) {
                    return f.length > 0;
                });
                if (companionList.length > 0) {
                    view.querySelector('#modalCompanionFiles').innerHTML = companionList.map(function(f) {
                        return '<div class="itemModal-companionItem">' +
                            '<span class="itemModal-companionIcon">&#128196;</span>' +
                            '<span class="itemModal-companionName">' + ServerSyncShared.escapeHtml(f) + '</span>' +
                            '</div>';
                    }).join('');
                    companionSection.classList.remove('hidden');
                } else {
                    companionSection.classList.add('hidden');
                }
            } else {
                companionSection.classList.add('hidden');
            }

            // Local path
            var localPathEl = view.querySelector('#modalLocalPath');
            var localPathNoteEl = view.querySelector('#modalLocalPathNote');
            var localExists = item.Status === 'Synced';
            if (item.LocalPath) {
                localPathEl.textContent = item.LocalPath;
                if (localExists) {
                    localPathNoteEl.textContent = '';
                    localPathNoteEl.style.display = 'none';
                } else {
                    localPathNoteEl.textContent = 'File will be synced to this location';
                    localPathNoteEl.style.display = 'block';
                }
            } else {
                localPathEl.textContent = 'N/A';
                localPathNoteEl.style.display = 'none';
            }

            // Show/hide buttons based on status
            var btnQueue = view.querySelector('#btnModalQueue');
            var btnIgnore = view.querySelector('#btnModalIgnore');
            var modalDeleteRow = view.querySelector('#modalDeleteRow');
            var isPendingDeletion = item.Status === 'Pending' && item.PendingType === 'Deletion';
            var isPendingDownloadOrReplacement = item.Status === 'Pending' && (item.PendingType === 'Download' || item.PendingType === 'Replacement');
            var isSynced = item.Status === 'Synced';

            var queueBtnSpan = btnQueue.querySelector('span');
            if (isSynced) {
                queueBtnSpan.textContent = 'Re-sync';
            } else {
                queueBtnSpan.textContent = 'Queue';
            }

            if (isPendingDeletion) {
                btnQueue.style.display = 'none';
                if (self.capabilities && self.capabilities.CanDeleteItems) {
                    modalDeleteRow.style.display = 'block';
                }
            } else if (isPendingDownloadOrReplacement) {
                btnQueue.style.display = 'inline-block';
                modalDeleteRow.style.display = 'none';
            } else {
                btnQueue.style.display = 'inline-block';
                if (self.capabilities && self.capabilities.CanDeleteItems) {
                    modalDeleteRow.style.display = 'block';
                }
            }

            view.querySelector('#itemDetailModal').classList.remove('hidden');
        },

        closeModal: function() {
            view.querySelector('#itemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            this.table.refresh();
            this.loadHealthStats();
        },

        modalIgnore: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.SourceItemId, 'Ignored');
            }
        },

        modalQueue: function() {
            if (this.currentModalItem) {
                if (this.currentModalItem.Status === 'Pending' && this.currentModalItem.PendingType === 'Deletion') {
                    return;
                }
                this.updateItemStatus(this.currentModalItem.SourceItemId, 'Queued');
            }
        },

        modalDelete: function() {
            var self = this;
            if (!this.currentModalItem) return;

            var fileName = ServerSyncShared.getFileName(this.currentModalItem.LocalPath || this.currentModalItem.SourcePath);
            if (!confirm('Delete "' + fileName + '" from the LOCAL server? This cannot be undone.\n\nNote: This only deletes from this local server, never from the source server.')) {
                return;
            }

            ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function() {
                self.closeModal();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert('Item deleted');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to delete item');
            });
        },

        updateItemStatus: function(sourceItemId, status) {
            var self = this;

            ServerSyncShared.apiRequest('UpdateItemStatus', 'POST', { SourceItemId: sourceItemId, Status: status }).then(function() {
                self.closeModal();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        },

        // --------------------------------------------
        // Filter Visibility
        // --------------------------------------------

        updatePendingFilterVisibility: function(config) {
            var downloadMode = config.DownloadNewContentMode || 'Enabled';
            var replaceMode = config.ReplaceExistingContentMode || 'Enabled';
            var deleteMode = config.DeleteMissingContentMode || 'Disabled';

            var showPendingDownload = downloadMode === 'RequireApproval';
            var showPendingReplace = replaceMode === 'RequireApproval';
            var showPendingDelete = deleteMode === 'RequireApproval';

            if (this.table) {
                this.table.setFilterOptionVisible('optPendingDownload', showPendingDownload);
                this.table.setFilterOptionVisible('optPendingReplacement', showPendingReplace);
                this.table.setFilterOptionVisible('optPendingDeletion', showPendingDelete);
                this.table.setFilterOptionVisible('optDeleting', showPendingDelete);
            }

            ServerSyncShared.setVisible('statusGroupPendingDownload', showPendingDownload);
            ServerSyncShared.setVisible('statusGroupPendingReplacement', showPendingReplace);
            ServerSyncShared.setVisible('statusGroupPendingDeletion', showPendingDelete);
            ServerSyncShared.setVisible('statusGroupDeleting', showPendingDelete);

            var showAnyPending = showPendingDownload || showPendingReplace || showPendingDelete;
            var pendingRow = view.querySelector('#pendingStatusRow');
            if (pendingRow) {
                pendingRow.style.display = showAnyPending ? 'flex' : 'none';
            }
        }
    };

    // ============================================
    // PAGE CONTROLLER
    // ============================================

    var ContentPageController = {
        init: function() {
            var self = this;

            SyncTableModule.init();
            SyncTableModule.loadCapabilities();

            ServerSyncShared.getConfig().then(function(config) {
                SyncTableModule.updatePendingFilterVisibility(config);
            });

            SyncTableModule.loadSyncStatus();
            SyncTableModule.loadSyncItems();
            SyncTableModule.loadHealthStats();
        }
    };

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        console.log('ServerSync Content: viewshow event fired');
        LibraryMenu.setTabs('serversync', 1, getTabs);
        ContentPageController.init();
    });

    view.addEventListener('viewhide', function () {
        console.log('ServerSync Content: viewhide event fired');
    });
}
