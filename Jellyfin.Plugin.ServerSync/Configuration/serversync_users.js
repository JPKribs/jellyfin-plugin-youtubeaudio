// Server Sync Plugin - Users Page Controller
// This file uses the Jellyfin multi-page plugin pattern

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

    // Shared utilities for Server Sync plugin configuration
    var ServerSyncShared = {
        pluginId: 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286',

        // Local server name (fetched from system info)
        localServerName: null,

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

        // Save plugin configuration
        saveConfig: function(config) {
            return ApiClient.updatePluginConfiguration(this.pluginId, config);
        },

        // Fetch local server name from system info
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

        // Safe event binding - binds event only if element exists (uses view.querySelector)
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


    // PaginatedTable Component
    // Generic table component with infinite scroll, search, filter, and bulk actions
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

        // _getItemId
        // Gets the ID for an item. Supports idKey as string (property name) or function.
        _getItemId: function(item) {
            var idKey = this.options.selection && this.options.selection.idKey || 'id';
            if (typeof idKey === 'function') {
                return idKey(item);
            }
            return item[idKey];
        },

        // _mergeOptions
        // Deep merges user-provided options with defaults.
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

        // _init
        // Initializes the component by creating DOM structure and binding events.
        _init: function() {
            this._createStructure();
            this._bindEvents();
        },

        // _createStructure
        // Builds and injects the table HTML into the container element.
        _createStructure: function() {
            var container = view.querySelector('#' + this.options.containerId);
            if (!container) {
                console.error('PaginatedTable: Container not found:', this.options.containerId);
                return;
            }
            container.innerHTML = this._buildHTML();
            this._cacheElements(container);
        },

        // _cacheElements
        // Stores references to frequently accessed DOM elements.
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

        // _buildHTML
        // Generates the complete HTML structure for the table component.
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

        // _bindEvents
        // Attaches event listeners to interactive elements.
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

        // _handleSearchInput
        // Debounces search input to avoid excessive API calls.
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

        // _handleScroll
        // Triggers loading more items when scrolled near the bottom.
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

        // _handleReload
        // Resets state and reloads all data from the first page.
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

        // reload
        // Public method to reset state and reload from page 1.
        reload: function() {
            console.log('PaginatedTable reload called, clearing state');
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.state.selectedIds.clear();
            return this.load();
        },

        // load
        // Fetches items from the API for the current page.
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
                    // Custom filter param builder
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

        // _loadMore
        // Increments page and loads the next batch of items.
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

        // _setLoading
        // Toggles the loading state visual indicator.
        _setLoading: function(loading) {
            if (this.elements.container) {
                // Only show loading overlay on first page load
                if (loading && this.state.currentPage === 1) {
                    this.elements.container.classList.add('pt-loading');
                } else {
                    this.elements.container.classList.remove('pt-loading');
                }
            }
        },

        // _render
        // Updates all rendered portions of the table.
        _render: function() {
            this._renderBody();
            this._updateItemCount();
            this._updateSelectionUI();
        },

        // _renderBody
        // Renders all table rows or empty state message.
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

        // _renderRow
        // Generates HTML for a single table row.
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

        // _getDisplayStatus
        // Returns the display text for a status value.
        _getDisplayStatus: function(item, value) {
            if (this.options.getDisplayStatus) {
                return this.options.getDisplayStatus(item, value);
            }
            return value || '';
        },

        // _getStatusClass
        // Returns the CSS class for a status value.
        _getStatusClass: function(item, value) {
            if (this.options.getStatusClass) {
                return this.options.getStatusClass(item, value);
            }
            return value || '';
        },

        // _bindRowEvents
        // Attaches click and change handlers to row elements.
        _bindRowEvents: function() {
            var self = this;
            var body = this.elements.body;
            if (!body) return;

            body.querySelectorAll('.pt-row').forEach(function(row) {
                var handleRowAction = function(e) {
                    // Skip if clicking checkbox or status button (handled separately)
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

            // Status badge buttons - explicit tap target for mobile
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

                // Prevent row click when clicking checkbox
                checkbox.addEventListener('click', function(e) {
                    e.stopPropagation();
                });
            });
        },

        // _getItemById
        // Finds an item in the current items array by its ID.
        _getItemById: function(id) {
            var self = this;
            return this.state.items.find(function(item) {
                return String(self._getItemId(item)) === String(id);
            });
        },

        // _updateItemCount
        // Updates the footer text showing loaded vs total item counts.
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

        // _updateSelectionUI
        // Syncs the selection count display and select-all checkbox state.
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

        // _toggleSelectAll
        // Selects or deselects all currently loaded items.
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

        // _notifySelectionChange
        // Invokes the selection change callback if configured.
        _notifySelectionChange: function() {
            if (this.options.selection && this.options.selection.onSelectionChange) {
                this.options.selection.onSelectionChange(this.getSelectedIds());
            }
        },

        // ========================================
        // Public API
        // ========================================

        // getSelectedIds
        // Returns an array of selected item IDs.
        getSelectedIds: function() {
            return Array.from(this.state.selectedIds);
        },

        // getSelectedItems
        // Returns an array of selected item objects.
        getSelectedItems: function() {
            var self = this;
            return this.state.items.filter(function(item) {
                return self.state.selectedIds.has(String(self._getItemId(item)));
            });
        },

        // clearSelection
        // Clears all selected items and updates UI.
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

        // refresh
        // Resets pagination and reloads data from the beginning.
        refresh: function() {
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            return this.load();
        },

        // setFilter
        // Applies a filter value and reloads data.
        setFilter: function(value) {
            this.state.filterValue = value;
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        // setSearch
        // Applies a search query and reloads data.
        setSearch: function(query) {
            this.state.searchQuery = query;
            this.state.items = [];
            this.state.currentPage = 1;
            this.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        // getItems
        // Returns all currently loaded items.
        getItems: function() {
            return this.state.items;
        },

        // getTotalCount
        // Returns the total number of items available on the server.
        getTotalCount: function() {
            return this.state.totalCount;
        },

        // getBulkActionsContainer
        // Returns the DOM element for injecting bulk action buttons.
        getBulkActionsContainer: function() {
            return this.elements.bulkActions;
        },

        // setFilterOptionVisible
        // Shows or hides a specific filter dropdown option.
        setFilterOptionVisible: function(optionId, visible) {
            if (this.elements.filter) {
                var option = this.elements.filter.querySelector('#' + optionId);
                if (option) {
                    option.style.display = visible ? 'block' : 'none';
                }
            }
        },

        // setFilterValue
        // Sets the filter value without triggering a reload.
        setFilterValue: function(value) {
            this.state.filterValue = value;
            if (this.elements.filter) {
                this.elements.filter.value = value;
            }
        }
    };


    // User Sync Table Module
    // Uses the generic PaginatedTable component for user sync items
    var UserSyncTableModule = {
        table: null,
        currentModalDetail: null,
        currentConfig: null,

        init: function(config) {
            var self = this;
            self.currentConfig = config;

            // Create the paginated table instance (consolidated view - 1 row per user)
            this.table = new PaginatedTable({
                containerId: 'userSyncItemsTableContainer',
                endpoint: 'UserSyncUsers',

                columns: [
                    {
                        key: 'user',
                        label: 'User',
                        type: 'custom',
                        render: function(item) {
                            var sourceUserName = item.SourceUserName || 'Unknown';
                            var localUserName = item.LocalUserName || 'Unknown';
                            var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                            var localServerName = ServerSyncShared.localServerName || 'Local';

                            return '<div class="syncItemInfo">' +
                                '<div class="syncItemName">' + ServerSyncShared.escapeHtml(sourceUserName) + ' -> ' + ServerSyncShared.escapeHtml(localUserName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(sourceServerName) + ' -> ' + ServerSyncShared.escapeHtml(localServerName) + '</div>' +
                                '</div>';
                        }
                    },
                    {
                        key: 'changes',
                        label: 'Changes',
                        type: 'custom',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No Changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.TotalChanges || 'Changes pending');
                        }
                    },
                    {
                        key: 'OverallStatus',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: function(item) {
                        return item.SourceUserId + '|' + item.LocalUserId;
                    },
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
                    placeholder: 'Search users...'
                },

                actions: {
                    onRowClick: function(item) {
                        self.showUserDetail(item);
                    },
                    onReload: function() {
                        self.loadUserStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No user sync items found. Run a refresh to scan for user data.'
                }
            });

            // Bind module-specific events
            this._bindModuleEvents();

            // Inject bulk action buttons
            this._injectBulkActions();
        },

        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'UserSyncTableModule'); };

            // Action buttons
            bind('btnRefreshUserItems', function() { self.triggerRefresh(); });
            bind('btnTriggerUserSync', function() { self.triggerSync(); });
            bind('btnRetryUserErrors', function() { self.retryErrors(); });
            bind('btnResetUserDatabase', function() { self.resetUserDatabase(); });

            // Modal buttons
            bind('btnUserSyncModalIgnore', function() { self.modalIgnore(); });
            bind('btnUserSyncModalQueue', function() { self.modalQueue(); });
            bind('btnUserSyncModalClose', function() { self.closeModal(); });
        },

        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnUserBulkIgnore" class="raised" disabled><span>Ignore</span></button>' +
                '<button is="emby-button" type="button" id="btnUserBulkQueue" class="raised button-primary" disabled><span>Queue</span></button>';

            view.querySelector('#btnUserBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnUserBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        loadUserStatus: function() {
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                view.querySelector('#userSyncedCount').textContent = status.Synced || 0;
                view.querySelector('#userQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#userErroredCount').textContent = status.Errored || 0;
                view.querySelector('#userIgnoredCount').textContent = status.Ignored || 0;
            }).catch(function(err) {
                console.log('User status not available:', err);
            });
        },

        loadHealthStats: function() {
            var self = this;
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                if (status) {
                    // Last sync time
                    var lastSyncEl = view.querySelector('#userHealthLastSync');
                    if (status.LastSyncTime) {
                        lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(new Date(status.LastSyncTime));
                        lastSyncEl.className = 'healthValue success';
                    } else {
                        lastSyncEl.textContent = 'Never';
                        lastSyncEl.className = 'healthValue';
                    }

                    // Users mapped (sum of all status counts = total unique users)
                    var userCount = (status.Synced || 0) + (status.Queued || 0) + (status.Errored || 0) + (status.Ignored || 0);
                    var userCountEl = view.querySelector('#userHealthUserCount');
                    userCountEl.textContent = userCount;
                    userCountEl.className = userCount > 0 ? 'healthValue success' : 'healthValue warning';

                    // Pending count
                    var pending = (status.Queued || 0);
                    var pendingCountEl = view.querySelector('#userHealthPendingCount');
                    pendingCountEl.textContent = pending;
                    pendingCountEl.className = pending > 0 ? 'healthValue warning' : 'healthValue';
                }
            }).catch(function() {
                // Ignore errors
            });
        },

        loadUserItems: function() {
            return this.table.reload();
        },

        triggerRefresh: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshUserItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Refreshing...';

            ServerSyncShared.apiRequest('TriggerUserRefresh', 'POST').then(function() {
                ServerSyncShared.showAlert('User refresh task started');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
                self.loadUserStatus();
                self.loadUserItems();
                self.loadHealthStats();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start user refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        triggerSync: function() {
            var btn = view.querySelector('#btnTriggerUserSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerUserSync', 'POST').then(function() {
                ServerSyncShared.showAlert('User sync task started');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start user sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('UserItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored user items queued for retry');
                self.loadUserStatus();
                self.loadUserItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        resetUserDatabase: function() {
            var self = this;

            if (!confirm('Are you sure you want to reset the user sync database?\n\nThis will delete ALL user sync tracking data. This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('ResetUserSyncDatabase', 'POST').then(function() {
                ServerSyncShared.showAlert('User sync database has been reset');
                self.loadUserStatus();
                self.loadUserItems();
                self.loadHealthStats();
            }).catch(function(err) {
                console.error('ResetUserSyncDatabase error:', err);
                ServerSyncShared.showAlert('Failed to reset user sync database');
            });
        },

        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnUserBulkIgnore');
            var queueBtn = view.querySelector('#btnUserBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        bulkIgnore: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var userMappings = selectedKeys.map(function(key) {
                var parts = key.split('|');
                return { SourceUserId: parts[0], LocalUserId: parts[1] };
            });

            ServerSyncShared.apiRequest('UserSyncUsers/Ignore', 'POST', { UserMappings: userMappings }).then(function() {
                self.table.clearSelection();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' user(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore users');
            });
        },

        bulkQueue: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var userMappings = selectedKeys.map(function(key) {
                var parts = key.split('|');
                return { SourceUserId: parts[0], LocalUserId: parts[1] };
            });

            ServerSyncShared.apiRequest('UserSyncUsers/Queue', 'POST', { UserMappings: userMappings }).then(function() {
                self.table.clearSelection();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' user(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue users');
            });
        },

        showUserDetail: function(item) {
            var self = this;
            var sourceUserId = item.SourceUserId;
            var localUserId = item.LocalUserId;

            // Fetch full user details from API
            ServerSyncShared.apiRequest('UserSyncUsers/' + encodeURIComponent(sourceUserId) + '/' + encodeURIComponent(localUserId)).then(function(detail) {
                if (!detail) {
                    ServerSyncShared.showAlert('User not found');
                    return;
                }

                self.currentModalDetail = detail;

                // Title
                view.querySelector('#userSyncModalTitle').textContent =
                    (detail.SourceUserName || 'Unknown') + ' -> ' + (detail.LocalUserName || 'Unknown');

                // Status badge
                var statusBadge = view.querySelector('#userSyncModalStatusBadge');
                statusBadge.textContent = detail.OverallStatus || 'Unknown';
                statusBadge.className = 'itemModal-statusBadge ' + (detail.OverallStatus || 'unknown');

                // Last sync
                var lastSyncSection = view.querySelector('#userSyncModalLastSyncSection');
                if (detail.LastSyncTime) {
                    lastSyncSection.classList.remove('hidden');
                    view.querySelector('#userSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime));
                } else {
                    lastSyncSection.classList.add('hidden');
                }

                // Error section
                var errorSection = view.querySelector('#userSyncModalErrorSection');
                if (detail.OverallStatus === 'Errored' && detail.ErrorMessage) {
                    errorSection.classList.remove('hidden');
                    view.querySelector('#userSyncModalError').textContent = detail.ErrorMessage;
                } else {
                    errorSection.classList.add('hidden');
                }

                // User info
                view.querySelector('#userSyncModalUser').textContent =
                    (detail.SourceUserName || 'Unknown') + ' -> ' + (detail.LocalUserName || 'Unknown');

                // Table headers - use 4 columns: Property | Source | Local | After Sync
                view.querySelector('#userSyncModalSourceHeader').textContent =
                    (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                view.querySelector('#userSyncModalLocalHeader').textContent =
                    ServerSyncShared.localServerName || 'Local';

                // Build comparison table with all categories
                var tbody = view.querySelector('#userSyncModalTableBody');
                tbody.innerHTML = '';

                // Policy section
                if (detail.PolicyEnabled) {
                    if (detail.PolicyItem) {
                        tbody.appendChild(self._createSectionHeader('Policy'));
                        self._addPropertyRows(tbody, detail.PolicyItem);
                    } else {
                        tbody.appendChild(self._createDisabledRow('Policy', 'Not available'));
                    }
                }

                // Configuration section
                if (detail.ConfigurationEnabled) {
                    if (detail.ConfigurationItem) {
                        tbody.appendChild(self._createSectionHeader('Configuration'));
                        self._addPropertyRows(tbody, detail.ConfigurationItem);
                    } else {
                        tbody.appendChild(self._createDisabledRow('Configuration', 'Not available'));
                    }
                }

                // Profile Image section
                if (detail.ProfileImageEnabled) {
                    if (detail.ProfileImageItem) {
                        tbody.appendChild(self._createSectionHeader('Profile Image'));
                        self._addProfileImageRow(tbody, detail.ProfileImageItem);
                    } else {
                        tbody.appendChild(self._createDisabledRow('Profile Image', 'Not available'));
                    }
                }

                view.querySelector('#userSyncItemDetailModal').classList.remove('hidden');
            }).catch(function(err) {
                console.error('Failed to load user detail:', err);
                ServerSyncShared.showAlert('Failed to load user details');
            });
        },

        _createSectionHeader: function(title) {
            var row = document.createElement('tr');
            row.className = 'userSyncModal-sectionHeader';
            var cell = document.createElement('td');
            cell.colSpan = 4;
            cell.textContent = title;
            row.appendChild(cell);
            return row;
        },

        _createDisabledRow: function(category, message) {
            var row = document.createElement('tr');
            row.className = 'userSyncModal-disabledRow';
            var cell = document.createElement('td');
            cell.colSpan = 4;
            cell.innerHTML = '<span style="opacity: 0.5;">' + ServerSyncShared.escapeHtml(category) + ': ' + ServerSyncShared.escapeHtml(message) + '</span>';
            row.appendChild(cell);
            return row;
        },

        _addPropertyRows: function(tbody, item) {
            var self = this;
            var properties = item.Properties || [];

            properties.forEach(function(prop) {
                var row = document.createElement('tr');
                var isChanged = prop.SourceValue !== prop.LocalValue;

                if (isChanged) {
                    row.className = 'userSyncModal-changedRow';
                }

                var propCell = document.createElement('td');
                propCell.className = 'historyCompareTable-property';
                propCell.textContent = prop.Name || '';

                var sourceCell = document.createElement('td');
                sourceCell.className = 'historyCompareTable-value';
                sourceCell.textContent = self._formatValue(prop.SourceValue);

                var localCell = document.createElement('td');
                localCell.className = 'historyCompareTable-value';
                localCell.textContent = self._formatValue(prop.LocalValue);

                var mergedCell = document.createElement('td');
                mergedCell.className = 'historyCompareTable-value historyCompareTable-merged';
                mergedCell.textContent = self._formatValue(prop.MergedValue !== undefined ? prop.MergedValue : prop.SourceValue);

                row.appendChild(propCell);
                row.appendChild(sourceCell);
                row.appendChild(localCell);
                row.appendChild(mergedCell);

                tbody.appendChild(row);
            });
        },

        _addProfileImageRow: function(tbody, item) {
            var row = document.createElement('tr');
            var isChanged = item.HasChanges;

            if (isChanged) {
                row.className = 'userSyncModal-changedRow';
            }

            var propCell = document.createElement('td');
            propCell.className = 'historyCompareTable-property';
            propCell.textContent = 'Profile Image';

            var sourceCell = document.createElement('td');
            sourceCell.className = 'historyCompareTable-value';
            sourceCell.textContent = item.SourceHasImage ? 'Yes' : 'No';

            var localCell = document.createElement('td');
            localCell.className = 'historyCompareTable-value';
            localCell.textContent = item.LocalHasImage ? 'Yes' : 'No';

            var mergedCell = document.createElement('td');
            mergedCell.className = 'historyCompareTable-value historyCompareTable-merged';
            mergedCell.textContent = item.SourceHasImage ? 'Yes' : 'No';

            row.appendChild(propCell);
            row.appendChild(sourceCell);
            row.appendChild(localCell);
            row.appendChild(mergedCell);

            tbody.appendChild(row);
        },

        _formatValue: function(value) {
            if (value === null || value === undefined) {
                return '-';
            }
            if (typeof value === 'boolean') {
                return value ? 'Yes' : 'No';
            }
            if (Array.isArray(value)) {
                return value.length > 0 ? value.join(', ') : '-';
            }
            return String(value);
        },

        closeModal: function() {
            view.querySelector('#userSyncItemDetailModal').classList.add('hidden');
            this.currentModalDetail = null;
        },

        modalIgnore: function() {
            var self = this;
            var detail = self.currentModalDetail;
            if (!detail) return;

            ServerSyncShared.apiRequest('UserSyncUsers/Ignore', 'POST', {
                UserMappings: [{ SourceUserId: detail.SourceUserId, LocalUserId: detail.LocalUserId }]
            }).then(function() {
                self.closeModal();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert('User ignored');
            }).catch(function(err) {
                console.error('Failed to ignore user:', err);
                ServerSyncShared.showAlert('Failed to ignore user');
            });
        },

        modalQueue: function() {
            var self = this;
            var detail = self.currentModalDetail;
            if (!detail) return;

            ServerSyncShared.apiRequest('UserSyncUsers/Queue', 'POST', {
                UserMappings: [{ SourceUserId: detail.SourceUserId, LocalUserId: detail.LocalUserId }]
            }).then(function() {
                self.closeModal();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert('User queued');
            }).catch(function(err) {
                console.error('Failed to queue user:', err);
                ServerSyncShared.showAlert('Failed to queue user');
            });
        }
    };


    // Users Page Controller
    var UsersPageController = {
        currentConfig: null,

        init: function() {
            var self = this;

            // Load config and initialize modules
            self.loadConfig();
        },

        loadConfig: function() {
            var self = this;

            // Fetch local server name first
            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                // Initialize user sync table module
                UserSyncTableModule.currentConfig = config;
                UserSyncTableModule.init(config);

                // Load initial data
                UserSyncTableModule.loadUserStatus();
                UserSyncTableModule.loadUserItems();
                UserSyncTableModule.loadHealthStats();
            });
        }
    };


    // Initialize on viewshow
    view.addEventListener('viewshow', function () {
        console.log('ServerSync Users: viewshow event fired');

        // Set up Jellyfin tabs - index 4 for Users tab
        LibraryMenu.setTabs('serversync', 4, getTabs);

        // Initialize the page
        UsersPageController.init();
    });

    view.addEventListener('viewhide', function () {
        console.log('ServerSync Users: viewhide event fired');
    });
}
