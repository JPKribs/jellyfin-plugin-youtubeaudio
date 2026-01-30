// Sync Table module
// Uses the generic PaginatedTable component for content sync items

var SyncTableModule = {
    table: null,
    currentModalItem: null,
    capabilities: null,

    init: function() {
        var self = this;

        // Create the paginated table instance
        this.table = new PaginatedTable({
            containerId: 'syncItemsTableContainer',
            endpoint: 'Items',

            columns: [
                {
                    key: 'name',
                    label: 'Name',
                    type: 'custom',
                    render: function(item) {
                        var fileName = ServerSyncShared.getFileName(item.SourcePath);
                        var localExists = item.Status === 'Synced';
                        var localPathClass = localExists ? '' : ' notExists';
                        var localPathDisplay = item.LocalPath
                            ? ServerSyncShared.getFileName(item.LocalPath)
                            : 'N/A';

                        var willSync = !localExists && item.LocalPath &&
                            item.Status !== 'Ignored' &&
                            !(item.Status === 'Pending' && item.PendingType === 'Deletion');
                        if (willSync) localPathDisplay = '(will sync to) ' + localPathDisplay;

                        var errorPreview = '';
                        if (item.Status === 'Errored' && item.ErrorMessage) {
                            errorPreview = '<div class="syncItemError" title="' +
                                ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                        }

                        return '<div class="syncItemInfo">' +
                            '<div class="syncItemName" title="' +
                                ServerSyncShared.escapeHtml(item.SourcePath) + '">' +
                                ServerSyncShared.escapeHtml(fileName) + '</div>' +
                            '<div class="syncItemPath' + localPathClass + '" title="' +
                                ServerSyncShared.escapeHtml(item.LocalPath || '') + '">' +
                                ServerSyncShared.escapeHtml(localPathDisplay) + '</div>' +
                            errorPreview +
                        '</div>';
                    }
                },
                {
                    key: 'details',
                    label: 'Details',
                    hidden: true,  // Hidden for content sync, available for future use
                    type: 'custom',
                    render: function() {
                        return '';
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
                idKey: 'SourceItemId',
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
                    { value: 'Errored', label: 'Errored' },
                    { value: 'Ignored', label: 'Ignored' },
                    { value: 'Pending', label: 'Pending' },
                    { value: 'Pending:Download', label: 'Pending Download', id: 'optPendingDownload', hidden: true },
                    { value: 'Pending:Replacement', label: 'Pending Replace', id: 'optPendingReplacement', hidden: true },
                    { value: 'Pending:Deletion', label: 'Pending Delete', id: 'optPendingDeletion', hidden: true },
                    { value: 'Deleting', label: 'Deleting', id: 'optDeleting', hidden: true }
                ],
                buildParams: function(filterValue) {
                    if (filterValue.indexOf(':') > -1) {
                        var parts = filterValue.split(':');
                        return { status: parts[0], pendingType: parts[1] };
                    }
                    return { status: filterValue };
                }
            },

            actions: {
                onRowClick: function(item) {
                    self.showItemDetail(item.SourceItemId);
                },
                onReload: function() {
                    self.loadSyncStatus();
                    self.loadHealthStats();
                }
            },

            getDisplayStatus: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending ' + item.PendingType;
                }
                return item.Status;
            },

            getStatusClass: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending-' + item.PendingType;
                }
                return item.Status;
            },

            emptyState: {
                message: 'No items found'
            }
        });

        // Bind module-specific events (buttons outside the table)
        this._bindModuleEvents();

        // Inject bulk action buttons into the table's bulk actions container
        this._injectBulkActions();
    },

    _bindModuleEvents: function() {
        var self = this;

        // Action buttons
        document.getElementById('btnRefreshItems').addEventListener('click', function() { self.refreshSyncTable(); });
        document.getElementById('btnTriggerSync').addEventListener('click', function() { self.triggerSync(); });
        document.getElementById('btnRetryErrors').addEventListener('click', function() { self.retryErrors(); });
        document.getElementById('btnResetDatabase').addEventListener('click', function() { self.resetDatabase(); });

        // Modal buttons
        document.getElementById('btnModalIgnore').addEventListener('click', function() { self.modalIgnore(); });
        document.getElementById('btnModalQueue').addEventListener('click', function() { self.modalQueue(); });
        document.getElementById('btnModalDelete').addEventListener('click', function() { self.modalDelete(); });
        document.getElementById('btnModalClose').addEventListener('click', function() { self.closeModal(); });
    },

    _injectBulkActions: function() {
        var self = this;
        var bulkContainer = this.table.getBulkActionsContainer();
        if (!bulkContainer) return;

        // Create bulk action buttons
        bulkContainer.innerHTML =
            '<button is="emby-button" type="button" id="btnBulkIgnore" class="raised" disabled><span>Ignore</span></button>' +
            '<button is="emby-button" type="button" id="btnBulkQueue" class="raised button-primary" disabled><span>Queue</span></button>' +
            '<button is="emby-button" type="button" id="btnBulkDelete" class="raised button-destructive" title="Delete from local server only" disabled><span>Delete</span></button>';

        // Bind events
        document.getElementById('btnBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
        document.getElementById('btnBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        document.getElementById('btnBulkDelete').addEventListener('click', function() { self.bulkDelete(); });
    },

    // Backward compatibility - expose items through table
    get syncItems() {
        return this.table ? this.table.getItems() : [];
    },

    get filteredItems() {
        return this.table ? this.table.getItems() : [];
    },

    get selectedItems() {
        return this.table ? new Set(this.table.getSelectedIds()) : new Set();
    },

    loadCapabilities: function() {
        var self = this;
        return ServerSyncShared.apiRequest('Capabilities', 'GET').then(function(capabilities) {
            self.capabilities = capabilities;
            self.updateDeleteCapabilityVisibility(capabilities.CanDeleteItems);
        }).catch(function() {
            self.capabilities = { CanDeleteItems: false };
            self.updateDeleteCapabilityVisibility(false);
        });
    },

    updateDeleteCapabilityVisibility: function(canDelete) {
        var bulkDeleteBtn = document.getElementById('btnBulkDelete');
        var modalDeleteRow = document.getElementById('modalDeleteRow');

        if (bulkDeleteBtn) {
            bulkDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
        }
        if (modalDeleteRow) {
            modalDeleteRow.style.display = canDelete ? 'block' : 'none';
        }
    },

    updatePendingVisibility: function(requireApproval) {
        document.getElementById('statusGroupPendingDownload').style.display = requireApproval ? 'block' : 'none';
        this.table.setFilterOptionVisible('optPendingDownload', requireApproval);
    },

    updateReplacementVisibility: function(requireApproval) {
        document.getElementById('statusGroupPendingReplacement').style.display = requireApproval ? 'block' : 'none';
        this.table.setFilterOptionVisible('optPendingReplacement', requireApproval);
    },

    updateDeletionVisibility: function(deleteIfMissing) {
        document.getElementById('statusGroupPendingDeletion').style.display = deleteIfMissing ? 'block' : 'none';
        this.table.setFilterOptionVisible('optPendingDeletion', deleteIfMissing);
        document.getElementById('statusGroupDeleting').style.display = deleteIfMissing ? 'block' : 'none';
        this.table.setFilterOptionVisible('optDeleting', deleteIfMissing);
    },

    loadHealthStats: function() {
        return ServerSyncShared.apiRequest('Stats', 'GET').then(function(stats) {
            // Last sync time
            var lastSyncEl = document.getElementById('healthLastSync');
            if (stats.LastSyncEndTime) {
                var lastSync = new Date(stats.LastSyncEndTime);
                lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                lastSyncEl.className = 'healthValue success';
            } else {
                lastSyncEl.textContent = 'Never';
                lastSyncEl.className = 'healthValue';
            }

            // Disk space
            var diskSpaceEl = document.getElementById('healthDiskSpace');
            if (stats.FreeDiskSpaceBytes > 0) {
                var freeSpace = ServerSyncShared.formatSize(stats.FreeDiskSpaceBytes);
                diskSpaceEl.textContent = freeSpace + ' free';
                if (stats.HasSufficientDiskSpace) {
                    diskSpaceEl.className = 'healthValue success';
                } else {
                    diskSpaceEl.className = 'healthValue error';
                }
            } else {
                diskSpaceEl.textContent = 'Unknown';
                diskSpaceEl.className = 'healthValue';
            }

            // Queued size
            var queuedSizeEl = document.getElementById('healthQueuedSize');
            queuedSizeEl.textContent = ServerSyncShared.formatSize(stats.TotalQueuedBytes);
            queuedSizeEl.className = stats.QueuedItems > 0 ? 'healthValue warning' : 'healthValue';
        }).catch(function() {
            // Ignore errors
        });
    },

    triggerSync: function() {
        var self = this;
        var btn = document.getElementById('btnTriggerSync');
        btn.disabled = true;
        btn.querySelector('span').textContent = 'Starting...';

        ServerSyncShared.apiRequest('TriggerSync', 'POST').then(function() {
            ServerSyncShared.showAlert('Sync task started');
            btn.querySelector('span').textContent = 'Sync';
            btn.disabled = false;
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to start sync task');
            btn.querySelector('span').textContent = 'Sync';
            btn.disabled = false;
        });
    },

    refreshSyncTable: function() {
        var self = this;
        var btn = document.getElementById('btnRefreshItems');
        btn.disabled = true;
        btn.querySelector('span').textContent = 'Refreshing...';

        ServerSyncShared.apiRequest('TriggerRefresh', 'POST').then(function() {
            ServerSyncShared.showAlert('Refresh task started');
            btn.querySelector('span').textContent = 'Refresh';
            btn.disabled = false;
            // Also reload the items display
            self.loadSyncStatus();
            self.loadSyncItems();
            self.loadHealthStats();
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to start refresh task');
            btn.querySelector('span').textContent = 'Refresh';
            btn.disabled = false;
        });
    },

    retryErrors: function() {
        var self = this;

        ServerSyncShared.apiRequest('RetryErroredItems', 'POST', {}).then(function() {
            ServerSyncShared.showAlert('Errored items queued for retry');
            self.loadSyncStatus();
            self.loadSyncItems();
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to retry errored items');
        });
    },

    resetDatabase: function() {
        var self = this;

        if (!confirm('Are you sure you want to reset the sync database?\n\nThis will delete ALL tracking data and you will need to re-sync everything. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest('ResetSyncDatabase', 'POST').then(function() {
            ServerSyncShared.showAlert('Sync database has been reset');
            self.loadSyncStatus();
            self.loadSyncItems();
            self.loadHealthStats();
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to reset sync database');
        });
    },

    loadSyncStatus: function() {
        return ServerSyncShared.apiRequest('Status', 'GET').then(function(status) {
            var syncedCount = status.Synced || 0;
            var queuedCount = status.Queued || 0;
            var erroredCount = status.Errored || 0;
            var ignoredCount = status.Ignored || 0;
            var pendingDownloadCount = status.PendingDownload || 0;
            var pendingReplacementCount = status.PendingReplacement || 0;
            var pendingDeletionCount = status.PendingDeletion || 0;
            var deletingCount = status.Deleting || 0;

            document.getElementById('syncedCount').textContent = syncedCount;
            document.getElementById('statusGroupSynced').setAttribute('title', 'Synced: ' + syncedCount);

            document.getElementById('queuedCount').textContent = queuedCount;
            document.getElementById('statusGroupQueued').setAttribute('title', 'Queued: ' + queuedCount);

            document.getElementById('erroredCount').textContent = erroredCount;
            document.getElementById('statusGroupErrored').setAttribute('title', 'Errored: ' + erroredCount);

            document.getElementById('ignoredCount').textContent = ignoredCount;
            document.getElementById('statusGroupIgnored').setAttribute('title', 'Ignored: ' + ignoredCount);

            document.getElementById('pendingDownloadCount').textContent = pendingDownloadCount;
            document.getElementById('statusGroupPendingDownload').setAttribute('title', 'Pending Download: ' + pendingDownloadCount);

            document.getElementById('pendingReplacementCount').textContent = pendingReplacementCount;
            document.getElementById('statusGroupPendingReplacement').setAttribute('title', 'Pending Replacement: ' + pendingReplacementCount);

            document.getElementById('pendingDeletionCount').textContent = pendingDeletionCount;
            document.getElementById('statusGroupPendingDeletion').setAttribute('title', 'Pending Deletion: ' + pendingDeletionCount);

            document.getElementById('deletingCount').textContent = deletingCount;
            document.getElementById('statusGroupDeleting').setAttribute('title', 'Deleting: ' + deletingCount);
        });
    },

    loadSyncItems: function() {
        return this.table.load();
    },

    updateBulkActionsVisibility: function(count) {
        var hasSelection = count > 0;
        var ignoreBtn = document.getElementById('btnBulkIgnore');
        var queueBtn = document.getElementById('btnBulkQueue');
        var deleteBtn = document.getElementById('btnBulkDelete');

        if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
        if (queueBtn) queueBtn.disabled = !hasSelection;
        if (deleteBtn) deleteBtn.disabled = !hasSelection;
    },

    bulkIgnore: function() {
        this.bulkAction('IgnoreItems');
    },

    bulkQueue: function() {
        var self = this;
        var ids = this.table.getSelectedIds();
        if (ids.length === 0) return;

        // Filter out pending deletion items - queuing them is a no-op
        var items = this.table.getItems();
        var filteredIds = ids.filter(function(id) {
            var item = items.find(function(i) { return i.SourceItemId === id; });
            if (item && item.Status === 'Pending' && item.PendingType === 'Deletion') {
                return false;
            }
            return true;
        });

        if (filteredIds.length === 0) {
            ServerSyncShared.showAlert('No items to queue (deletion items cannot be queued)');
            return;
        }

        ServerSyncShared.apiRequest('QueueItems', 'POST', { SourceItemIds: filteredIds }).then(function() {
            self.table.clearSelection();
            self.loadSyncStatus();
            self.loadSyncItems();
            ServerSyncShared.showAlert(filteredIds.length + ' item(s) queued');
        }).catch(function(err) {
            console.error('Bulk queue failed:', err);
            ServerSyncShared.showAlert('Failed to queue items');
        });
    },

    bulkDelete: function() {
        var self = this;
        var ids = this.table.getSelectedIds();
        if (ids.length === 0) return;

        if (!confirm('Delete ' + ids.length + ' item(s) from the LOCAL server? This cannot be undone.\n\nNote: This only deletes from this local server, never from the source server.')) {
            return;
        }

        ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: ids }).then(function(result) {
            self.table.clearSelection();
            self.loadSyncStatus();
            self.loadSyncItems();
            if (result && result.Deleted > 0) {
                ServerSyncShared.showAlert('Deleted ' + result.Deleted + ' item(s)');
            }
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to delete items');
        });
    },

    bulkAction: function(endpoint) {
        var self = this;
        var ids = this.table.getSelectedIds();
        if (ids.length === 0) return;

        ServerSyncShared.apiRequest(endpoint, 'POST', { SourceItemIds: ids }).then(function() {
            self.table.clearSelection();
            self.loadSyncStatus();
            self.loadSyncItems();
            ServerSyncShared.showAlert(ids.length + ' item(s) updated');
        }).catch(function(err) {
            console.error('Bulk action failed:', err);
            ServerSyncShared.showAlert('Failed to update items');
        });
    },

    getDisplayStatus: function(item) {
        if (item.Status === 'Pending' && item.PendingType) {
            return 'Pending ' + item.PendingType;
        }
        return item.Status;
    },

    getStatusClass: function(item) {
        if (item.Status === 'Pending' && item.PendingType) {
            return 'Pending-' + item.PendingType;
        }
        return item.Status;
    },

    showItemDetail: function(sourceItemId) {
        var self = this;
        var items = this.table.getItems();
        var item = items.find(function(i) { return i.SourceItemId === sourceItemId; });
        if (!item) return;

        self.currentModalItem = item;

        // Title
        document.getElementById('modalTitle').textContent = ServerSyncShared.getFileName(item.SourcePath);

        // Status badge
        var statusBadge = document.getElementById('modalStatusBadge');
        var displayStatus = self.getDisplayStatus(item);
        var statusClass = self.getStatusClass(item);
        statusBadge.textContent = displayStatus;
        statusBadge.className = 'itemModal-statusBadge ' + statusClass;

        // Size
        document.getElementById('modalSize').textContent = ServerSyncShared.formatSize(item.SourceSize);

        // Paths
        document.getElementById('modalSourcePath').textContent = item.SourcePath;

        // Error message
        var errorSection = document.getElementById('modalErrorSection');
        if (item.Status === 'Errored' && item.ErrorMessage) {
            document.getElementById('modalError').textContent = item.ErrorMessage;
            errorSection.style.display = 'flex';
        } else {
            errorSection.style.display = 'none';
        }

        // Retry count
        var retrySection = document.getElementById('modalRetrySection');
        if (item.RetryCount > 0) {
            document.getElementById('modalRetryCount').textContent = item.RetryCount + ' attempt' + (item.RetryCount > 1 ? 's' : '');
            retrySection.style.display = 'block';
        } else {
            retrySection.style.display = 'none';
        }

        // Last sync time
        var lastSyncSection = document.getElementById('modalLastSyncSection');
        if (item.LastSyncTime) {
            var lastSync = new Date(item.LastSyncTime);
            document.getElementById('modalLastSync').textContent = lastSync.toLocaleString();
            lastSyncSection.style.display = 'block';
        } else {
            lastSyncSection.style.display = 'none';
        }

        // Companion files
        var companionSection = document.getElementById('modalCompanionFilesSection');
        if (item.CompanionFiles) {
            var companionList = item.CompanionFiles.split(',').map(function(f) {
                return f.trim();
            }).filter(function(f) {
                return f.length > 0;
            });
            if (companionList.length > 0) {
                document.getElementById('modalCompanionFiles').innerHTML = companionList.map(function(f) {
                    return '<div class="itemModal-companionItem">' +
                        '<span class="itemModal-companionIcon">&#128196;</span>' +
                        '<span class="itemModal-companionName">' + ServerSyncShared.escapeHtml(f) + '</span>' +
                        '</div>';
                }).join('');
                companionSection.style.display = 'block';
            } else {
                companionSection.style.display = 'none';
            }
        } else {
            companionSection.style.display = 'none';
        }

        // Local path
        var localPathEl = document.getElementById('modalLocalPath');
        var localPathNoteEl = document.getElementById('modalLocalPathNote');
        var localExists = item.Status === 'Synced';
        if (item.LocalPath) {
            localPathEl.textContent = item.LocalPath;
            if (localExists) {
                localPathNoteEl.textContent = '';
                localPathNoteEl.style.display = 'none';
            } else {
                localPathNoteEl.textContent = 'File will be synced to this location';
                localPathNoteEl.style.display = 'block';
            }
        } else {
            localPathEl.textContent = 'N/A';
            localPathNoteEl.style.display = 'none';
        }

        // Show/hide modal buttons based on pending type
        var btnQueue = document.getElementById('btnModalQueue');
        var modalDeleteRow = document.getElementById('modalDeleteRow');
        var isPendingDeletion = item.Status === 'Pending' && item.PendingType === 'Deletion';
        var isPendingDownloadOrReplacement = item.Status === 'Pending' && (item.PendingType === 'Download' || item.PendingType === 'Replacement');

        if (isPendingDeletion) {
            btnQueue.style.display = 'none';
            if (self.capabilities && self.capabilities.CanDeleteItems) {
                modalDeleteRow.style.display = 'block';
            }
        } else if (isPendingDownloadOrReplacement) {
            btnQueue.style.display = 'inline-block';
            modalDeleteRow.style.display = 'none';
        } else {
            btnQueue.style.display = 'inline-block';
            if (self.capabilities && self.capabilities.CanDeleteItems) {
                modalDeleteRow.style.display = 'block';
            }
        }

        document.getElementById('itemDetailModal').style.display = 'flex';
    },

    closeModal: function() {
        document.getElementById('itemDetailModal').style.display = 'none';
        this.currentModalItem = null;
    },

    modalIgnore: function() {
        if (this.currentModalItem) {
            this.updateItemStatus(this.currentModalItem.SourceItemId, 'Ignored');
        }
    },

    modalQueue: function() {
        if (this.currentModalItem) {
            // Queuing a pending deletion item is a no-op
            if (this.currentModalItem.Status === 'Pending' && this.currentModalItem.PendingType === 'Deletion') {
                return;
            }
            this.updateItemStatus(this.currentModalItem.SourceItemId, 'Queued');
        }
    },

    modalDelete: function() {
        var self = this;
        if (!this.currentModalItem) return;

        var fileName = ServerSyncShared.getFileName(this.currentModalItem.LocalPath || this.currentModalItem.SourcePath);
        if (!confirm('Delete "' + fileName + '" from the LOCAL server? This cannot be undone.\n\nNote: This only deletes from this local server, never from the source server.')) {
            return;
        }

        ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function() {
            self.closeModal();
            self.loadSyncStatus();
            self.loadSyncItems();
            ServerSyncShared.showAlert('Item deleted');
        }).catch(function() {
            ServerSyncShared.showAlert('Failed to delete item');
        });
    },

    updateItemStatus: function(sourceItemId, status) {
        var self = this;

        ServerSyncShared.apiRequest('UpdateItemStatus', 'POST', { SourceItemId: sourceItemId, Status: status }).then(function() {
            self.closeModal();
            self.loadSyncStatus();
            self.loadSyncItems();
            ServerSyncShared.showAlert('Item status updated to ' + status);
        }).catch(function(err) {
            console.error('Failed to update item status:', err);
            ServerSyncShared.showAlert('Failed to update item status');
        });
    }
};
