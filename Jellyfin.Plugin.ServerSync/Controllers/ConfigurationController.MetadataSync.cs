using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// Metadata sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
    // ============================================
    // Metadata Sync Endpoints
    // ============================================

    /// <summary>
    /// GetMetadataSyncItem
    /// Gets a specific metadata sync item by its ID.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <returns>The metadata sync item DTO.</returns>
    [HttpGet("MetadataItems/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MetadataSyncItemDto> GetMetadataSyncItem(long id)
    {
        var item = _databaseProvider.Database.GetMetadataSyncItemById(id);
        if (item == null)
        {
            return NotFound();
        }

        return Ok(MapToMetadataSyncItemDto(item, _configManager.Configuration.LibraryMappings));
    }

    /// <summary>
    /// GetMetadataItemImageInfo
    /// Gets image info (including sizes) for a metadata sync item from the source server.
    /// </summary>
    /// <param name="id">The database ID of the metadata sync item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of image info with sizes.</returns>
    [HttpGet("MetadataItems/{id}/ImageInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ImageInfoDto>>> GetMetadataItemImageInfo(long id, CancellationToken cancellationToken)
    {
        var item = _databaseProvider.Database.GetMetadataSyncItemById(id);
        if (item == null || string.IsNullOrEmpty(item.SourceItemId))
        {
            return NotFound();
        }

        var config = _configManager.Configuration;
        if (string.IsNullOrEmpty(config.SourceServerUrl) || string.IsNullOrEmpty(config.SourceServerApiKey))
        {
            return NotFound("Source server not configured");
        }

        try
        {
            using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

            if (!Guid.TryParse(item.SourceItemId, out var sourceItemGuid))
            {
                return NotFound("Invalid source item ID");
            }

            var imageInfoList = await client.GetItemImageInfoAsync(sourceItemGuid, cancellationToken).ConfigureAwait(false);
            if (imageInfoList == null)
            {
                return Ok(new List<ImageInfoDto>());
            }

            var result = imageInfoList.Select(img => new ImageInfoDto
            {
                ImageType = img.ImageType?.ToString() ?? "Unknown",
                ImageIndex = img.ImageIndex ?? 0,
                Size = img.Size ?? 0,
                Width = img.Width ?? 0,
                Height = img.Height ?? 0
            }).ToList();

            return Ok(result);
        }
        catch (Exception)
        {
            // Return empty list on error
            return Ok(new List<ImageInfoDto>());
        }
    }

    /// <summary>
    /// GetMetadataSyncItems
    /// Gets paginated metadata sync items from the database with optional search and filter.
    /// One record per item containing all categories (Metadata, Images, People).
    /// </summary>
    /// <param name="search">Optional search term (matches item names or paths).</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sourceLibraryId">Optional source library ID filter.</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of metadata sync item DTOs.</returns>
    [HttpGet("MetadataItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<MetadataSyncItemDto>> GetMetadataSyncItems(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? sourceLibraryId = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
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
        var (items, totalCount) = _databaseProvider.Database.SearchMetadataSyncItemsPaginated(
            search, statusFilter, sourceLibraryId, skip, take);

        var libraryMappings = _configManager.Configuration.LibraryMappings;

        return Ok(new PaginatedResult<MetadataSyncItemDto>
        {
            Items = items.Select(i => MapToMetadataSyncItemDto(i, libraryMappings)).ToList(),
            TotalCount = totalCount,
            Skip = skip,
            Take = take
        });
    }

    /// <summary>
    /// GetMetadataSyncStatus
    /// Gets metadata sync status counts.
    /// </summary>
    /// <returns>Metadata sync status response with counts.</returns>
    [HttpGet("MetadataStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MetadataSyncStatusResponse> GetMetadataSyncStatus()
    {
        var counts = _databaseProvider.Database.GetMetadataSyncStatusCounts();
        var libraryMappings = _configManager.Configuration.LibraryMappings ?? new List<LibraryMapping>();

        return Ok(new MetadataSyncStatusResponse
        {
            Pending = counts.GetValueOrDefault(BaseSyncStatus.Pending, 0),
            Queued = counts.GetValueOrDefault(BaseSyncStatus.Queued, 0),
            Synced = counts.GetValueOrDefault(BaseSyncStatus.Synced, 0),
            Errored = counts.GetValueOrDefault(BaseSyncStatus.Errored, 0),
            Ignored = counts.GetValueOrDefault(BaseSyncStatus.Ignored, 0),
            LastSyncTime = _configManager.Configuration.LastMetadataSyncTime,
            LibraryCount = libraryMappings.Count
        });
    }

    /// <summary>
    /// UpdateMetadataSyncItemStatus
    /// Updates the status of a metadata sync item.
    /// </summary>
    /// <param name="request">Status update request.</param>
    /// <returns>Action result with success status.</returns>
    [HttpPost("MetadataItems/UpdateStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateMetadataSyncItemStatus([FromBody] UpdateMetadataSyncItemStatusRequest request)
    {
        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        _databaseProvider.Database.UpdateMetadataSyncItemStatusById(request.Id, status);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// QueueMetadataSyncItems
    /// Moves metadata sync items to Queued status.
    /// </summary>
    /// <param name="request">Bulk metadata sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("MetadataItems/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueMetadataSyncItems([FromBody] BulkMetadataSyncItemsRequest request)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdateMetadataSyncItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue metadata sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// IgnoreMetadataSyncItems
    /// Marks metadata sync items as ignored.
    /// </summary>
    /// <param name="request">Bulk metadata sync items request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("MetadataItems/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreMetadataSyncItems([FromBody] BulkMetadataSyncItemsRequest request)
    {
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdateMetadataSyncItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ignore metadata sync items by IDs");
        }

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// TriggerMetadataRefresh
    /// Manually triggers the refresh metadata sync table task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataRefresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataRefresh()
    {
        var refreshTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncRefreshMetadataTable");

        if (refreshTask == null)
        {
            return NotFound("Metadata refresh task not found");
        }

        _taskManager.Execute(refreshTask, new TaskOptions());

        return Ok(new { Message = "Metadata refresh task started" });
    }

    /// <summary>
    /// TriggerMetadataSync
    /// Manually triggers the sync missing metadata task.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("TriggerMetadataSync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult TriggerMetadataSync()
    {
        var syncTask = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == "ServerSyncMissingMetadata");

        if (syncTask == null)
        {
            return NotFound("Metadata sync task not found");
        }

        _taskManager.Execute(syncTask, new TaskOptions());

        return Ok(new { Message = "Metadata sync task started" });
    }

    /// <summary>
    /// ResetMetadataSyncDatabase
    /// Resets the metadata sync database, removing all tracked metadata sync items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetMetadataSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetMetadataSyncDatabase()
    {
        try
        {
            _databaseProvider.Database.ResetMetadataSyncDatabase();
            _logger.LogInformation("Metadata sync database has been reset");
            return Ok(new { Success = true, Message = "Metadata sync database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset metadata sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Maps a MetadataSyncItem to a DTO.
    /// </summary>
    private static MetadataSyncItemDto MapToMetadataSyncItemDto(MetadataSyncItem item, List<LibraryMapping>? libraryMappings)
    {
        // Look up library names
        string? sourceLibraryName = null;
        string? localLibraryName = null;

        var mapping = libraryMappings?.FirstOrDefault(m => m.SourceLibraryId == item.SourceLibraryId);
        if (mapping != null)
        {
            sourceLibraryName = mapping.SourceLibraryName;
            localLibraryName = mapping.LocalLibraryName;
        }

        return new MetadataSyncItemDto
        {
            Id = item.Id,
            SourceLibraryId = item.SourceLibraryId,
            LocalLibraryId = item.LocalLibraryId,
            SourceLibraryName = sourceLibraryName,
            LocalLibraryName = localLibraryName,
            SourceItemId = item.SourceItemId,
            LocalItemId = item.LocalItemId,
            ItemName = item.ItemName,
            SourcePath = item.SourcePath,
            LocalPath = item.LocalPath,
            SourceMetadataValue = item.SourceMetadataValue,
            LocalMetadataValue = item.LocalMetadataValue,
            SourceImagesValue = item.SourceImagesValue,
            LocalImagesValue = item.LocalImagesValue,
            SourcePeopleValue = item.SourcePeopleValue,
            LocalPeopleValue = item.LocalPeopleValue,
            SourceStudiosValue = item.SourceStudiosValue,
            LocalStudiosValue = item.LocalStudiosValue,
            HasMetadataChanges = item.HasMetadataChanges,
            HasImagesChanges = item.HasImagesChanges,
            HasPeopleChanges = item.HasPeopleChanges,
            HasStudiosChanges = item.HasStudiosChanges,
            HasChanges = item.HasChanges,
            ChangesSummary = item.ChangesSummary,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.ErrorMessage
        };
    }
}
