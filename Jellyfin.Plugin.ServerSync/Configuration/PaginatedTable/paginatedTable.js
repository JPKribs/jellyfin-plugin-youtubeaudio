// PaginatedTable - Generic paginated table component for Server Sync plugin
// Provides reusable table functionality with server-side pagination

/**
 * PaginatedTable constructor
 * @param {Object} options - Configuration options
 * @param {string} options.containerId - ID of container element
 * @param {string} options.endpoint - API endpoint for fetching data
 * @param {Array} options.columns - Column definitions
 * @param {Object} [options.selection] - Selection configuration
 * @param {Object} [options.pagination] - Pagination configuration
 * @param {Object} [options.filters] - Filter configuration
 * @param {Object} [options.actions] - Action callbacks
 * @param {Object} [options.emptyState] - Empty state configuration
 */
var PaginatedTable = function(options) {
    // Merge defaults with provided options
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
            pageSize: 50,
            pageSizes: [25, 50, 100]
        },
        filters: {
            options: [],
            buildParams: null
        },
        actions: {
            onRowClick: null
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

    // Deep merge options
    this._mergeOptions(options);

    // Instance state
    this.state = {
        items: [],
        totalCount: 0,
        currentPage: 1,
        pageSize: this.options.pagination.pageSize,
        selectedIds: new Set(),
        filterValue: '',
        searchQuery: '',
        isLoading: false
    };

    // DOM element references
    this.elements = {};

    // Search debounce timeout
    this.searchTimeout = null;

    // Initialize
    this._init();
};

PaginatedTable.prototype = {
    /**
     * Deep merge options
     */
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

    /**
     * Initialize the table component
     */
    _init: function() {
        this._createStructure();
        this._bindEvents();
    },

    /**
     * Create HTML structure
     */
    _createStructure: function() {
        var container = document.getElementById(this.options.containerId);
        if (!container) {
            console.error('PaginatedTable: Container not found:', this.options.containerId);
            return;
        }

        container.innerHTML = this._buildHTML();
        this._cacheElements(container);
    },

    /**
     * Cache DOM element references
     */
    _cacheElements: function(container) {
        this.elements = {
            container: container,
            search: container.querySelector('.pt-search'),
            filter: container.querySelector('.pt-filter'),
            selectAll: container.querySelector('.pt-select-all'),
            selectedCount: container.querySelector('.pt-selected-count'),
            bulkActions: container.querySelector('.pt-bulk-actions'),
            body: container.querySelector('.pt-body'),
            pageInfo: container.querySelector('.pt-page-info'),
            pageIndicator: container.querySelector('.pt-page-indicator'),
            btnFirst: container.querySelector('.pt-btn-first'),
            btnPrev: container.querySelector('.pt-btn-prev'),
            btnNext: container.querySelector('.pt-btn-next'),
            btnLast: container.querySelector('.pt-btn-last'),
            pageSize: container.querySelector('.pt-page-size')
        };
    },

    /**
     * Build the table HTML structure
     */
    _buildHTML: function() {
        var opts = this.options;
        var html = '<div class="pt-wrapper">';

        // Controls row (search + filter)
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

        // Selection header (if enabled)
        if (opts.selection && opts.selection.enabled) {
            html += '<div class="pt-selection-header">';
            html += '<label class="pt-select-all-container">';
            html += '<input type="checkbox" class="pt-select-all" />';
            html += '<span>Select All</span>';
            html += '</label>';
            html += '<span class="pt-selected-count">0 selected</span>';
            html += '<div class="pt-bulk-actions"></div>';
            html += '</div>';
        }

        // Table header
        var visibleColumns = opts.columns.filter(function(col) { return !col.hidden; });
        html += '<div class="pt-header">';
        if (opts.selection && opts.selection.enabled) {
            html += '<div class="pt-header-cell pt-cell-checkbox"></div>';
        }
        visibleColumns.forEach(function(col) {
            var className = 'pt-header-cell';
            if (col.type === 'status') className += ' pt-cell-status';
            if (col.className) className += ' ' + col.className;
            html += '<div class="' + className + '">' +
                ServerSyncShared.escapeHtml(col.label || '') + '</div>';
        });
        html += '</div>';

        // Table body (populated dynamically)
        html += '<div class="pt-body"></div>';

        // Pagination controls
        html += '<div class="pt-pagination">';
        html += '<div class="pt-page-info"></div>';
        html += '<div class="pt-page-controls">';
        html += '<button is="emby-button" class="pt-btn-first" title="First page">&#x23EE;</button>';
        html += '<button is="emby-button" class="pt-btn-prev" title="Previous page">&#x276E;</button>';
        html += '<span class="pt-page-indicator"></span>';
        html += '<button is="emby-button" class="pt-btn-next" title="Next page">&#x276F;</button>';
        html += '<button is="emby-button" class="pt-btn-last" title="Last page">&#x23ED;</button>';
        html += '</div>';
        if (opts.pagination.pageSizes && opts.pagination.pageSizes.length > 1) {
            html += '<select is="emby-select" class="pt-page-size">';
            var self = this;
            opts.pagination.pageSizes.forEach(function(size) {
                var selected = size === self.state.pageSize ? ' selected' : '';
                html += '<option value="' + size + '"' + selected + '>' + size + ' per page</option>';
            });
            html += '</select>';
        }
        html += '</div>';

        html += '</div>';
        return html;
    },

    /**
     * Bind event listeners
     */
    _bindEvents: function() {
        var self = this;

        // Search input
        if (this.elements.search) {
            this.elements.search.addEventListener('input', function(e) {
                self._handleSearchInput(e.target.value);
            });
        }

        // Filter select
        if (this.elements.filter) {
            this.elements.filter.addEventListener('change', function(e) {
                self.setFilter(e.target.value);
            });
        }

        // Select all checkbox
        if (this.elements.selectAll) {
            this.elements.selectAll.addEventListener('change', function(e) {
                self._toggleSelectAll(e.target.checked);
            });
        }

        // Pagination buttons
        if (this.elements.btnFirst) {
            this.elements.btnFirst.addEventListener('click', function() { self.goToPage(1); });
        }
        if (this.elements.btnPrev) {
            this.elements.btnPrev.addEventListener('click', function() { self.goToPage(self.state.currentPage - 1); });
        }
        if (this.elements.btnNext) {
            this.elements.btnNext.addEventListener('click', function() { self.goToPage(self.state.currentPage + 1); });
        }
        if (this.elements.btnLast) {
            this.elements.btnLast.addEventListener('click', function() {
                var totalPages = Math.ceil(self.state.totalCount / self.state.pageSize) || 1;
                self.goToPage(totalPages);
            });
        }

        // Page size select
        if (this.elements.pageSize) {
            this.elements.pageSize.addEventListener('change', function(e) {
                self.state.pageSize = parseInt(e.target.value, 10);
                self.state.currentPage = 1;
                self.load();
            });
        }
    },

    /**
     * Handle search input with debounce
     */
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

    /**
     * Load data from server
     */
    load: function() {
        var self = this;
        var state = this.state;
        var opts = this.options;

        if (state.isLoading) return Promise.resolve();
        state.isLoading = true;
        this._setLoading(true);

        // Build query parameters
        var params = [];
        params.push('skip=' + ((state.currentPage - 1) * state.pageSize));
        params.push('take=' + state.pageSize);

        if (state.searchQuery) {
            params.push('search=' + encodeURIComponent(state.searchQuery));
        }

        // Handle filter value
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
            state.items = result.Items || [];
            state.totalCount = result.TotalCount || 0;
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

    /**
     * Set loading state
     */
    _setLoading: function(loading) {
        if (this.elements.container) {
            if (loading) {
                this.elements.container.classList.add('pt-loading');
            } else {
                this.elements.container.classList.remove('pt-loading');
            }
        }
    },

    /**
     * Render the table
     */
    _render: function() {
        this._renderBody();
        this._updatePagination();
        this._updateSelectionUI();
    },

    /**
     * Render the table body
     */
    _renderBody: function() {
        var self = this;
        var state = this.state;
        var opts = this.options;
        var body = this.elements.body;

        if (!body) return;

        // Empty state
        if (state.items.length === 0) {
            body.innerHTML = '<div class="pt-empty">' +
                ServerSyncShared.escapeHtml(opts.emptyState.message) + '</div>';
            return;
        }

        // Render rows
        body.innerHTML = state.items.map(function(item) {
            return self._renderRow(item);
        }).join('');

        // Bind row events
        this._bindRowEvents();
    },

    /**
     * Render a single row
     */
    _renderRow: function(item) {
        var self = this;
        var opts = this.options;
        var idKey = opts.selection && opts.selection.idKey || 'id';
        var itemId = item[idKey];
        var visibleColumns = opts.columns.filter(function(col) { return !col.hidden; });

        var html = '<div class="pt-row" data-id="' + ServerSyncShared.escapeHtml(String(itemId)) + '">';

        // Checkbox cell
        if (opts.selection && opts.selection.enabled) {
            var checked = this.state.selectedIds.has(itemId) ? ' checked' : '';
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
                content = '<span class="pt-status-badge ' + statusClass + '">' +
                    ServerSyncShared.escapeHtml(displayStatus) + '</span>';
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

    /**
     * Get display status text
     */
    _getDisplayStatus: function(item, value) {
        if (this.options.getDisplayStatus) {
            return this.options.getDisplayStatus(item, value);
        }
        return value || '';
    },

    /**
     * Get status CSS class
     */
    _getStatusClass: function(item, value) {
        if (this.options.getStatusClass) {
            return this.options.getStatusClass(item, value);
        }
        return value || '';
    },

    /**
     * Bind events to rendered rows
     */
    _bindRowEvents: function() {
        var self = this;
        var body = this.elements.body;
        if (!body) return;

        // Row click events
        body.querySelectorAll('.pt-row').forEach(function(row) {
            row.addEventListener('click', function(e) {
                // Don't trigger row click if clicking checkbox
                if (e.target.type === 'checkbox') return;

                var id = row.dataset.id;
                var item = self._getItemById(id);
                if (item && self.options.actions && self.options.actions.onRowClick) {
                    self.options.actions.onRowClick(item);
                }
            });
        });

        // Checkbox change events
        body.querySelectorAll('.pt-row-checkbox').forEach(function(checkbox) {
            checkbox.addEventListener('change', function() {
                var id = checkbox.dataset.id;
                if (checkbox.checked) {
                    self.state.selectedIds.add(id);
                } else {
                    self.state.selectedIds.delete(id);
                }
                self._updateSelectionUI();
                self._notifySelectionChange();
            });
        });
    },

    /**
     * Get item by ID
     */
    _getItemById: function(id) {
        var idKey = this.options.selection && this.options.selection.idKey || 'id';
        return this.state.items.find(function(item) {
            return String(item[idKey]) === String(id);
        });
    },

    /**
     * Update pagination UI
     */
    _updatePagination: function() {
        var state = this.state;
        var totalPages = Math.ceil(state.totalCount / state.pageSize) || 1;
        var start = state.totalCount > 0 ? ((state.currentPage - 1) * state.pageSize) + 1 : 0;
        var end = Math.min(state.currentPage * state.pageSize, state.totalCount);

        // Page info
        if (this.elements.pageInfo) {
            if (state.totalCount === 0) {
                this.elements.pageInfo.textContent = 'No items';
            } else {
                this.elements.pageInfo.textContent = 'Showing ' + start + '-' + end + ' of ' + state.totalCount;
            }
        }

        // Page indicator
        if (this.elements.pageIndicator) {
            this.elements.pageIndicator.textContent = 'Page ' + state.currentPage + ' of ' + totalPages;
        }

        // Button states
        var isFirstPage = state.currentPage <= 1;
        var isLastPage = state.currentPage >= totalPages;

        if (this.elements.btnFirst) this.elements.btnFirst.disabled = isFirstPage;
        if (this.elements.btnPrev) this.elements.btnPrev.disabled = isFirstPage;
        if (this.elements.btnNext) this.elements.btnNext.disabled = isLastPage;
        if (this.elements.btnLast) this.elements.btnLast.disabled = isLastPage;
    },

    /**
     * Update selection UI
     */
    _updateSelectionUI: function() {
        var count = this.state.selectedIds.size;

        // Update selected count
        if (this.elements.selectedCount) {
            this.elements.selectedCount.textContent = count + ' selected';
        }

        // Update select all checkbox
        if (this.elements.selectAll) {
            var allSelected = count > 0 && count === this.state.items.length;
            var someSelected = count > 0 && count < this.state.items.length;
            this.elements.selectAll.checked = allSelected;
            this.elements.selectAll.indeterminate = someSelected;
        }
    },

    /**
     * Toggle select all
     */
    _toggleSelectAll: function(checked) {
        var self = this;
        var idKey = this.options.selection && this.options.selection.idKey || 'id';

        this.state.selectedIds.clear();

        if (checked) {
            this.state.items.forEach(function(item) {
                self.state.selectedIds.add(String(item[idKey]));
            });
        }

        // Update checkboxes in DOM
        if (this.elements.body) {
            this.elements.body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                cb.checked = checked;
            });
        }

        this._updateSelectionUI();
        this._notifySelectionChange();
    },

    /**
     * Notify selection change callback
     */
    _notifySelectionChange: function() {
        if (this.options.selection && this.options.selection.onSelectionChange) {
            this.options.selection.onSelectionChange(this.getSelectedIds());
        }
    },

    // Public API methods

    /**
     * Get selected item IDs
     */
    getSelectedIds: function() {
        return Array.from(this.state.selectedIds);
    },

    /**
     * Get selected items
     */
    getSelectedItems: function() {
        var self = this;
        var idKey = this.options.selection && this.options.selection.idKey || 'id';
        return this.state.items.filter(function(item) {
            return self.state.selectedIds.has(String(item[idKey]));
        });
    },

    /**
     * Clear selection
     */
    clearSelection: function() {
        this.state.selectedIds.clear();
        this._updateSelectionUI();
        this._notifySelectionChange();

        // Uncheck all checkboxes
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

    /**
     * Refresh current page
     */
    refresh: function() {
        return this.load();
    },

    /**
     * Go to specific page
     */
    goToPage: function(page) {
        var totalPages = Math.ceil(this.state.totalCount / this.state.pageSize) || 1;
        this.state.currentPage = Math.max(1, Math.min(page, totalPages));
        return this.load();
    },

    /**
     * Set filter value and reload
     */
    setFilter: function(value) {
        this.state.filterValue = value;
        this.state.currentPage = 1;
        this.clearSelection();
        return this.load();
    },

    /**
     * Set search query and reload
     */
    setSearch: function(query) {
        this.state.searchQuery = query;
        this.state.currentPage = 1;
        this.clearSelection();
        return this.load();
    },

    /**
     * Get current items (for backward compatibility)
     */
    getItems: function() {
        return this.state.items;
    },

    /**
     * Get total count
     */
    getTotalCount: function() {
        return this.state.totalCount;
    },

    /**
     * Get bulk actions container for external button injection
     */
    getBulkActionsContainer: function() {
        return this.elements.bulkActions;
    },

    /**
     * Show/hide a filter option by ID
     */
    setFilterOptionVisible: function(optionId, visible) {
        if (this.elements.filter) {
            var option = this.elements.filter.querySelector('#' + optionId);
            if (option) {
                option.style.display = visible ? 'block' : 'none';
            }
        }
    },

    /**
     * Update the filter select value programmatically
     */
    setFilterValue: function(value) {
        this.state.filterValue = value;
        if (this.elements.filter) {
            this.elements.filter.value = value;
        }
    }
};
