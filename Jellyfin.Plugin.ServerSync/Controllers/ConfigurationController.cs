using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
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

    /// <summary>
    /// SanitizeForLog
    /// Sanitizes user input to prevent log injection attacks.
    /// </summary>
    /// <param name="input">User input to sanitize.</param>
    /// <returns>Sanitized string safe for logging.</returns>
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

    /// <summary>
    /// TestConnection
    /// Tests connection to the source server using API key authentication.
    /// </summary>
    /// <param name="request">Connection test request.</param>
    /// <returns>Connection test response.</returns>
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

    /// <summary>
    /// ValidateUrl
    /// Validates a server URL format and accessibility.
    /// </summary>
    /// <param name="request">URL validation request.</param>
    /// <returns>URL validation response.</returns>
    [HttpPost("ValidateUrl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ValidateUrlResponse> ValidateUrl([FromBody] ValidateUrlRequest request)
    {
        return Ok(ValidateServerUrl(request.Url));
    }

    /// <summary>
    /// GetSourceLibraries
    /// Gets libraries from the source server.
    /// </summary>
    /// <param name="request">Connection request with credentials.</param>
    /// <returns>List of library DTOs.</returns>
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

    /// <summary>
    /// GetSourceUsers
    /// Gets users from the source server.
    /// </summary>
    /// <param name="request">Connection request with credentials.</param>
    /// <returns>List of user info DTOs.</returns>
    [HttpPost("GetSourceUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<UserInfoDto>>> GetSourceUsers([FromBody] TestConnectionRequest request)
    {
        var plugin = Plugin.Instance!;

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            request.ServerUrl,
            request.ApiKey);

        var users = await client.GetUsersAsync().ConfigureAwait(false);

        return Ok(users.Select(u => new UserInfoDto
        {
            Id = u.Id?.ToString() ?? string.Empty,
            Name = u.Name ?? string.Empty
        }).ToList());
    }

    /// <summary>
    /// GetSyncItems
    /// Gets paginated sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="search">Optional search term.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="pendingType">Optional pending type filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of sync item DTOs.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<SyncItemDto>> GetSyncItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? pendingType = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;

        // Clamp pagination values to reasonable limits
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

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

        // Get paginated results
        var (items, totalCount) = plugin.Database.SearchPaginated(search, statusFilter, pendingTypeFilter, skip, take);

        // Build lookup for library names from mappings
        var libraryMappings = config.LibraryMappings ?? new List<Models.Configuration.LibraryMapping>();
        var libraryNameLookup = libraryMappings.ToDictionary(
            m => m.SourceLibraryId,
            m => (SourceName: m.SourceLibraryName, LocalName: m.LocalLibraryName));

        return Ok(new PaginatedResult<SyncItemDto>
        {
            Items = items.Select(i =>
            {
                libraryNameLookup.TryGetValue(i.SourceLibraryId, out var libraryNames);
                return new SyncItemDto
                {
                    Id = i.Id,
                    SourceItemId = i.SourceItemId,
                    SourceLibraryId = i.SourceLibraryId,
                    SourceLibraryName = libraryNames.SourceName,
                    LocalLibraryId = i.LocalLibraryId,
                    LocalLibraryName = libraryNames.LocalName,
                    SourcePath = i.SourcePath,
                    LocalPath = i.LocalPath,
                    SourceSize = i.SourceSize,
                    SourceSizeFormatted = FormatUtilities.FormatBytes(i.SourceSize),
                    SourceCreateDate = i.SourceCreateDate,
                    LocalItemId = i.LocalItemId,
                    Status = i.Status.ToString(),
                    PendingType = i.PendingType?.ToString(),
                    StatusDate = i.StatusDate,
                    LastSyncTime = i.LastSyncTime,
                    ErrorMessage = i.ErrorMessage,
                    RetryCount = i.RetryCount,
                    SourceServerUrl = config.SourceServerUrl,
                    SourceServerId = config.SourceServerId,
                    CompanionFiles = i.CompanionFiles
                };
            }).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// GetSyncStatus
    /// Gets sync status counts.
    /// </summary>
    /// <returns>Sync status response with counts.</returns>
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

    /// <summary>
    /// GetSyncStats
    /// Gets detailed sync statistics for health dashboard.
    /// </summary>
    /// <returns>Sync stats response.</returns>
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

    /// <summary>
    /// GetDiskSpace
    /// Gets disk space information for configured library paths.
    /// </summary>
    /// <returns>List of disk space info.</returns>
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

    /// <summary>
    /// TriggerSync
    /// Manually triggers the sync task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
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

    /// <summary>
    /// TriggerRefresh
    /// Manually triggers the refresh sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
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

    /// <summary>
    /// RetryErroredItems
    /// Resets errored items for retry.
    /// </summary>
    /// <param name="request">Optional request with specific item IDs.</param>
    /// <returns>Action result with success status.</returns>
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

    /// <summary>
    /// UpdateItemStatus
    /// Updates the status of a sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
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

    /// <summary>
    /// IgnoreItems
    /// Marks multiple items as ignored.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
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

    /// <summary>
    /// QueueItems
    /// Moves items to Queued status (works for Pending, Ignored, Errored, and Synced items).
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with updated count.</returns>
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

    /// <summary>
    /// DeleteLocalItems
    /// Deletes items from the LOCAL server only using the Jellyfin API.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with deletion counts.</returns>
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
                    SanitizeForLog(fileName),
                    SanitizeForLog(item.LocalPath));
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
                    SanitizeForLog(fileName),
                    SanitizeForLog(item.SourcePath));
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
                logger.LogInformation("{Action}{Suffix}: {FileName} - Local path: {LocalPath}", action, suffix, SanitizeForLog(fileName), SanitizeForLog(item.LocalPath));
                plugin.Database.Delete(sourceItemId);
                deletedCount++;
            }
            else
            {
                // Check if file still exists after failed deletion attempt
                // It may have been deleted externally (manually or by another process)
                var stillExists = !string.IsNullOrEmpty(item.LocalPath) && System.IO.File.Exists(item.LocalPath);
                if (!stillExists)
                {
                    // File is gone (deleted elsewhere) - remove from tracking
                    logger.LogInformation(
                        "DELETE (external): {FileName} - File no longer exists after deletion attempt, removing from tracking. Local path: {LocalPath}",
                        SanitizeForLog(fileName),
                        SanitizeForLog(item.LocalPath));
                    plugin.Database.Delete(sourceItemId);
                    deletedCount++;
                }
                else
                {
                    logger.LogError("FAILED DELETE: {FileName} - {Error}. Local path: {LocalPath}", SanitizeForLog(fileName), SanitizeForLog(result.ErrorMessage), SanitizeForLog(item.LocalPath));
                    failedCount++;
                }
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

    /// <summary>
    /// RemoveFromTracking
    /// Removes items from the sync tracking database without deleting the actual files.
    /// </summary>
    /// <param name="request">Bulk items request.</param>
    /// <returns>Action result with removed count.</returns>
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

    /// <summary>
    /// GetCapabilities
    /// Returns the plugin capabilities including whether deletion is supported.
    /// </summary>
    /// <returns>Capabilities response.</returns>
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

    /// <summary>
    /// GetStatusMetadata
    /// Returns metadata for all status types including display names and colors.
    /// Used by the frontend to render status badges consistently.
    /// </summary>
    /// <returns>Dictionary of status metadata.</returns>
    [HttpGet("StatusMetadata")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, StatusMetadata>> GetStatusMetadata()
    {
        return Ok(StatusAppearanceHelper.GetStatusMetadata());
    }

    /// <summary>
    /// ResolveLocalItemIds
    /// Attempts to find and store the local Jellyfin item IDs for synced items.
    /// </summary>
    /// <returns>Action result with resolved count.</returns>
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
                        logger.LogDebug("Resolved LocalItemId for {FileName}", SanitizeForLog(Path.GetFileName(item.LocalPath)));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to resolve LocalItemId for {FileName}", SanitizeForLog(Path.GetFileName(item.LocalPath)));
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

    /// <summary>
    /// ResetSyncDatabase
    /// Deletes all items from the sync database and recreates it with the latest schema.
    /// </summary>
    /// <returns>Action result with success status.</returns>
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

    /// <summary>
    /// ResetHistorySyncDatabase
    /// Resets the history sync database, removing all tracked history items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetHistorySyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetHistorySyncDatabase()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();

        try
        {
            plugin.Database.ResetHistoryDatabase();
            logger.LogInformation("History sync database has been reset");
            return Ok(new { Success = true, Message = "History database reset successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset history sync database");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// ValidateServerUrl
    /// Validates and normalizes a server URL.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <returns>Validation response with normalized URL.</returns>
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

    /// <summary>
    /// ValidateConfiguration
    /// Validates the current plugin configuration.
    /// </summary>
    /// <returns>Validation response with errors.</returns>
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

    /// <summary>
    /// SanitizeConfiguration
    /// Sanitizes configuration values to valid ranges.
    /// </summary>
    /// <returns>Action result with status message.</returns>
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

    // ===== History Sync Endpoints =====

    /// <summary>
    /// GetHistoryItems
    /// Gets paginated history sync items from the database with optional search and filter.
    /// </summary>
    /// <param name="search">Optional search term.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sourceUserId">Optional source user ID filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of history sync item DTOs.</returns>
    [HttpGet("HistoryItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<HistorySyncItemDto>> GetHistoryItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceUserId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        // Clamp pagination values
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        // Parse status filter
        BaseSyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<BaseSyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        // Get paginated results
        var (items, totalCount) = plugin.Database.SearchHistoryItemsPaginated(search, statusFilter, sourceUserId, skip, take);

        return Ok(new PaginatedResult<HistorySyncItemDto>
        {
            Items = items.Select(i => MapToHistorySyncItemDto(i)).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// GetHistoryStatus
    /// Gets history sync status counts.
    /// </summary>
    /// <returns>History sync status response with counts.</returns>
    [HttpGet("HistoryStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseSyncStatusResponse> GetHistoryStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var counts = plugin.Database.GetHistoryStatusCounts();

        return Ok(new BaseSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(BaseSyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(BaseSyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(BaseSyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(BaseSyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(BaseSyncStatus.Ignored, 0)
        });
    }

    /// <summary>
    /// UpdateHistoryItemStatus
    /// Updates the status of a history sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("HistoryItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateHistoryItemStatus([FromBody] UpdateHistoryItemStatusRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        // Prefer database ID if provided
        if (request.Id.HasValue)
        {
            plugin.Database.UpdateHistoryItemStatusById(request.Id.Value, status);
        }
        else
        {
            plugin.Database.UpdateHistoryItemStatus(request.SourceUserId, request.SourceItemId, status);
        }

        return Ok(new { Success = true });
    }

    /// <summary>
    /// QueueHistoryItems
    /// Moves history items to Queued status.
    /// </summary>
    /// <param name="request">Bulk history items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("HistoryItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueHistoryItems([FromBody] BulkHistoryItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        // Support both Ids (preferred) and Items (legacy)
        if ((request?.Ids == null || request.Ids.Count == 0) &&
            (request?.Items == null || request.Items.Count == 0))
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        // Process by database ID if provided
        if (request?.Ids != null && request.Ids.Count > 0)
        {
            try
            {
                successCount = plugin.Database.BatchUpdateHistoryItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
            }
            catch (Exception ex)
            {
                plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                    .LogWarning(ex, "Failed to queue history items by IDs");
            }
        }
        // Fallback to legacy Items property
        else if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    plugin.Database.UpdateHistoryItemStatus(item.SourceUserId, item.SourceItemId, BaseSyncStatus.Queued);
                    successCount++;
                }
                catch (Exception ex)
                {
                    plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                        .LogWarning(ex, "Failed to queue history item {SourceUserId}/{SourceItemId}",
                            SanitizeForLog(item.SourceUserId), SanitizeForLog(item.SourceItemId));
                }
            }
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// IgnoreHistoryItems
    /// Marks history items as ignored.
    /// </summary>
    /// <param name="request">Bulk history items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("HistoryItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreHistoryItems([FromBody] BulkHistoryItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        // Support both Ids (preferred) and Items (legacy)
        if ((request?.Ids == null || request.Ids.Count == 0) &&
            (request?.Items == null || request.Items.Count == 0))
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        // Process by database ID if provided
        if (request?.Ids != null && request.Ids.Count > 0)
        {
            try
            {
                successCount = plugin.Database.BatchUpdateHistoryItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
            }
            catch (Exception ex)
            {
                plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                    .LogWarning(ex, "Failed to ignore history items by IDs");
            }
        }
        // Fallback to legacy Items property
        else if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    plugin.Database.UpdateHistoryItemStatus(item.SourceUserId, item.SourceItemId, BaseSyncStatus.Ignored);
                    successCount++;
                }
                catch (Exception ex)
                {
                    plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                        .LogWarning(ex, "Failed to ignore history item {SourceUserId}/{SourceItemId}",
                            SanitizeForLog(item.SourceUserId), SanitizeForLog(item.SourceItemId));
                }
            }
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// TriggerHistoryRefresh
    /// Manually triggers the refresh history sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerHistoryRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerHistoryRefresh()
    {
        var refreshTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncRefreshHistoryTable");

        if (refreshTask == null)
        {
            return NotFound("History refresh task not found");
        }

        _taskManager.Execute(refreshTask, new TaskOptions());

        return Ok(new { Message = "History refresh task started" });
    }

    /// <summary>
    /// TriggerHistorySync
    /// Manually triggers the sync missing history task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerHistorySync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerHistorySync()
    {
        var syncTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncMissingHistory");

        if (syncTask == null)
        {
            return NotFound("History sync task not found");
        }

        _taskManager.Execute(syncTask, new TaskOptions());

        return Ok(new { Message = "History sync task started" });
    }

    /// <summary>
    /// Maps a HistorySyncItem to a DTO.
    /// </summary>
    private static HistorySyncItemDto MapToHistorySyncItemDto(HistorySyncItem item)
    {
        return new HistorySyncItemDto
        {
            Id = item.Id,
            SourceUserId = item.SourceUserId,
            LocalUserId = item.LocalUserId,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceItemId = item.SourceItemId,
            LocalItemId = item.LocalItemId,
            ItemName = item.ItemName,
            SourcePath = item.SourcePath,
            LocalPath = item.LocalPath,
            SourceIsPlayed = item.SourceIsPlayed,
            SourcePlayCount = item.SourcePlayCount,
            SourcePlaybackPositionTicks = item.SourcePlaybackPositionTicks,
            SourceLastPlayedDate = item.SourceLastPlayedDate,
            SourceIsFavorite = item.SourceIsFavorite,
            LocalIsPlayed = item.LocalIsPlayed,
            LocalPlayCount = item.LocalPlayCount,
            LocalPlaybackPositionTicks = item.LocalPlaybackPositionTicks,
            LocalLastPlayedDate = item.LocalLastPlayedDate,
            LocalIsFavorite = item.LocalIsFavorite,
            MergedIsPlayed = item.MergedIsPlayed,
            MergedPlayCount = item.MergedPlayCount,
            MergedPlaybackPositionTicks = item.MergedPlaybackPositionTicks,
            MergedLastPlayedDate = item.MergedLastPlayedDate,
            MergedIsFavorite = item.MergedIsFavorite,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.ErrorMessage,
            HasChanges = item.HasChanges
        };
    }

    // ===== User Sync Endpoints =====

    /// <summary>
    /// GetUserSyncItemById
    /// Gets a single user sync item by its ID.
    /// </summary>
    /// <param name="id">The item ID.</param>
    /// <returns>The user sync item DTO or NotFound.</returns>
    [HttpGet("UserItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserSyncItemDto> GetUserSyncItemById(long id)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var item = plugin.Database.GetUserSyncItemById(id);
        if (item == null)
        {
            return NotFound("User sync item not found");
        }

        return Ok(MapToUserSyncItemDto(item));
    }

    /// <summary>
    /// GetUserSyncItems
    /// Gets paginated user sync items from the database with optional search and filter.
    /// One record per property category (Policy, Configuration, ProfileImage) per user.
    /// </summary>
    /// <param name="search">Optional search term (matches user names).</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sourceUserId">Optional source user ID filter.</param>
    /// <param name="propertyCategory">Optional property category filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of user sync item DTOs.</returns>
    [HttpGet("UserItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<UserSyncItemDto>> GetUserSyncItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceUserId = null,
        [FromQuery] string? propertyCategory = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        // Clamp pagination values
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        // Parse status filter
        BaseSyncStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<BaseSyncStatus>(status, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        // Get paginated results
        var (items, totalCount) = plugin.Database.SearchUserSyncItemsPaginated(
            search, statusFilter, sourceUserId, propertyCategory, skip, take);

        return Ok(new PaginatedResult<UserSyncItemDto>
        {
            Items = items.Select(i => MapToUserSyncItemDto(i)).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// GetUserSyncStatus
    /// Gets user sync status counts.
    /// </summary>
    /// <returns>User sync status response with counts.</returns>
    [HttpGet("UserStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BaseSyncStatusResponse> GetUserSyncStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var counts = plugin.Database.GetUserSyncStatusCounts();

        return Ok(new BaseSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(BaseSyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(BaseSyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(BaseSyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(BaseSyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(BaseSyncStatus.Ignored, 0)
        });
    }

    /// <summary>
    /// UpdateUserSyncItemStatus
    /// Updates the status of a user sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("UserItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateUserSyncItemStatus([FromBody] UpdateUserSyncItemStatusRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        plugin.Database.UpdateUserSyncItemStatusById(request.Id, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// QueueUserSyncItems
    /// Moves user sync items to Queued status.
    /// </summary>
    /// <param name="request">Bulk user sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("UserItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncItems([FromBody] BulkUserSyncItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = plugin.Database.BatchUpdateUserSyncItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
        }
        catch (Exception ex)
        {
            plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                .LogWarning(ex, "Failed to queue user sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// IgnoreUserSyncItems
    /// Marks user sync items as ignored.
    /// </summary>
    /// <param name="request">Bulk user sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("UserItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreUserSyncItems([FromBody] BulkUserSyncItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = plugin.Database.BatchUpdateUserSyncItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
        }
        catch (Exception ex)
        {
            plugin.LoggerFactory.CreateLogger<ConfigurationController>()
                .LogWarning(ex, "Failed to ignore user sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// TriggerUserRefresh
    /// Manually triggers the refresh user sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerUserRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerUserRefresh()
    {
        var refreshTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncRefreshUserTable");

        if (refreshTask == null)
        {
            return NotFound("User refresh task not found");
        }

        _taskManager.Execute(refreshTask, new TaskOptions());

        return Ok(new { Message = "User refresh task started" });
    }

    /// <summary>
    /// TriggerUserSync
    /// Manually triggers the sync missing user data task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerUserSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerUserSync()
    {
        var syncTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncMissingUserData");

        if (syncTask == null)
        {
            return NotFound("User sync task not found");
        }

        _taskManager.Execute(syncTask, new TaskOptions());

        return Ok(new { Message = "User sync task started" });
    }

    /// <summary>
    /// ResetUserSyncDatabase
    /// Resets the user sync database, removing all tracked user sync items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetUserSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetUserSyncDatabase()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();

        try
        {
            plugin.Database.ResetUserSyncDatabase();
            logger.LogInformation("User sync database has been reset");
            return Ok(new { Success = true, Message = "User sync database reset successfully" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset user sync database");
            return StatusCode(500, new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// Maps a UserSyncItem to a DTO.
    /// </summary>
    private static UserSyncItemDto MapToUserSyncItemDto(UserSyncItem item)
    {
        return new UserSyncItemDto
        {
            Id = item.Id,
            SourceUserId = item.SourceUserId,
            LocalUserId = item.LocalUserId,
            SourceUserName = item.SourceUserName,
            LocalUserName = item.LocalUserName,
            PropertyCategory = item.PropertyCategory,
            SourceValue = item.SourceValue,
            LocalValue = item.LocalValue,
            MergedValue = item.MergedValue,
            SourceImageSize = item.SourceImageSize,
            LocalImageSize = item.LocalImageSize,
            SourceImageSizeFormatted = item.SourceImageSize.HasValue ? FormatUtilities.FormatBytes(item.SourceImageSize.Value) : null,
            LocalImageSizeFormatted = item.LocalImageSize.HasValue ? FormatUtilities.FormatBytes(item.LocalImageSize.Value) : null,
            HasChanges = item.HasChanges,
            ChangesSummary = item.ChangesSummary,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.ErrorMessage
        };
    }
}
