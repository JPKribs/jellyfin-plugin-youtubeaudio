// Main orchestrator for Server Sync plugin configuration

var ServerSyncConfig = {
    currentConfig: null,

    init: function() {
        var self = this;

        // Tab navigation
        document.querySelectorAll('.tabButton').forEach(function(btn) {
            btn.addEventListener('click', function() {
                if (this.disabled) return;
                self.switchTab(this.dataset.tab);
            });
        });

        // Form submission
        document.getElementById('ServerSyncConfigForm').addEventListener('submit', function(e) {
            e.preventDefault();
            self.saveConfig();
        });

        // Load capabilities first, then config
        self.loadCapabilities().then(function() {
            self.loadConfig();
        });
    },

    switchTab: function(tabId) {
        document.querySelectorAll('.tabButton').forEach(function(btn) {
            btn.classList.toggle('active', btn.dataset.tab === tabId);
        });
        document.querySelectorAll('.tabPanel').forEach(function(panel) {
            panel.style.display = panel.id === 'tab' + tabId.charAt(0).toUpperCase() + tabId.slice(1) ? 'block' : 'none';
        });
    },

    loadCapabilities: function() {
        return Promise.all([
            ContentConfigModule.loadCapabilities(),
            SyncTableModule.loadCapabilities()
        ]);
    },

    loadConfig: function() {
        var self = this;

        ServerSyncShared.getConfig().then(function(config) {
            self.currentConfig = config;

            // Initialize modules with config
            SourceServerModule.init(config);
            ContentConfigModule.init(config);
            SyncTableModule.init();

            // Load config into modules
            ContentConfigModule.loadConfig(config);

            // Wire up cross-module communication
            SourceServerModule.onConnectionSuccess = function() {
                var values = SourceServerModule.getValues();
                ContentConfigModule.fetchSourceLibraries(values.SourceServerUrl, values.SourceServerApiKey);
            };

            // Fetch libraries then render mappings
            var libraryPromises = [ContentConfigModule.fetchLocalLibraries()];
            if (config.SourceServerUrl && config.SourceServerApiKey) {
                libraryPromises.push(ContentConfigModule.fetchSourceLibraries(config.SourceServerUrl, config.SourceServerApiKey));
            }

            Promise.all(libraryPromises).then(function() {
                ContentConfigModule.renderMappings(config.LibraryMappings || []);
            });

            // Load sync data
            SyncTableModule.loadSyncStatus();
            SyncTableModule.loadSyncItems();
            SyncTableModule.loadHealthStats();
        });
    },

    saveConfig: function() {
        var self = this;
        var config = self.currentConfig || {};

        // Merge values from modules
        Object.assign(config, SourceServerModule.getValues());
        Object.assign(config, ContentConfigModule.getValues());

        ServerSyncShared.saveConfig(config).then(function() {
            ServerSyncShared.showAlert('Settings saved');
        }).catch(function(err) {
            console.error('Failed to save config:', err);
            ServerSyncShared.showAlert('Failed to save settings');
        });
    }
};

// Initialize when page is shown
(function() {
    var page = document.querySelector('#ServerSyncConfigPage');
    page.addEventListener('viewshow', function() {
        ServerSyncConfig.init();
    });
})();
