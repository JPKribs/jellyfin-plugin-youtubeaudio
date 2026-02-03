// Server Sync Plugin - History Page Controller
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


    // History Sync Table Module
    // Uses the generic PaginatedTable component for history sync items
    var HistorySyncTableModule = {
        table: null,
        currentModalItem: null,
        currentConfig: null,
        _initialized: false,

        init: function(config) {
            // Prevent double initialization (viewshow can fire multiple times)
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            // Create the paginated table instance for history items
            this.table = new PaginatedTable({
                containerId: 'historyItemsTableContainer',
                endpoint: 'HistoryItems',

                columns: [
                    {
                        key: 'name',
                        label: 'Item',
                        type: 'custom',
                        render: function(item) {
                            var itemName = item.ItemName || 'Unknown Item';
                            // Look up user names from mappings (no GUIDs in table)
                            var userMapping = self.findUserMapping(item.SourceUserId, item.LocalUserId);
                            var sourceUserName = userMapping ? userMapping.SourceUserName : 'Unknown';
                            var localUserName = userMapping ? userMapping.LocalUserName : 'Unknown';
                            var userDisplay = sourceUserName + ' -> ' + localUserName;

                            return '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' + ServerSyncShared.escapeHtml(itemName) + '">' +
                                ServerSyncShared.escapeHtml(itemName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(userDisplay) + '</div>' +
                                '</div>';
                        }
                    },
                    {
                        key: 'details',
                        label: 'Changes',
                        type: 'custom',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No changes</span>';
                            }

                            var details = [];
                            if (item.MergedIsPlayed !== item.LocalIsPlayed) {
                                details.push('Played: ' + (item.MergedIsPlayed ? 'Yes' : 'No'));
                            }
                            if (item.MergedPlayCount !== item.LocalPlayCount) {
                                details.push('Count: ' + (item.MergedPlayCount || 0));
                            }
                            if (item.MergedIsFavorite !== item.LocalIsFavorite) {
                                details.push('Favorite: ' + (item.MergedIsFavorite ? 'Yes' : 'No'));
                            }

                            return ServerSyncShared.escapeHtml(details.join(', ') || 'Changes pending');
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
                        { value: 'Pending', label: 'Pending' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.Id);
                    },
                    onReload: function() {
                        self.loadHistoryStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No history items found. Run a refresh to scan for watch history.'
                }
            });

            // Bind module-specific events
            this._bindModuleEvents();

            // Inject bulk action buttons
            this._injectBulkActions();
        },

        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'HistorySyncTableModule'); };

            // Action buttons
            bind('btnRefreshHistoryItems', function() { self.refreshHistoryTable(); });
            bind('btnTriggerHistorySync', function() { self.triggerHistorySync(); });
            bind('btnRetryHistoryErrors', function() { self.retryErrors(); });
            bind('btnResetHistoryDatabase', function() { self.resetHistoryDatabase(); });

            // Modal buttons
            bind('btnHistoryModalIgnore', function() { self.modalIgnore(); });
            bind('btnHistoryModalQueue', function() { self.modalQueue(); });
            bind('btnHistoryModalClose', function() { self.closeModal(); });
        },

        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnHistoryBulkIgnore" class="raised" disabled><span>Ignore</span></button>' +
                '<button is="emby-button" type="button" id="btnHistoryBulkQueue" class="raised button-primary" disabled><span>Queue</span></button>';

            view.querySelector('#btnHistoryBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnHistoryBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        loadHistoryStatus: function() {
            return ServerSyncShared.apiRequest('HistoryStatus', 'GET').then(function(status) {
                view.querySelector('#historySyncedCount').textContent = status.Synced || 0;
                view.querySelector('#historyQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#historyErroredCount').textContent = status.Errored || 0;
                view.querySelector('#historyIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#historyStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#historyStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#historyStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#historyStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));
            }).catch(function(err) {
                console.log('History status not available:', err);
            });
        },

        loadHealthStats: function() {
            var self = this;
            return Promise.all([
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('HistoryStatus', 'GET')
            ]).then(function(results) {
                var config = results[0];
                var status = results[1];

                // Last history sync time
                var lastSyncEl = view.querySelector('#historyHealthLastSync');
                if (config.LastHistorySyncTime) {
                    var lastSync = new Date(config.LastHistorySyncTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                // User mapping count
                var userCountEl = view.querySelector('#historyHealthUserCount');
                var enabledUsers = (config.UserMappings || []).filter(function(m) { return m.IsEnabled; }).length;
                userCountEl.textContent = enabledUsers;
                userCountEl.className = enabledUsers > 0 ? 'healthValue success' : 'healthValue warning';

                // Library mapping count
                var libraryCountEl = view.querySelector('#historyHealthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';

                // Pending count from status
                var pendingCountEl = view.querySelector('#historyHealthPendingCount');
                var pending = (status.Pending || 0) + (status.Queued || 0);
                pendingCountEl.textContent = pending;
                pendingCountEl.className = pending > 0 ? 'healthValue warning' : 'healthValue';
            }).catch(function() {
                // Ignore errors
            });
        },

        loadHistoryItems: function() {
            return this.table.reload();
        },

        refreshHistoryTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshHistoryItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Refreshing...';

            ServerSyncShared.apiRequest('TriggerHistoryRefresh', 'POST').then(function() {
                ServerSyncShared.showAlert('History refresh task started');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
                self.loadHistoryStatus();
                self.loadHistoryItems();
                self.loadHealthStats();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start history refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        triggerHistorySync: function() {
            var btn = view.querySelector('#btnTriggerHistorySync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerHistorySync', 'POST').then(function() {
                ServerSyncShared.showAlert('History sync task started');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start history sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('HistoryItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored history items queued for retry');
                self.loadHistoryStatus();
                self.loadHistoryItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        resetHistoryDatabase: function() {
            var self = this;

            if (!confirm('Are you sure you want to reset the history sync database?\n\nThis will delete ALL history tracking data and you will need to re-sync everything. This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('ResetHistorySyncDatabase', 'POST').then(function() {
                ServerSyncShared.showAlert('History sync database has been reset');
                self.loadHistoryStatus();
                self.loadHistoryItems();
                self.loadHealthStats();
            }).catch(function(err) {
                console.error('ResetHistorySyncDatabase error:', err);
                ServerSyncShared.showAlert('Failed to reset history sync database');
            });
        },

        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnHistoryBulkIgnore');
            var queueBtn = view.querySelector('#btnHistoryBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        bulkIgnore: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('HistoryItems/Ignore', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore items');
            });
        },

        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('HistoryItems/Queue', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        showItemDetail: function(itemId) {
            var self = this;
            var items = this.table.getItems();
            var item = items.find(function(i) { return i.Id === itemId; });

            if (!item) return;

            self.currentModalItem = item;

            // Set title
            view.querySelector('#historyModalTitle').textContent = item.ItemName || 'Unknown Item';

            // Status badge
            var statusBadge = view.querySelector('#historyModalStatusBadge');
            statusBadge.textContent = item.Status;
            statusBadge.className = 'itemModal-statusBadge ' + item.Status;

            // Error section
            var errorSection = view.querySelector('#historyModalErrorSection');
            if (item.Status === 'Errored' && item.ErrorMessage) {
                view.querySelector('#historyModalError').textContent = item.ErrorMessage;
                errorSection.classList.remove('hidden');
            } else {
                errorSection.classList.add('hidden');
            }

            // User info - look up names from user mappings
            var userMapping = self.findUserMapping(item.SourceUserId, item.LocalUserId);
            var sourceUserDisplay = self.formatUserDisplay(
                userMapping ? userMapping.SourceUserName : null,
                item.SourceUserId
            );
            var localUserDisplay = self.formatUserDisplay(
                userMapping ? userMapping.LocalUserName : null,
                item.LocalUserId
            );
            view.querySelector('#historyModalUser').innerHTML = sourceUserDisplay + ' -> ' + localUserDisplay;

            // Set server names in table headers (no GUIDs in table headers)
            var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
            var localServerName = ServerSyncShared.localServerName || 'Local';
            view.querySelector('#historyModalSourceHeader').textContent = sourceServerName;
            view.querySelector('#historyModalLocalHeader').textContent = localServerName;

            // Source state
            self.setTableValue('historyModalSourcePlayed', item.SourceIsPlayed, 'bool');
            self.setTableValue('historyModalSourcePlayCount', item.SourcePlayCount, 'number');
            self.setTableValue('historyModalSourcePosition', item.SourcePlaybackPositionTicks, 'position');
            self.setTableValue('historyModalSourceLastPlayed', item.SourceLastPlayedDate, 'date');
            self.setTableValue('historyModalSourceFavorite', item.SourceIsFavorite, 'favorite');

            // Local state
            self.setTableValue('historyModalLocalPlayed', item.LocalIsPlayed, 'bool');
            self.setTableValue('historyModalLocalPlayCount', item.LocalPlayCount, 'number');
            self.setTableValue('historyModalLocalPosition', item.LocalPlaybackPositionTicks, 'position');
            self.setTableValue('historyModalLocalLastPlayed', item.LocalLastPlayedDate, 'date');
            self.setTableValue('historyModalLocalFavorite', item.LocalIsFavorite, 'favorite');

            // Merged state
            self.setTableValue('historyModalMergedPlayed', item.MergedIsPlayed, 'bool');
            self.setTableValue('historyModalMergedPlayCount', item.MergedPlayCount, 'number');
            self.setTableValue('historyModalMergedPosition', item.MergedPlaybackPositionTicks, 'position');
            self.setTableValue('historyModalMergedLastPlayed', item.MergedLastPlayedDate, 'date');
            self.setTableValue('historyModalMergedFavorite', item.MergedIsFavorite, 'favorite');

            // Last sync
            var lastSyncSection = view.querySelector('#historyModalLastSyncSection');
            if (item.LastSyncTime) {
                view.querySelector('#historyModalLastSync').textContent =
                    ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                lastSyncSection.classList.remove('hidden');
            } else {
                lastSyncSection.classList.add('hidden');
            }

            view.querySelector('#historyItemDetailModal').classList.remove('hidden');
        },

        setTableValue: function(elementId, value, type) {
            var el = view.querySelector('#' + elementId);
            if (!el) return;

            var text = '-';
            var cssClass = '';

            switch (type) {
                case 'bool':
                    if (value === true) {
                        text = 'Yes';
                        cssClass = 'value-yes';
                    } else if (value === false) {
                        text = 'No';
                        cssClass = 'value-no';
                    }
                    break;
                case 'favorite':
                    if (value === true) {
                        text = 'Yes';
                        cssClass = 'value-favorite';
                    } else if (value === false) {
                        text = 'No';
                        cssClass = 'value-no';
                    }
                    break;
                case 'number':
                    text = (value !== null && value !== undefined) ? String(value) : '-';
                    break;
                case 'position':
                    text = this.formatPosition(value);
                    break;
                case 'date':
                    text = this.formatDate(value);
                    break;
            }

            el.textContent = text;
            // Keep the base classes and add value-specific class
            var baseClasses = el.className.split(' ').filter(function(c) {
                return c.indexOf('value-') !== 0;
            }).join(' ');
            el.className = baseClasses + (cssClass ? ' ' + cssClass : '');
        },

        formatPosition: function(ticks) {
            if (!ticks || ticks === 0) return '-';
            // Convert ticks to time (1 tick = 100 nanoseconds)
            var seconds = Math.floor(ticks / 10000000);
            var minutes = Math.floor(seconds / 60);
            var hours = Math.floor(minutes / 60);
            seconds = seconds % 60;
            minutes = minutes % 60;

            if (hours > 0) {
                return hours + ':' + String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
            }
            return minutes + ':' + String(seconds).padStart(2, '0');
        },

        formatDate: function(dateStr) {
            if (!dateStr) return '-';
            try {
                return ServerSyncShared.formatRelativeTime(new Date(dateStr));
            } catch (e) {
                return dateStr;
            }
        },

        findUserMapping: function(sourceUserId, localUserId) {
            if (!this.currentConfig || !this.currentConfig.UserMappings) return null;
            return this.currentConfig.UserMappings.find(function(m) {
                return m.SourceUserId === sourceUserId || m.LocalUserId === localUserId;
            });
        },

        formatUserDisplay: function(userName, userId) {
            // Modal shows: username (GUID)
            if (userName && userId) {
                return ServerSyncShared.escapeHtml(userName) +
                    ' <span class="historyModalUserGuid">(' + ServerSyncShared.escapeHtml(userId) + ')</span>';
            }
            if (userName) {
                return ServerSyncShared.escapeHtml(userName);
            }
            // Fallback to GUID only if no username available
            return '<span class="historyModalUserGuid">' + ServerSyncShared.escapeHtml(userId || 'Unknown') + '</span>';
        },

        closeModal: function() {
            view.querySelector('#historyItemDetailModal').classList.add('hidden');
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

            ServerSyncShared.apiRequest('HistoryItems/UpdateStatus', 'POST', { Id: itemId, Status: status }).then(function() {
                self.closeModal();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        }
    };


    // History Page Controller
    var HistoryPageController = {
        currentConfig: null,

        init: function() {
            var self = this;

            // Collapsible sections
            view.querySelectorAll('.collapsibleHeader').forEach(function(header) {
                header.addEventListener('click', function() {
                    var targetId = this.dataset.target;
                    var content = view.querySelector('#' + targetId);
                    if (content) {
                        this.classList.toggle('collapsed');
                        content.classList.toggle('collapsed');
                    }
                });
            });

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

                // Initialize history sync table module
                HistorySyncTableModule.currentConfig = config;
                HistorySyncTableModule.init(config);

                // Load initial data
                HistorySyncTableModule.loadHistoryStatus();
                HistorySyncTableModule.loadHistoryItems();
                HistorySyncTableModule.loadHealthStats();
            });
        }
    };


    // Initialize on viewshow
    view.addEventListener('viewshow', function () {
        console.log('ServerSync History: viewshow event fired');

        // Set up Jellyfin tabs - index 2 for History tab
        LibraryMenu.setTabs('serversync', 2, getTabs);

        // Initialize the page
        HistoryPageController.init();
    });

    view.addEventListener('viewhide', function () {
        console.log('ServerSync History: viewhide event fired');
    });
}
