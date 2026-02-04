// ============================================
// SETTINGS - PAGE CONTROLLER
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
    // CONSTANTS & STATE
    // ============================================
    var pluginId = 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286';
    var _initialized = false;

    var currentConfig = null;
    var sourceLibraries = [];
    var localLibraries = [];
    var sourceUsers = [];
    var localUsers = [];

    // ============================================
    // UTILITY FUNCTIONS
    // ============================================

    function escapeHtml(str) {
        if (!str) return '';
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function apiRequest(endpoint, method, data) {
        var options = {
            url: ApiClient.getUrl('ServerSync/' + endpoint),
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
    }

    function setVisible(elementId, visible) {
        var el = view.querySelector('#' + elementId);
        if (el) {
            if (visible) {
                el.classList.remove('hidden');
            } else {
                el.classList.add('hidden');
            }
        }
    }

    function bindClick(id, handler) {
        var el = view.querySelector('#' + id);
        if (el) el.addEventListener('click', handler);
        return el;
    }

    // ============================================
    // SERVER MODULE
    // ============================================

    function loadServerConfig(config) {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
        if (urlEl) urlEl.value = config.SourceServerUrl || '';
        if (apiKeyEl) apiKeyEl.value = config.SourceServerApiKey || '';

        if (config.SourceServerName || config.SourceServerId) {
            var nameEl = view.querySelector('#txtSourceServerName');
            var idEl = view.querySelector('#txtSourceServerId');
            if (nameEl) nameEl.textContent = config.SourceServerName || 'Unknown';
            if (idEl) idEl.textContent = config.SourceServerId || 'Unknown';
            setVisible('serverInfoContainer', true);
        }
    }

    function testConnection() {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
        var statusEl = view.querySelector('#connectionStatus');
        var url = urlEl ? urlEl.value : '';
        var apiKey = apiKeyEl ? apiKeyEl.value : '';

        if (!url || !apiKey) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter URL and API key</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Testing...';

        apiRequest('TestConnection', 'POST', { ServerUrl: url, ApiKey: apiKey }).then(function(response) {
            if (response && response.Success) {
                if (statusEl) statusEl.innerHTML = '<span class="text-success">Connected to ' + escapeHtml(response.ServerName) + '</span>';
                var nameEl = view.querySelector('#txtSourceServerName');
                var idEl = view.querySelector('#txtSourceServerId');
                if (nameEl) nameEl.textContent = response.ServerName || 'Unknown';
                if (idEl) idEl.textContent = response.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);

                if (currentConfig) {
                    currentConfig.SourceServerName = response.ServerName;
                    currentConfig.SourceServerId = response.ServerId;
                }

                fetchSourceLibraries(url, apiKey);
                fetchSourceUsers(url, apiKey);
                showMappingSections();
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Connection failed') + '</span>';
            }
        }).catch(function() {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Connection failed</span>';
        });
    }

    function saveServerConfig() {
        var config = currentConfig || {};
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
        config.SourceServerUrl = urlEl ? urlEl.value : '';
        config.SourceServerApiKey = apiKeyEl ? apiKeyEl.value : '';

        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('Server settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save server settings');
        });
    }

    // ============================================
    // LIBRARY MAPPINGS MODULE
    // ============================================

    function fetchSourceLibraries(serverUrl, apiKey) {
        return apiRequest('GetSourceLibraries', 'POST', { ServerUrl: serverUrl, ApiKey: apiKey }).then(function(libraries) {
            sourceLibraries = libraries || [];
            updateLibrarySelects();
        }).catch(function() {
            sourceLibraries = [];
        });
    }

    function fetchLocalLibraries() {
        return ApiClient.fetch({
            url: ApiClient.getUrl('Library/VirtualFolders'),
            type: 'GET',
            dataType: 'json'
        }).then(function(folders) {
            localLibraries = (folders || []).map(function(folder) {
                return { Id: folder.ItemId, Name: folder.Name, Locations: folder.Locations || [] };
            });
        }).catch(function() {
            localLibraries = [];
        });
    }

    function updateLibrarySelects() {
        view.querySelectorAll('.sourceLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select source library...</option>';
            sourceLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations || []);
                select.appendChild(option);
            });
            if (savedValue) select.value = savedValue;
        });
        view.querySelectorAll('.localLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select local library...</option>';
            localLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations || []);
                select.appendChild(option);
            });
            if (savedValue) select.value = savedValue;
        });
    }

    function renderLibraryMappings(mappings) {
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;
        container.innerHTML = '';
        (mappings || []).forEach(function(mapping, index) {
            addLibraryMappingRow(mapping, index);
        });
    }

    function addLibraryMappingRow(mapping, index) {
        mapping = mapping || {};
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;
        if (index === undefined) index = container.children.length;

        var div = document.createElement('div');
        div.className = 'mapping libraryMapping';
        div.innerHTML =
            '<div class="mappingHeader">' +
                '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="mappingEnabled" ' + (mapping.IsEnabled ? 'checked' : '') + ' /><span>Enabled</span></label>' +
                '<button is="emby-button" type="button" class="btnRemoveMapping raised button-destructive"><span>Remove</span></button>' +
            '</div>' +
            '<div class="mappingGrid">' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer"><label class="inputLabel">Source Library</label><select is="emby-select" class="sourceLibrarySelect"></select></div>' +
                    '<div class="inputContainer"><label class="inputLabel">Source Root Path</label><input is="emby-input" type="text" class="sourceRootPath" value="' + escapeHtml(mapping.SourceRootPath || '') + '" /></div>' +
                '</div>' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer"><label class="inputLabel">Local Library</label><select is="emby-select" class="localLibrarySelect"></select></div>' +
                    '<div class="inputContainer"><label class="inputLabel">Local Root Path</label><input is="emby-input" type="text" class="localRootPath" value="' + escapeHtml(mapping.LocalRootPath || '') + '" /></div>' +
                '</div>' +
            '</div>';

        container.appendChild(div);

        // Populate source library select
        var sourceSelect = div.querySelector('.sourceLibrarySelect');
        if (mapping.SourceLibraryId) sourceSelect.dataset.savedValue = mapping.SourceLibraryId;
        sourceSelect.innerHTML = '<option value="">Select source library...</option>';
        sourceLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations || []);
            sourceSelect.appendChild(option);
        });
        if (mapping.SourceLibraryId) sourceSelect.value = mapping.SourceLibraryId;
        sourceSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) div.querySelector('.sourceRootPath').value = locations[0];
            }
        });

        // Populate local library select
        var localSelect = div.querySelector('.localLibrarySelect');
        if (mapping.LocalLibraryId) localSelect.dataset.savedValue = mapping.LocalLibraryId;
        localSelect.innerHTML = '<option value="">Select local library...</option>';
        localLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations || []);
            localSelect.appendChild(option);
        });
        if (mapping.LocalLibraryId) localSelect.value = mapping.LocalLibraryId;
        localSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) div.querySelector('.localRootPath').value = locations[0];
            }
        });

        div.querySelector('.btnRemoveMapping').addEventListener('click', function() { div.remove(); });
    }

    function collectLibraryMappings() {
        var mappings = [];
        view.querySelectorAll('.libraryMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceLibrarySelect');
            var localSelect = row.querySelector('.localLibrarySelect');
            mappings.push({
                IsEnabled: row.querySelector('.mappingEnabled').checked,
                SourceLibraryId: sourceSelect.value,
                SourceLibraryName: sourceSelect.options[sourceSelect.selectedIndex] ? sourceSelect.options[sourceSelect.selectedIndex].textContent : '',
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: localSelect.value,
                LocalLibraryName: localSelect.options[localSelect.selectedIndex] ? localSelect.options[localSelect.selectedIndex].textContent : '',
                LocalRootPath: row.querySelector('.localRootPath').value
            });
        });
        return mappings;
    }

    function saveLibraries() {
        var config = currentConfig || {};
        config.LibraryMappings = collectLibraryMappings();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('Library mappings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save library mappings');
        });
    }

    // ============================================
    // USER MAPPINGS MODULE
    // ============================================

    function fetchSourceUsers(serverUrl, apiKey) {
        return apiRequest('GetSourceUsers', 'POST', { ServerUrl: serverUrl, ApiKey: apiKey }).then(function(users) {
            sourceUsers = users || [];
            updateUserSelects();
        }).catch(function() {
            sourceUsers = [];
        });
    }

    function fetchLocalUsers() {
        return ApiClient.fetch({
            url: ApiClient.getUrl('Users'),
            type: 'GET',
            dataType: 'json'
        }).then(function(users) {
            localUsers = (users || []).map(function(user) {
                return { Id: user.Id, Name: user.Name };
            });
        }).catch(function() {
            localUsers = [];
        });
    }

    function updateUserSelects() {
        view.querySelectorAll('.sourceUserSelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select source user...</option>';
            sourceUsers.forEach(function(user) {
                var option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            if (savedValue) select.value = savedValue;
        });
        view.querySelectorAll('.localUserSelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select local user...</option>';
            localUsers.forEach(function(user) {
                var option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            if (savedValue) select.value = savedValue;
        });
    }

    function renderUserMappings(mappings) {
        var container = view.querySelector('#userMappingsContainer');
        if (!container) return;
        container.innerHTML = '';
        (mappings || []).forEach(function(mapping, index) {
            addUserMappingRow(mapping, index);
        });
    }

    function addUserMappingRow(mapping, index) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#userMappingsContainer');
        if (!container) return;
        if (index === undefined) index = container.children.length;

        var div = document.createElement('div');
        div.className = 'mapping userMapping';
        div.innerHTML =
            '<div class="mappingHeader">' +
                '<label class="checkboxContainer"><input is="emby-checkbox" type="checkbox" class="userMappingEnabled" ' + (mapping.IsEnabled !== false ? 'checked' : '') + ' /><span>Enabled</span></label>' +
                '<button is="emby-button" type="button" class="btnRemoveUserMapping raised button-destructive"><span>Remove</span></button>' +
            '</div>' +
            '<div class="mappingGrid">' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Source User</label><select is="emby-select" class="sourceUserSelect"></select></div></div>' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Local User</label><select is="emby-select" class="localUserSelect"></select></div></div>' +
            '</div>';

        container.appendChild(div);

        var sourceSelect = div.querySelector('.sourceUserSelect');
        if (mapping.SourceUserId) sourceSelect.dataset.savedValue = mapping.SourceUserId;
        sourceSelect.innerHTML = '<option value="">Select source user...</option>';
        sourceUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            sourceSelect.appendChild(option);
        });
        if (mapping.SourceUserId) sourceSelect.value = mapping.SourceUserId;

        var localSelect = div.querySelector('.localUserSelect');
        if (mapping.LocalUserId) localSelect.dataset.savedValue = mapping.LocalUserId;
        localSelect.innerHTML = '<option value="">Select local user...</option>';
        localUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            localSelect.appendChild(option);
        });
        if (mapping.LocalUserId) localSelect.value = mapping.LocalUserId;

        div.querySelector('.btnRemoveUserMapping').addEventListener('click', function() { div.remove(); });
    }

    function collectUserMappings() {
        var mappings = [];
        view.querySelectorAll('.userMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceUserSelect');
            var localSelect = row.querySelector('.localUserSelect');
            mappings.push({
                IsEnabled: row.querySelector('.userMappingEnabled').checked,
                SourceUserId: sourceSelect.value,
                SourceUserName: sourceSelect.options[sourceSelect.selectedIndex] ? sourceSelect.options[sourceSelect.selectedIndex].textContent : '',
                LocalUserId: localSelect.value,
                LocalUserName: localSelect.options[localSelect.selectedIndex] ? localSelect.options[localSelect.selectedIndex].textContent : ''
            });
        });
        return mappings;
    }

    function saveUsers() {
        var config = currentConfig || {};
        config.UserMappings = collectUserMappings();
        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('User mappings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save user mappings');
        });
    }

    // ============================================
    // SYNC SETTINGS MODULE
    // ============================================

    // --- Content Settings ---

    function loadContentSettings(config) {
        view.querySelector('#chkEnableContentSync').checked = config.EnableContentSync || false;
        view.querySelector('#chkDetectUpdatedFiles').checked = config.DetectUpdatedFiles !== false;
        view.querySelector('#selChangeDetectionPolicy').value = config.ChangeDetectionPolicy || 'SizeOnly';
        view.querySelector('#chkIncludeExtras').checked = config.IncludeCompanionFiles || false;
        view.querySelector('#selDownloadNewContentMode').value = config.DownloadNewContentMode || 'Enabled';
        view.querySelector('#selReplaceExistingContentMode').value = config.ReplaceExistingContentMode || 'Enabled';
        view.querySelector('#selDeleteMissingContentMode').value = config.DeleteMissingContentMode || 'Disabled';
        view.querySelector('#chkEnableRecyclingBin').checked = config.EnableRecyclingBin || false;
        view.querySelector('#txtRecyclingBinPath').value = config.RecyclingBinPath || '';
        view.querySelector('#txtRecyclingBinRetentionDays').value = config.RecyclingBinRetentionDays || 7;
        view.querySelector('#chkRemoveEmptyFolders').checked = config.RemoveEmptyFoldersOnDelete || false;
        view.querySelector('#txtMaxConcurrentDownloads').value = config.MaxConcurrentDownloads || 2;
        view.querySelector('#txtMaxRetryCount').value = config.MaxRetryCount || 3;
        view.querySelector('#txtTempDownloadPath').value = config.TempDownloadPath || '';
        view.querySelector('#txtMaxDownloadSpeed').value = config.MaxDownloadSpeed || 0;
        view.querySelector('#selDownloadSpeedUnit').value = config.DownloadSpeedUnit || 'MB';
        view.querySelector('#txtMinFreeDiskSpace').value = config.MinimumFreeDiskSpaceGb || 10;
        view.querySelector('#chkEnableBandwidthScheduling').checked = config.EnableBandwidthScheduling || false;
        view.querySelector('#txtScheduledStartHour').value = config.ScheduledStartHour || 0;
        view.querySelector('#txtScheduledEndHour').value = config.ScheduledEndHour || 6;
        view.querySelector('#txtScheduledDownloadSpeed').value = config.ScheduledDownloadSpeed || 0;
        view.querySelector('#selScheduledDownloadSpeedUnit').value = config.ScheduledDownloadSpeedUnit || 'MB';

        updateNestedVisibility();
    }

    function saveContentSettings() {
        var config = currentConfig || {};
        config.EnableContentSync = view.querySelector('#chkEnableContentSync').checked;
        config.DetectUpdatedFiles = view.querySelector('#chkDetectUpdatedFiles').checked;
        config.ChangeDetectionPolicy = view.querySelector('#selChangeDetectionPolicy').value;
        config.IncludeCompanionFiles = view.querySelector('#chkIncludeExtras').checked;
        config.DownloadNewContentMode = view.querySelector('#selDownloadNewContentMode').value;
        config.ReplaceExistingContentMode = view.querySelector('#selReplaceExistingContentMode').value;
        config.DeleteMissingContentMode = view.querySelector('#selDeleteMissingContentMode').value;
        config.EnableRecyclingBin = view.querySelector('#chkEnableRecyclingBin').checked;
        config.RecyclingBinPath = view.querySelector('#txtRecyclingBinPath').value;
        config.RecyclingBinRetentionDays = parseInt(view.querySelector('#txtRecyclingBinRetentionDays').value) || 7;
        config.RemoveEmptyFoldersOnDelete = view.querySelector('#chkRemoveEmptyFolders').checked;
        config.MaxConcurrentDownloads = parseInt(view.querySelector('#txtMaxConcurrentDownloads').value) || 2;
        config.MaxRetryCount = parseInt(view.querySelector('#txtMaxRetryCount').value) || 3;
        config.TempDownloadPath = view.querySelector('#txtTempDownloadPath').value || null;
        config.MaxDownloadSpeed = parseInt(view.querySelector('#txtMaxDownloadSpeed').value) || 0;
        config.DownloadSpeedUnit = view.querySelector('#selDownloadSpeedUnit').value;
        config.MinimumFreeDiskSpaceGb = parseInt(view.querySelector('#txtMinFreeDiskSpace').value) || 10;
        config.EnableBandwidthScheduling = view.querySelector('#chkEnableBandwidthScheduling').checked;
        config.ScheduledStartHour = parseInt(view.querySelector('#txtScheduledStartHour').value) || 0;
        config.ScheduledEndHour = parseInt(view.querySelector('#txtScheduledEndHour').value) || 6;
        config.ScheduledDownloadSpeed = parseInt(view.querySelector('#txtScheduledDownloadSpeed').value) || 0;
        config.ScheduledDownloadSpeedUnit = view.querySelector('#selScheduledDownloadSpeedUnit').value;

        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('Content settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save content settings');
        });
    }

    // --- History Settings ---

    function loadHistorySettings(config) {
        view.querySelector('#chkEnableHistorySync').checked = config.EnableHistorySync || false;
        view.querySelector('#chkHistorySyncPlayedStatus').checked = config.SyncPlayedStatus !== false;
        view.querySelector('#chkHistorySyncPlaybackPosition').checked = config.SyncPlaybackPosition !== false;
        view.querySelector('#chkHistorySyncPlayCount').checked = config.SyncPlayCount !== false;
        view.querySelector('#chkHistorySyncLastPlayedDate').checked = config.SyncLastPlayedDate !== false;
        view.querySelector('#chkHistorySyncFavorites').checked = config.SyncFavorites !== false;
    }

    function saveHistorySettings() {
        var config = currentConfig || {};
        config.EnableHistorySync = view.querySelector('#chkEnableHistorySync').checked;
        config.SyncPlayedStatus = view.querySelector('#chkHistorySyncPlayedStatus').checked;
        config.SyncPlaybackPosition = view.querySelector('#chkHistorySyncPlaybackPosition').checked;
        config.SyncPlayCount = view.querySelector('#chkHistorySyncPlayCount').checked;
        config.SyncLastPlayedDate = view.querySelector('#chkHistorySyncLastPlayedDate').checked;
        config.SyncFavorites = view.querySelector('#chkHistorySyncFavorites').checked;

        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('History settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save history settings');
        });
    }

    // --- Metadata Settings ---

    function loadMetadataSettings(config) {
        view.querySelector('#chkEnableMetadataSync').checked = config.EnableMetadataSync || false;
        view.querySelector('#chkMetadataSyncMetadata').checked = config.MetadataSyncMetadata !== false;
        view.querySelector('#chkMetadataSyncGenres').checked = config.MetadataSyncGenres !== false;
        view.querySelector('#chkMetadataSyncTags').checked = config.MetadataSyncTags !== false;
        view.querySelector('#chkMetadataSyncStudios').checked = config.MetadataSyncStudios !== false;
        view.querySelector('#chkMetadataSyncPeople').checked = config.MetadataSyncPeople === true;
        view.querySelector('#chkMetadataSyncImages').checked = config.MetadataSyncImages !== false;
    }

    function saveMetadataSettings() {
        var config = currentConfig || {};
        config.EnableMetadataSync = view.querySelector('#chkEnableMetadataSync').checked;
        config.MetadataSyncMetadata = view.querySelector('#chkMetadataSyncMetadata').checked;
        config.MetadataSyncGenres = view.querySelector('#chkMetadataSyncGenres').checked;
        config.MetadataSyncTags = view.querySelector('#chkMetadataSyncTags').checked;
        config.MetadataSyncStudios = view.querySelector('#chkMetadataSyncStudios').checked;
        config.MetadataSyncPeople = view.querySelector('#chkMetadataSyncPeople').checked;
        config.MetadataSyncImages = view.querySelector('#chkMetadataSyncImages').checked;

        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('Metadata settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save metadata settings');
        });
    }

    // --- User Sync Settings ---

    function loadUserSyncSettings(config) {
        view.querySelector('#chkEnableUserSync').checked = config.EnableUserSync || false;
        view.querySelector('#chkUserSyncPolicy').checked = config.SyncUserPolicy !== false;
        view.querySelector('#chkUserSyncConfiguration').checked = config.SyncUserConfiguration !== false;
        view.querySelector('#chkUserSyncProfileImage').checked = config.SyncUserProfileImage !== false;
    }

    function saveUserSyncSettings() {
        var config = currentConfig || {};
        config.EnableUserSync = view.querySelector('#chkEnableUserSync').checked;
        config.SyncUserPolicy = view.querySelector('#chkUserSyncPolicy').checked;
        config.SyncUserConfiguration = view.querySelector('#chkUserSyncConfiguration').checked;
        config.SyncUserProfileImage = view.querySelector('#chkUserSyncProfileImage').checked;

        ApiClient.updatePluginConfiguration(pluginId, config).then(function() {
            Dashboard.alert('User sync settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save user sync settings');
        });
    }

    // --- Nested Visibility ---

    function updateNestedVisibility() {
        setVisible('detectUpdatedFilesSettings', view.querySelector('#chkDetectUpdatedFiles').checked);
        setVisible('recyclingBinSettings', view.querySelector('#chkEnableRecyclingBin').checked);
        setVisible('bandwidthScheduleContainer', view.querySelector('#chkEnableBandwidthScheduling').checked);
    }

    // ============================================
    // PAGE INITIALIZATION
    // ============================================

    function showMappingSections() {
        setVisible('librariesSection', true);
        setVisible('usersSection', true);
    }

    function initCollapsibles() {
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
    }

    function initNestedVisibilityHandlers() {
        var chkDetect = view.querySelector('#chkDetectUpdatedFiles');
        var chkRecycle = view.querySelector('#chkEnableRecyclingBin');
        var chkBandwidth = view.querySelector('#chkEnableBandwidthScheduling');
        if (chkDetect) chkDetect.addEventListener('change', updateNestedVisibility);
        if (chkRecycle) chkRecycle.addEventListener('change', updateNestedVisibility);
        if (chkBandwidth) chkBandwidth.addEventListener('change', updateNestedVisibility);
    }

    function loadConfig() {
        ApiClient.getPluginConfiguration(pluginId).then(function(config) {
            currentConfig = config;

            loadServerConfig(config);
            loadContentSettings(config);
            loadHistorySettings(config);
            loadMetadataSettings(config);
            loadUserSyncSettings(config);

            if (config.SourceServerUrl && config.SourceServerApiKey) {
                showMappingSections();
            }

            var promises = [fetchLocalLibraries(), fetchLocalUsers()];
            if (config.SourceServerUrl && config.SourceServerApiKey) {
                promises.push(fetchSourceLibraries(config.SourceServerUrl, config.SourceServerApiKey));
                promises.push(fetchSourceUsers(config.SourceServerUrl, config.SourceServerApiKey));
            }

            Promise.all(promises).then(function() {
                renderLibraryMappings(config.LibraryMappings || []);
                renderUserMappings(config.UserMappings || []);
            });
        });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        console.log('ServerSync Settings: viewshow');
        LibraryMenu.setTabs('serversync', 0, getTabs);

        if (!_initialized) {
            _initialized = true;

            initCollapsibles();
            initNestedVisibilityHandlers();

            // Server actions
            bindClick('btnTestConnection', testConnection);
            bindClick('btnSaveServer', saveServerConfig);

            // Library mapping actions
            bindClick('btnAddMapping', function() { addLibraryMappingRow(); });
            bindClick('btnSaveLibraries', saveLibraries);

            // User mapping actions
            bindClick('btnAddUserMapping', function() { addUserMappingRow(); });
            bindClick('btnSaveUsers', saveUsers);

            // Sync settings actions
            bindClick('btnSaveContentSettings', saveContentSettings);
            bindClick('btnSaveHistorySettings', saveHistorySettings);
            bindClick('btnSaveMetadataSettings', saveMetadataSettings);
            bindClick('btnSaveUserSyncSettings', saveUserSyncSettings);
        }

        loadConfig();
    });
}
