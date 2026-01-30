// PaginatedTable - Generic paginated table component for Server Sync plugin
// Provides reusable table functionality with infinite scroll pagination

// PaginatedTable
// Constructor that initializes a new paginated table instance with the given options.
var PaginatedTable = function(options) {
    this.options = {
        containerId: '',
        endpoint: '',
        columns: [],
        selection: {
            enabled: false,
            idKey: 'id',
            onSelectionChange: null
        },
        pagination: {
            pageSize: 50
        },
        filters: {
            options: [],
            buildParams: null
        },
        actions: {
            onRowClick: null,
            onReload: null
        },
        emptyState: {
            message: 'No items found'
        },
        search: {
            enabled: true,
            placeholder: 'Search...',
            debounceMs: 300
        },
        getDisplayStatus: null,
        getStatusClass: null
    };

    this._mergeOptions(options);

    this.state = {
        items: [],
        totalCount: 0,
        currentPage: 1,
        pageSize: this.options.pagination.pageSize,
        selectedIds: new Set(),
        filterValue: '',
        searchQuery: '',
        isLoading: false,
        hasMore: true
    };

    this.elements = {};
    this.searchTimeout = null;
    this._init();
};

PaginatedTable.prototype = {

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
        var container = document.getElementById(this.options.containerId);
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
        var idKey = opts.selection && opts.selection.idKey || 'id';
        var itemId = item[idKey];
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
        var idKey = this.options.selection && this.options.selection.idKey || 'id';
        return this.state.items.find(function(item) {
            return String(item[idKey]) === String(id);
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
        var idKey = this.options.selection && this.options.selection.idKey || 'id';

        this.state.selectedIds.clear();

        if (checked) {
            this.state.items.forEach(function(item) {
                self.state.selectedIds.add(String(item[idKey]));
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
        var idKey = this.options.selection && this.options.selection.idKey || 'id';
        return this.state.items.filter(function(item) {
            return self.state.selectedIds.has(String(item[idKey]));
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
