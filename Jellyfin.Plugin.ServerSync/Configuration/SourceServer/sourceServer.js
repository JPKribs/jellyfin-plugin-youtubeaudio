// Source Server Configuration module

var SourceServerModule = {
    currentConfig: null,

    init: function(config) {
        var self = this;
        self.currentConfig = config;

        document.getElementById('btnSaveServer').addEventListener('click', function() { self.saveServerConfig(); });
        document.getElementById('btnTestConnection').addEventListener('click', function() { self.testConnection(); });

        self.loadConfig(config);
    },

    loadConfig: function(config) {
        document.getElementById('txtSourceServerUrl').value = config.SourceServerUrl || '';
        document.getElementById('txtSourceServerApiKey').value = config.SourceServerApiKey || '';

        if (config.SourceServerName || config.SourceServerId) {
            document.getElementById('txtSourceServerName').textContent = config.SourceServerName || 'Unknown';
            document.getElementById('txtSourceServerId').textContent = config.SourceServerId || 'Unknown';
            document.getElementById('serverInfoContainer').style.display = 'block';
        }
    },

    getValues: function() {
        return {
            SourceServerUrl: document.getElementById('txtSourceServerUrl').value,
            SourceServerApiKey: document.getElementById('txtSourceServerApiKey').value
        };
    },

    testConnection: function() {
        var self = this;
        var url = document.getElementById('txtSourceServerUrl').value;
        var apiKey = document.getElementById('txtSourceServerApiKey').value;
        var statusEl = document.getElementById('connectionStatus');

        if (!url || !apiKey) {
            statusEl.innerHTML = '<span style="color: #d9534f;">Please enter URL and API key</span>';
            return;
        }

        var requestData = {
            ServerUrl: url,
            ApiKey: apiKey
        };

        statusEl.textContent = 'Testing...';

        ServerSyncShared.apiRequest('TestConnection', 'POST', requestData).then(function(response) {
            if (response && response.Success) {
                statusEl.innerHTML = '<span style="color: #5cb85c;">Connected to ' + ServerSyncShared.escapeHtml(response.ServerName) + '</span>';
                document.getElementById('txtSourceServerName').textContent = response.ServerName || 'Unknown';
                document.getElementById('txtSourceServerId').textContent = response.ServerId || 'Unknown';
                document.getElementById('serverInfoContainer').style.display = 'block';

                if (self.currentConfig) {
                    self.currentConfig.SourceServerName = response.ServerName;
                    self.currentConfig.SourceServerId = response.ServerId;
                }

                // Trigger library fetch event
                if (self.onConnectionSuccess) {
                    self.onConnectionSuccess(response);
                }
            } else {
                statusEl.innerHTML = '<span style="color: #d9534f;">' + ServerSyncShared.escapeHtml((response && response.Message) || 'Connection failed') + '</span>';
            }
        }).catch(function() {
            statusEl.innerHTML = '<span style="color: #d9534f;">Connection failed</span>';
        });
    },

    saveServerConfig: function() {
        var self = this;
        var config = self.currentConfig || {};

        config.SourceServerUrl = document.getElementById('txtSourceServerUrl').value;
        config.SourceServerApiKey = document.getElementById('txtSourceServerApiKey').value;

        ServerSyncShared.saveConfig(config).then(function() {
            ServerSyncShared.showAlert('Server settings saved');
        }).catch(function(err) {
            console.error('Failed to save server config:', err);
            ServerSyncShared.showAlert('Failed to save server settings');
        });
    },

    // Callback for successful connection
    onConnectionSuccess: null
};
