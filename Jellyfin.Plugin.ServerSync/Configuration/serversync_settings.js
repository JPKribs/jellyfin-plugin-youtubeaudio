// ============================================
// SETTINGS - PAGE CONTROLLER
// ============================================

export default function (view) {
    'use strict';

    // ============================================
    // TAB NAVIGATION (local copy for synchronous access)
    // ============================================

    function getTabs() {
        return [
            { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
            { href: 'configurationpage?name=serversync_settings', name: 'Settings' }
        ];
    }

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var _sharedPromise = import('/web/configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
    });

    // ============================================
    // CONSTANTS & STATE
    // ============================================
    var _initialized = false;

    var currentConfig = null;
    var sourceLibraries = [];
    var localLibraries = [];
    var sourceUsers = [];
    var localUsers = [];

    // ============================================
    // UTILITY ALIASES (delegate to shared module)
    // ============================================

    function escapeHtml(str) {
        return ServerSyncShared.escapeHtml(str);
    }

    function apiRequest(endpoint, method, data) {
        return ServerSyncShared.apiRequest(endpoint, method, data);
    }

    function setVisible(elementId, visible) {
        ServerSyncShared.setVisible(elementId, visible);
    }

    function bindClick(id, handler) {
        return ServerSyncShared.bindClick(id, handler);
    }

    // Safe DOM accessors for load/save settings
    function getEl(id) {
        return view.querySelector('#' + id);
    }

    function setChecked(id, value) {
        var el = getEl(id);
        if (el) el.checked = value;
    }

    function getChecked(id) {
        var el = getEl(id);
        return el ? el.checked : false;
    }

    function setValue(id, value) {
        var el = getEl(id);
        if (el) el.value = value;
    }

    function getValue(id, fallback) {
        var el = getEl(id);
        return el ? el.value : (fallback || '');
    }

    function getIntValue(id, fallback) {
        return parseInt(getValue(id, '0')) || fallback;
    }

    // ============================================
    // SERVER MODULE
    // ============================================

    // --- Server Configuration ---

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

        // Load authenticated user if present
        if (config.SourceServerAuthenticatedUser) {
            var authUserEl = view.querySelector('#txtAuthenticatedUser');
            if (authUserEl) authUserEl.textContent = config.SourceServerAuthenticatedUser;
            setVisible('authenticatedUserRow', true);

            // Pre-fill the username field for convenience
            var usernameEl = view.querySelector('#txtAuthUsername');
            if (usernameEl) usernameEl.value = config.SourceServerAuthenticatedUser;
        } else {
            setVisible('authenticatedUserRow', false);
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

        // Note: SourceServerAuthenticatedUser is set by generateToken, not here
        // If the API key was manually changed, clear the authenticated user
        // (This is handled by generateToken setting it, and manual edits won't update it)

        ServerSyncShared.saveConfig(config).then(function() {
            Dashboard.alert('Server settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save server settings');
        });
    }

    // --- Token Generation ---

    function generateToken() {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var usernameEl = view.querySelector('#txtAuthUsername');
        var passwordEl = view.querySelector('#txtAuthPassword');
        var statusEl = view.querySelector('#tokenGeneratorStatus');

        var serverUrl = urlEl ? urlEl.value : '';
        var username = usernameEl ? usernameEl.value : '';
        var password = passwordEl ? passwordEl.value : '';

        if (!serverUrl) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter a Server URL first</span>';
            return;
        }

        if (!username || !password) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Username and password are required</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Authenticating...';

        apiRequest('Authenticate', 'POST', {
            ServerUrl: serverUrl,
            Username: username,
            Password: password
        }).then(function(response) {
            if (response && response.Success) {
                // Clear the password field for security
                if (passwordEl) passwordEl.value = '';

                // Update the API key field with the new token
                var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
                if (apiKeyEl) apiKeyEl.value = response.AccessToken;

                // Update the authenticated user display
                var authUserEl = view.querySelector('#txtAuthenticatedUser');
                if (authUserEl) authUserEl.textContent = response.Username || username;
                setVisible('authenticatedUserRow', true);

                // Update server info display
                var nameEl = view.querySelector('#txtSourceServerName');
                var idEl = view.querySelector('#txtSourceServerId');
                if (nameEl) nameEl.textContent = response.ServerName || 'Unknown';
                if (idEl) idEl.textContent = response.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);

                // Update currentConfig
                if (currentConfig) {
                    currentConfig.SourceServerApiKey = response.AccessToken;
                    currentConfig.SourceServerAuthenticatedUser = response.Username || username;
                    currentConfig.SourceServerName = response.ServerName;
                    currentConfig.SourceServerId = response.ServerId;
                }

                // Save the configuration automatically
                var config = currentConfig || {};
                config.SourceServerUrl = serverUrl;
                config.SourceServerApiKey = response.AccessToken;
                config.SourceServerAuthenticatedUser = response.Username || username;
                config.SourceServerName = response.ServerName;
                config.SourceServerId = response.ServerId;

                ServerSyncShared.saveConfig(config).then(function() {
                    if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated and saved!</span>';

                    // Fetch source data now that we have valid credentials
                    fetchSourceLibraries(serverUrl, response.AccessToken);
                    fetchSourceUsers(serverUrl, response.AccessToken);
                    showMappingSections();
                }).catch(function() {
                    if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated!</span> <span class="text-error">(Save failed)</span>';
                });
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Authentication failed') + '</span>';
            }
        }).catch(function(error) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Authentication failed</span>';
            console.error('Token generation error:', error);
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
            '</div>' +
            '<div class="ignoredPathsSection">' +
                '<label class="inputLabel ignoredPathsLabel">Ignored Paths</label>' +
                '<div class="fieldDescription">Folder paths to skip during sync (relative to source root). Use * as a wildcard (e.g. Star Wars*). Applies to Content, Metadata, and History sync.</div>' +
                '<div class="ignoredPathsList"></div>' +
                '<button is="emby-button" type="button" class="btnAddIgnoredPath raised button-secondary button-small">' +
                    '<span>+ Add Ignored Path</span>' +
                '</button>' +
            '</div>';

        container.appendChild(div);

        // Wire up ignored paths add/remove
        var ignoredList = div.querySelector('.ignoredPathsList');
        var sourceRootInput = div.querySelector('.sourceRootPath');

        function getSourceRootPrefix() {
            var root = (sourceRootInput ? sourceRootInput.value : '').replace(/\\/g, '/').replace(/\/+$/, '');
            return root ? root + '/' : '';
        }

        function updateAllIgnoredPrefixes() {
            var prefix = getSourceRootPrefix();
            div.querySelectorAll('.ignoredPathPrefix').forEach(function(span) {
                span.textContent = prefix;
            });
        }

        function addIgnoredPathRow(pathValue) {
            var row = document.createElement('div');
            row.className = 'ignoredPathRow';
            row.innerHTML =
                '<span class="ignoredPathPrefix">' + escapeHtml(getSourceRootPrefix()) + '</span>' +
                '<input is="emby-input" type="text" class="ignoredPathInput" placeholder="e.g. The Simpsons or Star Wars*" value="' + escapeHtml(pathValue || '') + '" />' +
                '<button is="emby-button" type="button" class="btnRemoveIgnoredPath raised button-destructive button-small" title="Remove"><span class="material-icons">delete</span></button>';
            ignoredList.appendChild(row);
            row.querySelector('.btnRemoveIgnoredPath').addEventListener('click', function() { row.remove(); });
        }
        (mapping.IgnoredPaths || []).forEach(function(p) { addIgnoredPathRow(p); });
        div.querySelector('.btnAddIgnoredPath').addEventListener('click', function() { addIgnoredPathRow(''); });

        // Keep prefix in sync when source root path changes
        if (sourceRootInput) {
            sourceRootInput.addEventListener('input', updateAllIgnoredPrefixes);
        }

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
            updateAllIgnoredPrefixes();
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
            var ignoredPaths = [];
            row.querySelectorAll('.ignoredPathInput').forEach(function(input) {
                var val = input.value.trim();
                if (val.length > 0) { ignoredPaths.push(val); }
            });
            mappings.push({
                IsEnabled: row.querySelector('.mappingEnabled').checked,
                SourceLibraryId: sourceSelect.value,
                SourceLibraryName: sourceSelect.options[sourceSelect.selectedIndex] ? sourceSelect.options[sourceSelect.selectedIndex].textContent : '',
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: localSelect.value,
                LocalLibraryName: localSelect.options[localSelect.selectedIndex] ? localSelect.options[localSelect.selectedIndex].textContent : '',
                LocalRootPath: row.querySelector('.localRootPath').value,
                IgnoredPaths: ignoredPaths
            });
        });
        return mappings;
    }

    function saveLibraries() {
        var config = currentConfig || {};
        config.LibraryMappings = collectLibraryMappings();
        ServerSyncShared.saveConfig(config).then(function() {
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
        ServerSyncShared.saveConfig(config).then(function() {
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
        setChecked('chkEnableContentSync', config.EnableContentSync || false);
        setChecked('chkDetectUpdatedFiles', config.DetectUpdatedFiles !== false);
        setValue('selChangeDetectionPolicy', config.ChangeDetectionPolicy || 'SizeOnly');
        setChecked('chkIncludeExtras', config.IncludeCompanionFiles || false);
        setValue('selDownloadNewContentMode', config.DownloadNewContentMode || 'Enabled');
        setValue('selReplaceExistingContentMode', config.ReplaceExistingContentMode || 'Enabled');
        setValue('selDeleteMissingContentMode', config.DeleteMissingContentMode || 'Disabled');
        setChecked('chkEnableRecyclingBin', config.EnableRecyclingBin || false);
        setValue('txtRecyclingBinPath', config.RecyclingBinPath || '');
        setValue('txtRecyclingBinRetentionDays', config.RecyclingBinRetentionDays || 7);
        setChecked('chkRemoveEmptyFolders', config.RemoveEmptyFoldersOnDelete || false);
        setValue('txtMaxConcurrentDownloads', config.MaxConcurrentDownloads || 2);
        setValue('txtMaxRetryCount', config.MaxRetryCount || 3);
        setValue('txtTempDownloadPath', config.TempDownloadPath || '');
        setValue('txtMaxDownloadSpeed', config.MaxDownloadSpeed || 0);
        setValue('selDownloadSpeedUnit', config.DownloadSpeedUnit || 'MB');
        setValue('txtMinFreeDiskSpace', config.MinimumFreeDiskSpaceGb || 10);
        setChecked('chkEnableBandwidthScheduling', config.EnableBandwidthScheduling || false);
        setValue('txtScheduledStartHour', config.ScheduledStartHour || 0);
        setValue('txtScheduledEndHour', config.ScheduledEndHour || 6);
        setValue('txtScheduledDownloadSpeed', config.ScheduledDownloadSpeed || 0);
        setValue('selScheduledDownloadSpeedUnit', config.ScheduledDownloadSpeedUnit || 'MB');

        updateNestedVisibility();
    }

    function saveContentSettings() {
        var config = currentConfig || {};
        config.EnableContentSync = getChecked('chkEnableContentSync');
        config.DetectUpdatedFiles = getChecked('chkDetectUpdatedFiles');
        config.ChangeDetectionPolicy = getValue('selChangeDetectionPolicy', 'SizeOnly');
        config.IncludeCompanionFiles = getChecked('chkIncludeExtras');
        config.DownloadNewContentMode = getValue('selDownloadNewContentMode', 'Enabled');
        config.ReplaceExistingContentMode = getValue('selReplaceExistingContentMode', 'Enabled');
        config.DeleteMissingContentMode = getValue('selDeleteMissingContentMode', 'Disabled');
        config.EnableRecyclingBin = getChecked('chkEnableRecyclingBin');
        config.RecyclingBinPath = getValue('txtRecyclingBinPath');
        config.RecyclingBinRetentionDays = getIntValue('txtRecyclingBinRetentionDays', 7);
        config.RemoveEmptyFoldersOnDelete = getChecked('chkRemoveEmptyFolders');
        config.MaxConcurrentDownloads = getIntValue('txtMaxConcurrentDownloads', 2);
        config.MaxRetryCount = getIntValue('txtMaxRetryCount', 3);
        config.TempDownloadPath = getValue('txtTempDownloadPath') || null;
        config.MaxDownloadSpeed = getIntValue('txtMaxDownloadSpeed', 0);
        config.DownloadSpeedUnit = getValue('selDownloadSpeedUnit', 'MB');
        config.MinimumFreeDiskSpaceGb = getIntValue('txtMinFreeDiskSpace', 10);
        config.EnableBandwidthScheduling = getChecked('chkEnableBandwidthScheduling');
        config.ScheduledStartHour = getIntValue('txtScheduledStartHour', 0);
        config.ScheduledEndHour = getIntValue('txtScheduledEndHour', 6);
        config.ScheduledDownloadSpeed = getIntValue('txtScheduledDownloadSpeed', 0);
        config.ScheduledDownloadSpeedUnit = getValue('selScheduledDownloadSpeedUnit', 'MB');

        ServerSyncShared.saveConfig(config).then(function() {
            Dashboard.alert('Content settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save content settings');
        });
    }

    // --- History Settings ---

    function loadHistorySettings(config) {
        setChecked('chkEnableHistorySync', config.EnableHistorySync || false);
        setChecked('chkHistorySyncPlayedStatus', config.SyncPlayedStatus !== false);
        setChecked('chkHistorySyncPlaybackPosition', config.SyncPlaybackPosition !== false);
        setChecked('chkHistorySyncPlayCount', config.SyncPlayCount !== false);
        setChecked('chkHistorySyncLastPlayedDate', config.SyncLastPlayedDate !== false);
        setChecked('chkHistorySyncFavorites', config.SyncFavorites !== false);
    }

    function saveHistorySettings() {
        var config = currentConfig || {};
        config.EnableHistorySync = getChecked('chkEnableHistorySync');
        config.SyncPlayedStatus = getChecked('chkHistorySyncPlayedStatus');
        config.SyncPlaybackPosition = getChecked('chkHistorySyncPlaybackPosition');
        config.SyncPlayCount = getChecked('chkHistorySyncPlayCount');
        config.SyncLastPlayedDate = getChecked('chkHistorySyncLastPlayedDate');
        config.SyncFavorites = getChecked('chkHistorySyncFavorites');

        ServerSyncShared.saveConfig(config).then(function() {
            Dashboard.alert('History settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save history settings');
        });
    }

    // --- Metadata Settings ---

    function loadMetadataSettings(config) {
        setChecked('chkEnableMetadataSync', config.EnableMetadataSync || false);
        setChecked('chkMetadataSyncMetadata', config.MetadataSyncMetadata !== false);
        setChecked('chkMetadataSyncGenres', config.MetadataSyncGenres !== false);
        setChecked('chkMetadataSyncTags', config.MetadataSyncTags !== false);
        setChecked('chkMetadataSyncStudios', config.MetadataSyncStudios !== false);
        setChecked('chkMetadataSyncPeople', config.MetadataSyncPeople === true);
        setChecked('chkMetadataSyncImages', config.MetadataSyncImages !== false);
        setValue('selMetadataRefreshMode', config.MetadataRefreshMode || 'FullRefresh');
    }

    function saveMetadataSettings() {
        var config = currentConfig || {};
        config.EnableMetadataSync = getChecked('chkEnableMetadataSync');
        config.MetadataSyncMetadata = getChecked('chkMetadataSyncMetadata');
        config.MetadataSyncGenres = getChecked('chkMetadataSyncGenres');
        config.MetadataSyncTags = getChecked('chkMetadataSyncTags');
        config.MetadataSyncStudios = getChecked('chkMetadataSyncStudios');
        config.MetadataSyncPeople = getChecked('chkMetadataSyncPeople');
        config.MetadataSyncImages = getChecked('chkMetadataSyncImages');
        config.MetadataRefreshMode = getValue('selMetadataRefreshMode', 'FullRefresh');

        ServerSyncShared.saveConfig(config).then(function() {
            Dashboard.alert('Metadata settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save metadata settings');
        });
    }

    // --- User Sync Settings ---

    function loadUserSyncSettings(config) {
        setChecked('chkEnableUserSync', config.EnableUserSync || false);
        setChecked('chkUserSyncPolicy', config.SyncUserPolicy !== false);
        setChecked('chkUserSyncConfiguration', config.SyncUserConfiguration !== false);
        setChecked('chkUserSyncProfileImage', config.SyncUserProfileImage !== false);
    }

    function saveUserSyncSettings() {
        var config = currentConfig || {};
        config.EnableUserSync = getChecked('chkEnableUserSync');
        config.SyncUserPolicy = getChecked('chkUserSyncPolicy');
        config.SyncUserConfiguration = getChecked('chkUserSyncConfiguration');
        config.SyncUserProfileImage = getChecked('chkUserSyncProfileImage');

        ServerSyncShared.saveConfig(config).then(function() {
            Dashboard.alert('User sync settings saved');
        }).catch(function() {
            Dashboard.alert('Failed to save user sync settings');
        });
    }

    // --- Nested Visibility ---

    function updateNestedVisibility() {
        setVisible('detectUpdatedFilesSettings', getChecked('chkDetectUpdatedFiles'));
        setVisible('recyclingBinSettings', getChecked('chkEnableRecyclingBin'));
        setVisible('bandwidthScheduleContainer', getChecked('chkEnableBandwidthScheduling'));
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
                    var isExpanded = !this.classList.contains('collapsed');
                    this.setAttribute('aria-expanded', String(isExpanded));
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
        ServerSyncShared.getConfig().then(function(config) {
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
        }).catch(function() {
            Dashboard.alert('Failed to load plugin configuration');
        });
    }

    // ============================================
    // TROUBLESHOOTING: DATABASE RESET
    // ============================================

    function resetTable(endpoint, tableName) {
        if (!confirm('Are you sure you want to reset the ' + tableName + ' table?\n\nThis will delete all ' + tableName + ' tracking data and you will need to re-sync. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest(endpoint, 'POST').then(function() {
            ServerSyncShared.showAlert('The ' + tableName + ' table has been reset.');
        }).catch(function(err) {
            console.error(endpoint + ' error:', err);
            ServerSyncShared.showAlert('Failed to reset ' + tableName + ' table.');
        });
    }

    function resetEntireDatabase() {
        if (!confirm('Are you sure you want to reset the ENTIRE sync database?\n\nThis will delete ALL tracking data across all sync types (Content, History, Metadata, Users). You will need to re-sync everything from scratch. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest('ResetSyncDatabase', 'POST').then(function() {
            ServerSyncShared.showAlert('The entire sync database has been reset.');
        }).catch(function(err) {
            console.error('ResetSyncDatabase error:', err);
            ServerSyncShared.showAlert('Failed to reset sync database.');
        });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('serversync', 1, getTabs);

        _sharedPromise.then(function() {
            if (!_initialized) {
                _initialized = true;

                initCollapsibles();
                initNestedVisibilityHandlers();

                // Server actions
                bindClick('btnTestConnection', testConnection);
                bindClick('btnSaveServer', saveServerConfig);
                bindClick('btnGenerateToken', generateToken);

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

                // Troubleshooting: Database reset actions
                bindClick('btnResetContentTable', function() { resetTable('ResetContentSyncDatabase', 'content sync'); });
                bindClick('btnResetHistoryTable', function() { resetTable('ResetHistorySyncDatabase', 'history sync'); });
                bindClick('btnResetMetadataTable', function() { resetTable('ResetMetadataSyncDatabase', 'metadata sync'); });
                bindClick('btnResetUserTable', function() { resetTable('ResetUserSyncDatabase', 'user sync'); });
                bindClick('btnResetEntireDatabase', resetEntireDatabase);
            }

            loadConfig();
        });
    });
}
