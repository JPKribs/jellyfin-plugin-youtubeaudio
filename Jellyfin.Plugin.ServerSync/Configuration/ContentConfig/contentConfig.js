// Content Sync Configuration module

var ContentConfigModule = {
    currentConfig: null,
    sourceLibraries: [],
    localLibraries: [],
    capabilities: null,

    init: function(config) {
        var self = this;
        self.currentConfig = config;

        document.getElementById('btnAddMapping').addEventListener('click', function() {
            self.addMapping();
        });

        document.getElementById('chkEnableContentSync').addEventListener('change', function() {
            self.updateContentSyncVisibility(this.checked);
        });

        document.getElementById('chkDetectUpdatedFiles').addEventListener('change', function() {
            self.updateDetectUpdatedFilesVisibility(this.checked);
        });

        document.getElementById('chkEnableBandwidthScheduling').addEventListener('change', function() {
            self.updateBandwidthSchedulingVisibility(this.checked);
        });

        document.getElementById('chkEnableRecyclingBin').addEventListener('change', function() {
            self.updateRecyclingBinVisibility(this.checked);
        });

        // Update pending visibility when approval modes change
        document.getElementById('selDownloadNewContentMode').addEventListener('change', function() {
            self.updatePendingVisibilityFromUI();
        });

        document.getElementById('selReplaceExistingContentMode').addEventListener('change', function() {
            self.updatePendingVisibilityFromUI();
        });

        document.getElementById('selDeleteMissingContentMode').addEventListener('change', function() {
            self.updatePendingVisibilityFromUI();
        });
    },

    updatePendingVisibilityFromUI: function() {
        var downloadMode = document.getElementById('selDownloadNewContentMode').value;
        var replaceMode = document.getElementById('selReplaceExistingContentMode').value;
        var deleteMode = document.getElementById('selDeleteMissingContentMode').value;
        this.updatePendingVisibility(downloadMode, replaceMode, deleteMode);
    },

    loadConfig: function(config) {
        var self = this;
        self.currentConfig = config;

        document.getElementById('chkEnableContentSync').checked = config.EnableContentSync || false;
        document.getElementById('chkIncludeExtras').checked = config.IncludeCompanionFiles || false;
        document.getElementById('chkDetectUpdatedFiles').checked = config.DetectUpdatedFiles !== false;
        document.getElementById('selChangeDetectionPolicy').value = config.ChangeDetectionPolicy || 'SizeOnly';

        // Set approval mode dropdowns - config stores string enum names (Enabled, RequireApproval, Disabled)
        var downloadMode = config.DownloadNewContentMode || 'Enabled';
        var replaceMode = config.ReplaceExistingContentMode || 'Enabled';
        var deleteMode = config.DeleteMissingContentMode || 'Disabled';
        document.getElementById('selDownloadNewContentMode').value = downloadMode;
        document.getElementById('selReplaceExistingContentMode').value = replaceMode;
        document.getElementById('selDeleteMissingContentMode').value = deleteMode;

        // Update pending card visibility based on approval modes
        self.updatePendingVisibility(downloadMode, replaceMode, deleteMode);

        document.getElementById('txtMaxConcurrentDownloads').value = config.MaxConcurrentDownloads || 2;
        document.getElementById('txtMaxRetryCount').value = config.MaxRetryCount || 3;
        document.getElementById('txtTempDownloadPath').value = config.TempDownloadPath || '';
        document.getElementById('txtMaxDownloadSpeed').value = config.MaxDownloadSpeed || 0;
        document.getElementById('selDownloadSpeedUnit').value = config.DownloadSpeedUnit || 'MB';
        document.getElementById('txtMinFreeDiskSpace').value = config.MinimumFreeDiskSpaceGb || 10;
        document.getElementById('chkEnableBandwidthScheduling').checked = config.EnableBandwidthScheduling || false;
        document.getElementById('txtScheduledStartHour').value = config.ScheduledStartHour || 0;
        document.getElementById('txtScheduledEndHour').value = config.ScheduledEndHour || 6;
        document.getElementById('txtScheduledDownloadSpeed').value = config.ScheduledDownloadSpeed || 0;
        document.getElementById('selScheduledDownloadSpeedUnit').value = config.ScheduledDownloadSpeedUnit || 'MB';
        document.getElementById('chkEnableRecyclingBin').checked = config.EnableRecyclingBin || false;
        document.getElementById('txtRecyclingBinPath').value = config.RecyclingBinPath || '';
        document.getElementById('txtRecyclingBinRetentionDays').value = config.RecyclingBinRetentionDays || 7;

        self.updateBandwidthSchedulingVisibility(config.EnableBandwidthScheduling);
        self.updateContentSyncVisibility(config.EnableContentSync);
        self.updateDetectUpdatedFilesVisibility(config.DetectUpdatedFiles !== false);
        self.updateRecyclingBinVisibility(config.EnableRecyclingBin);
    },

    getValues: function() {
        return {
            EnableContentSync: document.getElementById('chkEnableContentSync').checked,
            IncludeCompanionFiles: document.getElementById('chkIncludeExtras').checked,
            DetectUpdatedFiles: document.getElementById('chkDetectUpdatedFiles').checked,
            ChangeDetectionPolicy: document.getElementById('selChangeDetectionPolicy').value,
            DownloadNewContentMode: document.getElementById('selDownloadNewContentMode').value,
            ReplaceExistingContentMode: document.getElementById('selReplaceExistingContentMode').value,
            DeleteMissingContentMode: document.getElementById('selDeleteMissingContentMode').value,
            MaxConcurrentDownloads: parseInt(document.getElementById('txtMaxConcurrentDownloads').value, 10) || 2,
            MaxRetryCount: parseInt(document.getElementById('txtMaxRetryCount').value, 10) || 3,
            TempDownloadPath: document.getElementById('txtTempDownloadPath').value || null,
            MaxDownloadSpeed: parseInt(document.getElementById('txtMaxDownloadSpeed').value, 10) || 0,
            DownloadSpeedUnit: document.getElementById('selDownloadSpeedUnit').value,
            MinimumFreeDiskSpaceGb: parseInt(document.getElementById('txtMinFreeDiskSpace').value, 10) || 10,
            EnableBandwidthScheduling: document.getElementById('chkEnableBandwidthScheduling').checked,
            ScheduledStartHour: parseInt(document.getElementById('txtScheduledStartHour').value, 10) || 0,
            ScheduledEndHour: parseInt(document.getElementById('txtScheduledEndHour').value, 10) || 6,
            ScheduledDownloadSpeed: parseInt(document.getElementById('txtScheduledDownloadSpeed').value, 10) || 0,
            ScheduledDownloadSpeedUnit: document.getElementById('selScheduledDownloadSpeedUnit').value,
            EnableRecyclingBin: document.getElementById('chkEnableRecyclingBin').checked,
            RecyclingBinPath: document.getElementById('txtRecyclingBinPath').value,
            RecyclingBinRetentionDays: parseInt(document.getElementById('txtRecyclingBinRetentionDays').value, 10) || 7,
            LibraryMappings: this.collectMappings()
        };
    },

    loadCapabilities: function() {
        var self = this;
        return ServerSyncShared.apiRequest('Capabilities', 'GET').then(function(capabilities) {
            self.capabilities = capabilities;
        }).catch(function() {
            self.capabilities = { CanDeleteItems: false };
        });
    },

    updateBandwidthSchedulingVisibility: function(enabled) {
        document.getElementById('bandwidthScheduleContainer').style.display = enabled ? 'block' : 'none';
    },

    updateContentSyncVisibility: function(enabled) {
        document.getElementById('contentSyncSettings').style.display = enabled ? 'block' : 'none';
    },

    updateDetectUpdatedFilesVisibility: function(enabled) {
        document.getElementById('detectUpdatedFilesSettings').style.display = enabled ? 'block' : 'none';
    },

    updateRecyclingBinVisibility: function(enabled) {
        document.getElementById('recyclingBinSettings').style.display = enabled ? 'block' : 'none';
    },

    updatePendingVisibility: function(downloadMode, replaceMode, deleteMode) {
        // Show pending cards only if approval is required
        var showPendingDownload = downloadMode === 'RequireApproval';
        var showPendingReplace = replaceMode === 'RequireApproval';
        var showPendingDelete = deleteMode === 'RequireApproval';
        var showAnyPending = showPendingDownload || showPendingReplace || showPendingDelete;

        document.getElementById('statusGroupPendingDownload').style.display = showPendingDownload ? 'block' : 'none';
        document.getElementById('statusGroupPendingReplacement').style.display = showPendingReplace ? 'block' : 'none';
        document.getElementById('statusGroupPendingDeletion').style.display = showPendingDelete ? 'block' : 'none';

        // Show/hide the pending row container
        var pendingRow = document.getElementById('pendingStatusRow');
        if (pendingRow) {
            pendingRow.style.display = showAnyPending ? 'flex' : 'none';
        }

        // Also update filter options in SyncTableModule if available
        if (typeof SyncTableModule !== 'undefined' && SyncTableModule.table) {
            SyncTableModule.table.setFilterOptionVisible('optPendingDownload', showPendingDownload);
            SyncTableModule.table.setFilterOptionVisible('optPendingReplacement', showPendingReplace);
            SyncTableModule.table.setFilterOptionVisible('optPendingDeletion', showPendingDelete);
            SyncTableModule.table.setFilterOptionVisible('optDeleting', showPendingDelete);
        }
    },

    fetchSourceLibraries: function(serverUrl, apiKey) {
        var self = this;
        return ServerSyncShared.apiRequest('GetSourceLibraries', 'POST', {
            ServerUrl: serverUrl,
            ApiKey: apiKey
        }).then(function(libraries) {
            self.sourceLibraries = libraries || [];
            self.updateLibrarySelects();
        }).catch(function(err) {
            console.error('Failed to fetch source libraries:', err);
            self.sourceLibraries = [];
        });
    },

    fetchLocalLibraries: function() {
        var self = this;
        return ApiClient.fetch({
            url: ApiClient.getUrl('Library/VirtualFolders'),
            type: 'GET',
            dataType: 'json'
        }).then(function(folders) {
            self.localLibraries = (folders || []).map(function(folder) {
                return {
                    Id: folder.ItemId,
                    Name: folder.Name,
                    Locations: folder.Locations || []
                };
            });
        }).catch(function() {
            self.localLibraries = [];
        });
    },

    updateLibrarySelects: function() {
        var self = this;
        document.querySelectorAll('.sourceLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select source library...</option>';
            self.sourceLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations);
                select.appendChild(option);
            });
            if (savedValue) {
                select.value = savedValue;
            }
        });
        self.updateLocalLibrarySelects();
    },

    updateLocalLibrarySelects: function() {
        var self = this;
        document.querySelectorAll('.localLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select local library...</option>';
            self.localLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations);
                select.appendChild(option);
            });
            if (savedValue) {
                select.value = savedValue;
            }
        });
    },

    renderMappings: function(mappings) {
        var self = this;
        var container = document.getElementById('libraryMappingsContainer');
        container.innerHTML = '';
        mappings.forEach(function(mapping, index) {
            self.addMappingRow(mapping, index);
        });
    },

    addMapping: function() {
        var container = document.getElementById('libraryMappingsContainer');
        this.addMappingRow({}, container.children.length);
    },

    addMappingRow: function(mapping, index) {
        var self = this;
        var container = document.getElementById('libraryMappingsContainer');

        var div = document.createElement('div');
        div.className = 'libraryMapping';
        div.dataset.index = index;

        div.innerHTML =
            '<div class="mappingHeader">' +
                '<label class="checkboxContainer">' +
                    '<input is="emby-checkbox" type="checkbox" class="mappingEnabled" ' + (mapping.IsEnabled ? 'checked' : '') + ' />' +
                    '<span>Enabled</span>' +
                '</label>' +
                '<button is="emby-button" type="button" class="btnRemoveMapping raised button-destructive">' +
                    '<span>Remove</span>' +
                '</button>' +
            '</div>' +
            '<div class="mappingGrid">' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel" for="sourceLibrary' + index + '">Source Library</label>' +
                        '<select is="emby-select" id="sourceLibrary' + index + '" class="sourceLibrarySelect"></select>' +
                    '</div>' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel" for="sourceRootPath' + index + '">Source Root Path</label>' +
                        '<input is="emby-input" type="text" id="sourceRootPath' + index + '" class="sourceRootPath" value="' + ServerSyncShared.escapeHtml(mapping.SourceRootPath || '') + '" />' +
                    '</div>' +
                '</div>' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel" for="localLibrary' + index + '">Local Library</label>' +
                        '<select is="emby-select" id="localLibrary' + index + '" class="localLibrarySelect"></select>' +
                    '</div>' +
                    '<div class="inputContainer">' +
                        '<label class="inputLabel" for="localRootPath' + index + '">Local Root Path</label>' +
                        '<input is="emby-input" type="text" id="localRootPath' + index + '" class="localRootPath" value="' + ServerSyncShared.escapeHtml(mapping.LocalRootPath || '') + '" />' +
                    '</div>' +
                '</div>' +
            '</div>';

        container.appendChild(div);

        // Populate source library select
        var sourceSelect = div.querySelector('.sourceLibrarySelect');
        if (mapping.SourceLibraryId) {
            sourceSelect.dataset.savedValue = mapping.SourceLibraryId;
        }
        sourceSelect.innerHTML = '<option value="">Select source library...</option>';
        self.sourceLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations);
            sourceSelect.appendChild(option);
        });

        if (mapping.SourceLibraryId) {
            sourceSelect.value = mapping.SourceLibraryId;
        }

        sourceSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) {
                    div.querySelector('.sourceRootPath').value = locations[0];
                }
            }
        });

        // Populate local library select
        var localSelect = div.querySelector('.localLibrarySelect');
        if (mapping.LocalLibraryId) {
            localSelect.dataset.savedValue = mapping.LocalLibraryId;
        }
        localSelect.innerHTML = '<option value="">Select local library...</option>';
        self.localLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations);
            localSelect.appendChild(option);
        });

        if (mapping.LocalLibraryId) {
            localSelect.value = mapping.LocalLibraryId;
        }

        localSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) {
                    div.querySelector('.localRootPath').value = locations[0];
                }
            }
        });

        div.querySelector('.btnRemoveMapping').addEventListener('click', function() {
            div.remove();
        });
    },

    collectMappings: function() {
        var mappings = [];
        document.querySelectorAll('.libraryMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceLibrarySelect');
            var sourceOption = sourceSelect.options[sourceSelect.selectedIndex];
            var localSelect = row.querySelector('.localLibrarySelect');
            var localOption = localSelect.options[localSelect.selectedIndex];

            mappings.push({
                IsEnabled: row.querySelector('.mappingEnabled').checked,
                SourceLibraryId: sourceSelect.value,
                SourceLibraryName: sourceOption ? sourceOption.textContent : '',
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: localSelect.value,
                LocalLibraryName: localOption ? localOption.textContent : '',
                LocalRootPath: row.querySelector('.localRootPath').value
            });
        });
        return mappings;
    }
};
