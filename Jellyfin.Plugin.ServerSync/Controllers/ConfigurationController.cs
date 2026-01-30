using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// API controller for Server Sync plugin operations.
/// NOTE: This controller ONLY operates on the LOCAL server. It NEVER modifies the source server.
/// All delete operations use the local Jellyfin API to delete items from this server only.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ServerSync")]
[Produces(MediaTypeNames.Application.Json)]
public class ConfigurationController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;

    public ConfigurationController(ILibraryManager libraryManager, ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
    }

    // SanitizeForLog
    // Sanitizes user input to prevent log injection attacks.
    // Removes control characters and truncates to prevent log flooding.
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "[empty]";
        }

        // Remove control characters (newlines, tabs, etc.) that could forge log entries
        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray());

        // Truncate to prevent log flooding
        const int maxLength = 100;
        if (sanitized.Length > maxLength)
        {
            sanitized = string.Concat(sanitized.AsSpan(0, maxLength), "...");
        }

        return sanitized;
    }

    // TestConnection
    // Tests connection to the source server using API key authentication.
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection([FromBody] TestConnectionRequest request)
    {
        var plugin = Plugin.Instance!;

        // Validate URL first
        var urlValidation = ValidateServerUrl(request.ServerUrl);
        if (!urlValidation.IsValid)
        {
            return Ok(new TestConnectionResponse
            {
                Success = false,
                Message = urlValidation.Message
            });
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return Ok(new TestConnectionResponse
            {
                Success = false,
                Message = "API key is required"
            });
        }

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            urlValidation.NormalizedUrl!,
            request.ApiKey);

        var result = await client.TestConnectionAsync().ConfigureAwait(false);

        return Ok(new TestConnectionResponse
        {
            Success = result.Success,
            ServerName = result.ServerName,
            ServerId = result.ServerId,
            Message = result.Success ? "Connection successful" : result.ErrorMessage ?? "Failed to connect to server"
        });
    }

    // ValidateUrl
    // Validates a server URL format and accessibility.
    [HttpPost("ValidateUrl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ValidateUrlResponse> ValidateUrl([FromBody] ValidateUrlRequest request)
    {
        return Ok(ValidateServerUrl(request.Url));
    }

    // GetSourceLibraries
    // Gets libraries from the source server.
    [HttpPost("GetSourceLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<LibraryDto>>> GetSourceLibraries([FromBody] TestConnectionRequest request)
    {
        var plugin = Plugin.Instance!;

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            request.ServerUrl,
            request.ApiKey);

        var libraries = await client.GetLibrariesAsync().ConfigureAwait(false);

        return Ok(libraries.Select(l => new LibraryDto
        {
            Id = l.ItemId ?? string.Empty,
            Name = l.Name ?? string.Empty,
            Locations = l.Locations?.ToList() ?? new List<string>()
        }).ToList());
    }

    // GetSyncItems
    // Gets sync items from the database with optional search and filter.
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<SyncItemDto>> GetSyncItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pendingType = null)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;

        // Parse status filter
        SyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<SyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        // Parse pending type filter
        PendingType? pendingTypeFilter = null;
        if (!string.IsNullOrEmpty(pendingType) && Enum.TryParse<PendingType>(pendingType, out var parsedPendingType))
        {
            pendingTypeFilter = parsedPendingType;
        }

        // Use Search method if any filters are provided, otherwise GetAll
        var items = !string.IsNullOrEmpty(search) || statusFilter.HasValue || pendingTypeFilter.HasValue
            ? plugin.Database.Search(search, statusFilter, pendingTypeFilter)
            : plugin.Database.GetAll();

        return Ok(items.Select(i => new SyncItemDto
        {
            Id = i.Id,
            SourceItemId = i.SourceItemId,
            SourceLibraryId = i.SourceLibraryId,
            LocalLibraryId = i.LocalLibraryId,
            SourcePath = i.SourcePath,
            LocalPath = i.LocalPath,
            SourceSize = i.SourceSize,
            SourceCreateDate = i.SourceCreateDate,
            LocalItemId = i.LocalItemId,
            Status = i.Status.ToString(),
            PendingType = i.PendingType?.ToString(),
            StatusDate = i.StatusDate,
            LastSyncTime = i.LastSyncTime,
            ErrorMessage = i.ErrorMessage,
            RetryCount = i.RetryCount,
            SourceServerUrl = config.SourceServerUrl,
            SourceServerId = config.SourceServerId
        }).ToList());
    }

    // GetSyncStatus
    // Gets sync status counts.
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncStatusResponse> GetSyncStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var counts = plugin.Database.GetStatusCounts();
        var pendingCounts = plugin.Database.GetPendingCounts();

        return Ok(new SyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            PendingDownload = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
            PendingReplacement = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
            PendingDeletion = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            Deleting = counts.GetValueOrDefault(SyncStatus.Deleting, 0)
        });
    }

    // GetSyncStats
    // Gets detailed sync statistics for health dashboard.
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SyncStatsResponse> GetSyncStats()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;
        var stats = plugin.Database.GetSyncStats();
        var pendingCounts = plugin.Database.GetPendingCounts();
        var diskInfo = DiskSpaceService.GetMinimumDiskSpaceInfo(config);

        return Ok(new SyncStatsResponse
        {
            TotalItems = stats.StatusCounts.Values.Sum(),
            SyncedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Synced, 0),
            QueuedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Queued, 0),
            ErroredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Errored, 0),
            PendingItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Pending, 0),
            PendingDownloadItems = pendingCounts.GetValueOrDefault(PendingType.Download, 0),
            PendingReplacementItems = pendingCounts.GetValueOrDefault(PendingType.Replacement, 0),
            PendingDeletionItems = pendingCounts.GetValueOrDefault(PendingType.Deletion, 0),
            IgnoredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Ignored, 0),
            TotalSyncedBytes = stats.TotalSyncedBytes,
            TotalQueuedBytes = stats.TotalQueuedBytes,
            LastSyncTime = stats.LastSyncTime,
            LastSyncStartTime = config.LastSyncStartTime,
            LastSyncEndTime = config.LastSyncEndTime,
            FreeDiskSpaceBytes = diskInfo?.FreeBytes ?? 0,
            MinimumRequiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024,
            HasSufficientDiskSpace = diskInfo?.IsSufficient ?? true
        });
    }

    // GetDiskSpace
    // Gets disk space information for configured library paths.
    [HttpGet("DiskSpace")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<DiskSpaceInfo>> GetDiskSpace()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        return Ok(DiskSpaceService.GetDiskSpaceInfo(plugin.Configuration));
    }

    // TriggerSync
    // Manually triggers the sync task.
    [HttpPost("TriggerSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerSync()
    {
        var downloadTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncDownloadContent");

        if (downloadTask == null)
        {
            return NotFound("Download task not found");
        }

        _taskManager.Execute(downloadTask, new TaskOptions());

        return Ok(new { Message = "Sync task started" });
    }

    // TriggerRefresh
    // Manually triggers the refresh sync table task.
    [HttpPost("TriggerRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerRefresh()
    {
        var refreshTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncUpdateTables");

        if (refreshTask == null)
        {
            return NotFound("Refresh task not found");
        }

        _taskManager.Execute(refreshTask, new TaskOptions());

        return Ok(new { Message = "Refresh task started" });
    }

    // RetryErroredItems
    // Resets errored items for retry.
    [HttpPost("RetryErroredItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RetryErroredItems([FromBody] BulkItemsRequest? request = null)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.SourceItemIds?.Count > 0)
        {
            foreach (var itemId in request.SourceItemIds)
            {
                plugin.Database.UpdateStatus(itemId, SyncStatus.Queued);
            }
        }
        else
        {
            var erroredItems = plugin.Database.GetByStatus(SyncStatus.Errored);
            foreach (var item in erroredItems)
            {
                plugin.Database.UpdateStatus(item.SourceItemId, SyncStatus.Queued);
            }
        }

        return Ok(new { Success = true });
    }

    // UpdateItemStatus
    // Updates the status of a sync item.
    [HttpPost("UpdateItemStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateItemStatus([FromBody] UpdateItemStatusRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<SyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        plugin.Database.UpdateStatus(request.SourceItemId, status);
        return Ok(new { Success = true });
    }

    // IgnoreItems
    // Marks multiple items as ignored.
    [HttpPost("IgnoreItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                plugin.Database.UpdateStatus(itemId, SyncStatus.Ignored);
                successCount++;
            }
            catch (Exception ex)
            {
                plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                    .LogWarning(ex, "Failed to update status for item {ItemId}", SanitizeForLog(itemId));
            }
        }

        return Ok(new { Updated = successCount });
    }

    // QueueItems
    // Moves items to Queued status (works for Pending, Ignored, Errored, and Synced items).
    [HttpPost("QueueItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var itemId in request.SourceItemIds)
        {
            try
            {
                plugin.Database.UpdateStatus(itemId, SyncStatus.Queued);
                successCount++;
            }
            catch (Exception ex)
            {
                plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                    .LogWarning(ex, "Failed to update status for item {ItemId}", SanitizeForLog(itemId));
            }
        }

        return Ok(new { Updated = successCount });
    }

    // DeleteLocalItems
    // Deletes items from the LOCAL server only using the Jellyfin API.
    // This NEVER touches the source server. Companion files are also deleted.
    [HttpPost("DeleteLocalItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteLocalItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();
        var config = plugin.Configuration;
        var deletedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        logger.LogInformation("Starting deletion of {Count} items from local server", request.SourceItemIds.Count);

        foreach (var sourceItemId in request.SourceItemIds)
        {
            var item = plugin.Database.GetBySourceItemId(sourceItemId);
            if (item == null)
            {
                logger.LogWarning("SKIPPED DELETE: Item {SourceItemId} not found in tracking database", SanitizeForLog(sourceItemId));
                skippedCount++;
                continue;
            }

            var fileName = Path.GetFileName(item.LocalPath);

            // Validate path is within configured library directories (safety check)
            if (!string.IsNullOrEmpty(item.LocalPath) && !FileValidationService.IsPathWithinLibrary(item.LocalPath, config))
            {
                logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - Path is outside configured library directories. Local path: {LocalPath}",
                    fileName,
                    item.LocalPath);
                skippedCount++;
                continue;
            }

            // Try to find the local item - first by stored ID, then by path
            MediaBrowser.Controller.Entities.BaseItem? localItem = null;

            if (!string.IsNullOrEmpty(item.LocalItemId) && Guid.TryParse(item.LocalItemId, out var localItemGuid))
            {
                localItem = _libraryManager.GetItemById(localItemGuid);
            }

            // If not found by ID, try to find by path (handles case where LocalItemId was never set)
            if (localItem == null && !string.IsNullOrEmpty(item.LocalPath))
            {
                localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
            }

            // Check if file exists anywhere
            var fileExists = localItem != null || (!string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath));

            if (!fileExists)
            {
                // File doesn't exist anywhere - remove from tracking since there's nothing to delete
                logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - File not found in Jellyfin library or on disk, removing from tracking. Source path: {SourcePath}",
                    fileName,
                    item.SourcePath);
                plugin.Database.Delete(sourceItemId);
                skippedCount++;
                continue;
            }

            // Use FileDeletionService to handle the deletion
            var result = FileDeletionService.DeleteLocalFile(item.LocalPath, localItem, _libraryManager, config, logger);

            if (result.Success)
            {
                var useRecyclingBin = config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath);
                var action = useRecyclingBin ? "RECYCLED" : "DELETED";
                var suffix = localItem == null ? " (direct)" : string.Empty;
                logger.LogInformation("{Action}{Suffix}: {FileName} - Local path: {LocalPath}", action, suffix, fileName, item.LocalPath);
                plugin.Database.Delete(sourceItemId);
                deletedCount++;
            }
            else
            {
                logger.LogError("FAILED DELETE: {FileName} - {Error}. Local path: {LocalPath}", fileName, result.ErrorMessage, item.LocalPath);
                failedCount++;
            }
        }

        logger.LogInformation(
            "Deletion task complete: {Deleted} deleted, {Failed} failed, {Skipped} skipped out of {Total} items",
            deletedCount,
            failedCount,
            skippedCount,
            request.SourceItemIds.Count);

        return Ok(new { Deleted = deletedCount, Failed = failedCount, Skipped = skippedCount });
    }

    // RemoveFromTracking
    // Removes items from the sync tracking database without deleting the actual files.
    // Use this to stop tracking items without affecting the local files.
    [HttpPost("RemoveFromTracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RemoveFromTracking([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.SourceItemIds == null || request.SourceItemIds.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;
        foreach (var sourceItemId in request.SourceItemIds)
        {
            try
            {
                plugin.Database.Delete(sourceItemId);
                successCount++;
            }
            catch (Exception ex)
            {
                plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                    .LogWarning(ex, "Failed to remove tracking for item {ItemId}", SanitizeForLog(sourceItemId));
            }
        }

        return Ok(new { Removed = successCount });
    }

    // GetCapabilities
    // Returns the plugin capabilities including whether deletion is supported.
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        try
        {
            // Deletion is supported if we have access to the library manager
            // which is injected via DI - if we got here, we have it
            var canDelete = _libraryManager != null;

            return Ok(new CapabilitiesResponse
            {
                CanDeleteItems = canDelete,
                SupportsCompanionFiles = true,
                SupportsBandwidthScheduling = true
            });
        }
        catch (Exception)
        {
            // Return safe defaults if anything fails
            return Ok(new CapabilitiesResponse
            {
                CanDeleteItems = false,
                SupportsCompanionFiles = true,
                SupportsBandwidthScheduling = true
            });
        }
    }

    // ResolveLocalItemIds
    // Attempts to find and store the local Jellyfin item IDs for synced items.
    // This is needed for delete operations to work properly.
    [HttpPost("ResolveLocalItemIds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResolveLocalItemIds()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();

        try
        {
            var syncedItems = plugin.Database.GetByStatus(SyncStatus.Synced);
            var resolvedCount = 0;
            var alreadyResolvedCount = 0;

            foreach (var item in syncedItems)
            {
                // Skip if already has LocalItemId
                if (!string.IsNullOrEmpty(item.LocalItemId))
                {
                    alreadyResolvedCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(item.LocalPath))
                {
                    continue;
                }

                try
                {
                    // Try to find the item in Jellyfin by path
                    var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                    if (localItem != null)
                    {
                        plugin.Database.UpdateStatus(
                            item.SourceItemId,
                            item.Status,
                            localPath: item.LocalPath,
                            localItemId: localItem.Id.ToString());
                        resolvedCount++;
                        logger.LogDebug("Resolved LocalItemId for {FileName}", Path.GetFileName(item.LocalPath));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to resolve LocalItemId for {FileName}", Path.GetFileName(item.LocalPath));
                }
            }

            logger.LogInformation("Resolved {Count} local item IDs, {AlreadyResolved} already resolved", resolvedCount, alreadyResolvedCount);
            return Ok(new { Resolved = resolvedCount, AlreadyResolved = alreadyResolvedCount });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve local item IDs");
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    // ResetSyncDatabase
    // Deletes all items from the sync database and recreates it with the latest schema.
    [HttpPost("ResetSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetSyncDatabase()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();

        try
        {
            plugin.Database.ResetDatabase();
            logger.LogInformation("Sync database has been reset");
            return Ok(new { Success = true, Message = "Database reset successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset sync database");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }

    // ValidateServerUrl
    // Validates and normalizes a server URL.
    private static ValidateUrlResponse ValidateServerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = "URL cannot be empty"
            };
        }

        // Check for path traversal attempts
        if (url.Contains("..", StringComparison.Ordinal) || url.Contains("./", StringComparison.Ordinal))
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = "URL contains invalid path sequences"
            };
        }

        // Try to parse as URI
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = "Invalid URL format"
            };
        }

        // Only allow HTTP and HTTPS
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ValidateUrlResponse
            {
                IsValid = false,
                Message = "Only HTTP and HTTPS URLs are allowed"
            };
        }

        // Check for localhost variants that might be intentional
        var isLocalhost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                          uri.Host.Equals("::1", StringComparison.Ordinal);

        // Normalize the URL
        var normalizedUrl = $"{uri.Scheme}://{uri.Host}";
        if (!uri.IsDefaultPort)
        {
            normalizedUrl += $":{uri.Port}";
        }

        return new ValidateUrlResponse
        {
            IsValid = true,
            NormalizedUrl = normalizedUrl,
            Message = isLocalhost ? "Warning: Using localhost URL. Make sure this is intentional." : null
        };
    }

    // ValidateConfiguration
    // Validates the current plugin configuration.
    [HttpGet("ValidateConfiguration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ConfigurationValidationResponse> ValidateConfiguration()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var errors = plugin.Configuration.ValidateConfiguration();

        return Ok(new ConfigurationValidationResponse
        {
            IsValid = errors.Count == 0,
            Errors = errors
        });
    }

    // SanitizeConfiguration
    // Sanitizes configuration values to valid ranges.
    [HttpPost("SanitizeConfiguration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult SanitizeConfiguration()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        plugin.Configuration.SanitizeValues();
        plugin.SaveConfiguration();

        return Ok(new { Message = "Configuration sanitized" });
    }

}
