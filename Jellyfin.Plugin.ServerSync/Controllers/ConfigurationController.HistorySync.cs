using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// History sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
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
        var (items, totalCount) = _databaseProvider.Database.SearchHistoryItemsPaginated(search, statusFilter, sourceUserId, skip, take);
        var config = _configManager.Configuration;

        return Ok(new PaginatedResult<HistorySyncItemDto>
        {
            Items = items.Select(i => MapToHistorySyncItemDto(i, config.SourceServerUrl, config.SourceServerApiKey)).ToList(),
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
        var counts = _databaseProvider.Database.GetHistoryStatusCounts();

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
        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        // Prefer database ID if provided
        if (request.Id.HasValue)
        {
            _databaseProvider.Database.UpdateHistoryItemStatusById(request.Id.Value, status);
        }
        else if (!string.IsNullOrEmpty(request.SourceUserId) && !string.IsNullOrEmpty(request.SourceItemId))
        {
            _databaseProvider.Database.UpdateHistoryItemStatus(request.SourceUserId, request.SourceItemId, status);
        }
        else
        {
            return BadRequest("Either Id or both SourceUserId and SourceItemId must be provided");
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
                successCount = _databaseProvider.Database.BatchUpdateHistoryItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to queue history items by IDs");
            }
        }
        // Fallback to legacy Items property
        else if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    _databaseProvider.Database.UpdateHistoryItemStatus(item.SourceUserId, item.SourceItemId, BaseSyncStatus.Queued);
                    successCount++;
                }
                catch (Exception ex)
                {
                    var sanitizedUserId = SanitizeForLog(item.SourceUserId);
                    var sanitizedItemId = SanitizeForLog(item.SourceItemId);
                    _logger.LogWarning(ex, "Failed to queue history item {SourceUserId}/{SourceItemId}",
                            sanitizedUserId, sanitizedItemId);
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
                successCount = _databaseProvider.Database.BatchUpdateHistoryItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ignore history items by IDs");
            }
        }
        // Fallback to legacy Items property
        else if (request?.Items != null)
        {
            foreach (var item in request.Items)
            {
                try
                {
                    _databaseProvider.Database.UpdateHistoryItemStatus(item.SourceUserId, item.SourceItemId, BaseSyncStatus.Ignored);
                    successCount++;
                }
                catch (Exception ex)
                {
                    var sanitizedUserId = SanitizeForLog(item.SourceUserId);
                    var sanitizedItemId = SanitizeForLog(item.SourceItemId);
                    _logger.LogWarning(ex, "Failed to ignore history item {SourceUserId}/{SourceItemId}",
                            sanitizedUserId, sanitizedItemId);
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
    private static HistorySyncItemDto MapToHistorySyncItemDto(HistorySyncItem item, string? sourceServerUrl, string? sourceServerApiKey)
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
            SourceServerUrl = sourceServerUrl,
            SourceServerApiKey = sourceServerApiKey,
            Status = item.Status.ToString(),
            StatusDate = item.StatusDate,
            LastSyncTime = item.LastSyncTime,
            ErrorMessage = item.ErrorMessage,
            HasChanges = item.HasChanges
        };
    }
}
