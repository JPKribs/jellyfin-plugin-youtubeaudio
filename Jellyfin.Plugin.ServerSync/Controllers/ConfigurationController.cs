using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

// TestConnectionRequest
// Request to test connection to a source server.
public class TestConnectionRequest
{
    [Required]
    public string ServerUrl { get; set; } = string.Empty;

    [Required]
    public string ApiKey { get; set; } = string.Empty;
}

// TestConnectionResponse
// Response from a connection test.
public class TestConnectionResponse
{
    public bool Success { get; set; }

    public string? ServerName { get; set; }

    public string? ServerId { get; set; }

    public string? Message { get; set; }
}

// LibraryDto
// Library information for API responses.
public class LibraryDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Locations { get; set; } = new();
}

// SyncItemDto
// Sync item information for API responses.
public class SyncItemDto
{
    public long Id { get; set; }

    public string SourceItemId { get; set; } = string.Empty;

    public string SourceLibraryId { get; set; } = string.Empty;

    public string LocalLibraryId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? LocalPath { get; set; }

    public string? LocalItemId { get; set; }

    public long SourceSize { get; set; }

    public DateTime SourceCreateDate { get; set; }

    public DateTime SourceModifyDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime StatusDate { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public string? SourceServerUrl { get; set; }

    public string? SourceServerId { get; set; }
}

// SyncStatusResponse
// Status counts for API responses.
public class SyncStatusResponse
{
    public int Pending { get; set; }

    public int Queued { get; set; }

    public int Synced { get; set; }

    public int Errored { get; set; }

    public int Ignored { get; set; }

    public int PendingDeletion { get; set; }
}

// CapabilitiesResponse
// Plugin capabilities for the UI.
public class CapabilitiesResponse
{
    public bool CanDeleteItems { get; set; }

    public bool SupportsCompanionFiles { get; set; }

    public bool SupportsBandwidthScheduling { get; set; }
}

// SyncStatsResponse
// Detailed sync statistics for health dashboard.
public class SyncStatsResponse
{
    public int TotalItems { get; set; }

    public int SyncedItems { get; set; }

    public int QueuedItems { get; set; }

    public int ErroredItems { get; set; }

    public int PendingItems { get; set; }

    public int IgnoredItems { get; set; }

    public int PendingDeletionItems { get; set; }

    public long TotalSyncedBytes { get; set; }

    public long TotalQueuedBytes { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public DateTime? LastSyncStartTime { get; set; }

    public DateTime? LastSyncEndTime { get; set; }

    public long FreeDiskSpaceBytes { get; set; }

    public long MinimumRequiredBytes { get; set; }

    public bool HasSufficientDiskSpace { get; set; }
}

// DiskSpaceInfo
// Information about disk space availability.
public class DiskSpaceInfo
{
    public long FreeBytes { get; set; }

    public long TotalBytes { get; set; }

    public long RequiredBytes { get; set; }

    public bool IsSufficient { get; set; }

    public string Path { get; set; } = string.Empty;
}

// UpdateItemStatusRequest
// Request to update an item's status.
public class UpdateItemStatusRequest
{
    [Required]
    public string SourceItemId { get; set; } = string.Empty;

    [Required]
    public string Status { get; set; } = string.Empty;
}

// BulkItemsRequest
// Request for bulk item operations.
public class BulkItemsRequest
{
    [Required]
    public List<string> SourceItemIds { get; set; } = new();
}

// ValidateUrlRequest
// Request to validate a server URL.
public class ValidateUrlRequest
{
    [Required]
    public string Url { get; set; } = string.Empty;
}

// ValidateUrlResponse
// Response from URL validation.
public class ValidateUrlResponse
{
    public bool IsValid { get; set; }

    public string? Message { get; set; }

    public string? NormalizedUrl { get; set; }
}

// ConfigurationValidationResponse
// Response from configuration validation.
public class ConfigurationValidationResponse
{
    public bool IsValid { get; set; }

    public List<string> Errors { get; set; } = new();
}

// ConfigurationController
// API controller for Server Sync plugin operations.
// NOTE: This controller ONLY operates on the LOCAL server. It NEVER modifies the source server.
// All delete operations use the local Jellyfin API to delete items from this server only.
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
    // Gets all sync items from the database.
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<SyncItemDto>> GetSyncItems()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var config = plugin.Configuration;
        var items = plugin.Database.GetAll();

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
            SourceModifyDate = i.SourceModifyDate,
            LocalItemId = i.LocalItemId,
            Status = i.Status.ToString(),
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

        return Ok(new SyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(SyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(SyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(SyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(SyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(SyncStatus.Ignored, 0),
            PendingDeletion = counts.GetValueOrDefault(SyncStatus.PendingDeletion, 0)
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
        var diskInfo = GetDiskSpaceForLibraries();

        return Ok(new SyncStatsResponse
        {
            TotalItems = stats.StatusCounts.Values.Sum(),
            SyncedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Synced, 0),
            QueuedItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Queued, 0),
            ErroredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Errored, 0),
            PendingItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Pending, 0),
            IgnoredItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.Ignored, 0),
            PendingDeletionItems = stats.StatusCounts.GetValueOrDefault(SyncStatus.PendingDeletion, 0),
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

        var config = plugin.Configuration;
        var requiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;
        var results = new List<DiskSpaceInfo>();

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(mapping.LocalRootPath) ?? mapping.LocalRootPath);
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = driveInfo.AvailableFreeSpace,
                    TotalBytes = driveInfo.TotalSize,
                    RequiredBytes = requiredBytes,
                    IsSufficient = driveInfo.AvailableFreeSpace >= requiredBytes
                });
            }
            catch
            {
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = 0,
                    TotalBytes = 0,
                    RequiredBytes = requiredBytes,
                    IsSufficient = false
                });
            }
        }

        return Ok(results);
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

        return Ok();
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
        return Ok();
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
                    .LogWarning(ex, "Failed to update status for item {ItemId}", itemId);
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
                    .LogWarning(ex, "Failed to update status for item {ItemId}", itemId);
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
        var deletedCount = 0;
        var failedCount = 0;
        var skippedCount = 0;

        logger.LogInformation("Starting deletion of {Count} items from local server", request.SourceItemIds.Count);

        foreach (var sourceItemId in request.SourceItemIds)
        {
            var item = plugin.Database.GetBySourceItemId(sourceItemId);
            if (item == null)
            {
                logger.LogWarning("SKIPPED DELETE: Item {SourceItemId} not found in tracking database", sourceItemId);
                skippedCount++;
                continue;
            }

            var fileName = Path.GetFileName(item.LocalPath);

            // Only delete if there's a local item ID (meaning Jellyfin knows about it)
            if (!string.IsNullOrEmpty(item.LocalItemId) && Guid.TryParse(item.LocalItemId, out var localItemGuid))
            {
                try
                {
                    // Use Jellyfin's library manager to delete the item from the LOCAL server
                    // This properly handles companion files and database cleanup
                    var localItem = _libraryManager.GetItemById(localItemGuid);
                    if (localItem != null)
                    {
                        _libraryManager.DeleteItem(
                            localItem,
                            new DeleteOptions { DeleteFileLocation = true },
                            localItem.GetParent(),
                            notifyParentItem: true);

                        logger.LogInformation(
                            "DELETED: {FileName} - Local path: {LocalPath}",
                            fileName,
                            item.LocalPath);
                        deletedCount++;
                    }
                    else
                    {
                        // Item not found in Jellyfin, just remove from tracking
                        logger.LogWarning(
                            "SKIPPED DELETE: {FileName} - Item not found in Jellyfin library (LocalItemId: {LocalItemId}), removing from tracking only",
                            fileName,
                            item.LocalItemId);
                        skippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "FAILED DELETE: {FileName} - {Error}. Local path: {LocalPath}",
                        fileName,
                        ex.Message,
                        item.LocalPath);
                    failedCount++;
                    continue;
                }
            }
            else
            {
                logger.LogWarning(
                    "SKIPPED DELETE: {FileName} - No local item ID, removing from tracking only. Source path: {SourcePath}",
                    fileName,
                    item.SourcePath);
                skippedCount++;
            }

            // Remove from our tracking database
            plugin.Database.Delete(sourceItemId);
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
                    .LogWarning(ex, "Failed to remove tracking for item {ItemId}", sourceItemId);
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

    // GetDiskSpaceForLibraries
    // Gets the minimum disk space info across all configured library paths.
    private DiskSpaceInfo? GetDiskSpaceForLibraries()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return null;
        }

        var config = plugin.Configuration;
        var requiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;
        DiskSpaceInfo? minSpaceInfo = null;

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(mapping.LocalRootPath) ?? mapping.LocalRootPath);
                var info = new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = driveInfo.AvailableFreeSpace,
                    TotalBytes = driveInfo.TotalSize,
                    RequiredBytes = requiredBytes,
                    IsSufficient = driveInfo.AvailableFreeSpace >= requiredBytes
                };

                if (minSpaceInfo == null || info.FreeBytes < minSpaceInfo.FreeBytes)
                {
                    minSpaceInfo = info;
                }
            }
            catch
            {
                // Skip paths that can't be accessed
            }
        }

        return minSpaceInfo;
    }
}
