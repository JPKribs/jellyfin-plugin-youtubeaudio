// ============================================
// YOUTUBE AUDIO PLUGIN - SHARED MODULE
// ============================================
// Shared utilities used by all plugin pages.

export function getTabs() {
    return [
        { href: 'configurationpage?name=youtubeaudio_download', name: 'Download' },
        { href: 'configurationpage?name=youtubeaudio_import', name: 'Import' },
        { href: 'configurationpage?name=youtubeaudio_settings', name: 'Settings' }
    ];
}

export function createShared(view) {
    return {
        pluginId: '7323ea64-a200-4265-ab8f-e7ae27d06c38',

        // ===== Utilities =====

        // Escape HTML special characters to prevent XSS
        escapeHtml: function(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        },

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

        // Format duration in seconds to mm:ss or hh:mm:ss
        formatDuration: function(seconds) {
            if (!seconds) return '';
            var h = Math.floor(seconds / 3600);
            var m = Math.floor((seconds % 3600) / 60);
            var s = Math.floor(seconds % 60);
            if (h > 0) {
                return h + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
            }
            return m + ':' + String(s).padStart(2, '0');
        },

        // Get plugin configuration
        getConfig: function() {
            var self = this;
            return new Promise(function(resolve, reject) {
                ApiClient.getPluginConfiguration(self.pluginId).then(resolve).catch(reject);
            });
        },

        // Save plugin configuration
        saveConfig: function(config) {
            var self = this;
            return new Promise(function(resolve, reject) {
                ApiClient.updatePluginConfiguration(self.pluginId, config).then(resolve).catch(reject);
            });
        },

        // Make an API request to the YouTube Audio controller
        apiRequest: function(endpoint, method, data) {
            var options = {
                url: ApiClient.getUrl('YouTubeAudio/' + endpoint),
                type: method || 'GET',
                dataType: 'json'
            };

            if (data) {
                options.contentType = 'application/json';
                options.data = JSON.stringify(data);
            }

            return ApiClient.fetch(options).catch(function(error) {
                if (error && error.message && error.message.indexOf('JSON') !== -1) {
                    return null;
                }
                throw error;
            });
        },

        // Status badge rendering (mirrors ServerSync pt-status-badge pattern)
        getStatusBadge: function(statusCode, statusText) {
            var classMap = {
                0: 'Queued',
                1: 'Downloading',
                2: 'Downloaded',
                3: 'Imported',
                4: 'Error'
            };
            var cls = classMap[statusCode] || 'Queued';
            return '<span class="pt-status-badge ' + cls + '">' + this.escapeHtml(statusText) + '</span>';
        },

        // Debounced search for combo-box autocomplete
        createDebouncedSearch: function(endpoint, delay) {
            var timeout = null;
            var self = this;
            return function(query, callback) {
                if (timeout) clearTimeout(timeout);
                if (!query || query.length < 2) {
                    callback([]);
                    return;
                }
                timeout = setTimeout(function() {
                    self.apiRequest(endpoint + '?query=' + encodeURIComponent(query), 'GET')
                        .then(function(results) { callback(results || []); })
                        .catch(function() { callback([]); });
                }, delay || 300);
            };
        },

        // Searchable combo-box component
        // Returns { element, getValue, setValue, destroy }
        createSearchableComboBox: function(options) {
            var container = document.createElement('div');
            container.className = 'yta-combo-box';

            var input = document.createElement('input');
            input.type = 'text';
            input.className = 'yta-combo-input';
            input.placeholder = options.placeholder || '';

            var dropdown = document.createElement('div');
            dropdown.className = 'yta-combo-dropdown hidden';

            container.appendChild(input);
            container.appendChild(dropdown);

            input.addEventListener('input', function() {
                options.searchFn(input.value, function(results) {
                    dropdown.innerHTML = '';
                    if (results.length === 0) {
                        dropdown.classList.add('hidden');
                        return;
                    }
                    results.forEach(function(item) {
                        var opt = document.createElement('div');
                        opt.className = 'yta-combo-option';
                        opt.textContent = item;
                        opt.addEventListener('click', function() {
                            input.value = item;
                            dropdown.classList.add('hidden');
                            if (options.onSelect) options.onSelect(item);
                        });
                        dropdown.appendChild(opt);
                    });
                    dropdown.classList.remove('hidden');
                });
            });

            // Close dropdown when clicking outside (tracked for cleanup)
            function onDocClick(e) {
                if (!container.contains(e.target)) {
                    dropdown.classList.add('hidden');
                }
            }
            document.addEventListener('click', onDocClick);

            return {
                element: container,
                getValue: function() { return input.value; },
                setValue: function(val) { input.value = val || ''; },
                destroy: function() {
                    document.removeEventListener('click', onDocClick);
                }
            };
        },

        // DOM helpers
        getEl: function(id) {
            return view.querySelector('#' + id);
        },

        setVisible: function(id, visible) {
            var el = typeof id === 'string' ? view.querySelector('#' + id) : id;
            if (el) {
                if (visible) el.classList.remove('hidden');
                else el.classList.add('hidden');
            }
        },

        setStatus: function(elementId, message, isError) {
            var el = view.querySelector('#' + elementId);
            if (el) {
                el.textContent = message;
                el.style.color = isError ? 'var(--yta-status-error-color)' : 'var(--yta-status-downloaded-color)';
                if (message) {
                    setTimeout(function() { if (el.textContent === message) el.textContent = ''; }, 5000);
                }
            }
        },

        // Initialize collapsible sections (mirrors ServerSync pattern)
        initCollapsibles: function() {
            view.querySelectorAll('.collapsibleHeader').forEach(function(header) {
                header.addEventListener('click', function() {
                    var targetId = this.dataset.target;
                    var content = view.querySelector('#' + targetId);
                    if (content) {
                        this.classList.toggle('collapsed');
                        content.classList.toggle('collapsed');
                        var isExpanded = !this.classList.contains('collapsed');
                        this.setAttribute('aria-expanded', String(isExpanded));
                    }
                });
            });
        }
    };
}
