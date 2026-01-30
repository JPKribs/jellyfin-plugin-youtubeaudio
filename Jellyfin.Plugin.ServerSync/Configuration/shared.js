// Shared utilities for Server Sync plugin configuration

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

    // Save plugin configuration
    saveConfig: function(config) {
        return ApiClient.updatePluginConfiguration(this.pluginId, config);
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
            // dataType: 'json' handles parsing, just return
            return response;
        }).catch(function(error) {
            // Some endpoints return empty 200/204, which fails JSON parse
            // Check if this is a "no content" situation
            if (error && error.message && error.message.indexOf('JSON') !== -1) {
                return null;
            }
            throw error;
        });
    }
};
