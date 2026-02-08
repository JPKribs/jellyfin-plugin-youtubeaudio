using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// User sync endpoints for Server Sync plugin.
/// </summary>
public partial class ConfigurationController
{
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
        var item = _databaseProvider.Database.GetUserSyncItemById(id);
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
        var (items, totalCount) = _databaseProvider.Database.SearchUserSyncItemsPaginated(
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
        var counts = _databaseProvider.Database.GetUserSyncStatusCounts();

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
        if (!Enum.TryParse<BaseSyncStatus>(request.Status, out var status))
        {
            return BadRequest("Invalid status value");
        }

        _databaseProvider.Database.UpdateUserSyncItemStatusById(request.Id, status);
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
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdateUserSyncItemStatusByIds(request.Ids, BaseSyncStatus.Queued);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue user sync items by IDs");
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
        if (request?.Ids == null || request.Ids.Count == 0)
        {
            return BadRequest("No items specified");
        }

        var successCount = 0;

        try
        {
            successCount = _databaseProvider.Database.BatchUpdateUserSyncItemStatusByIds(request.Ids, BaseSyncStatus.Ignored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ignore user sync items by IDs");
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
        try
        {
            _databaseProvider.Database.ResetUserSyncDatabase();
            _logger.LogInformation("User sync database has been reset");
            return Ok(new { Success = true, Message = "User sync database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset user sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
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

    // ============================================
    // User Sync Consolidated Endpoints
    // ============================================

    /// <summary>
    /// GetUserSyncUsers
    /// Gets paginated list of user sync users (consolidated view).
    /// Groups UserSyncItems by (SourceUserId, LocalUserId) showing one row per user.
    /// </summary>
    /// <param name="search">Optional search term (matches usernames).</param>
    /// <param name="status">Optional status filter (matches any category with this status).</param>
    /// <param name="skip">Number of items to skip (default 0).</param>
    /// <param name="take">Maximum items to return (default 50, max 200).</param>
    /// <returns>Paginated result of user sync user DTOs.</returns>
    [HttpGet("UserSyncUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PaginatedResult<UserSyncUserDto>> GetUserSyncUsers(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
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
        var (users, totalCount) = _databaseProvider.Database.GetUserSyncUsersPaginated(search, statusFilter, skip, take);

        // Map to DTOs
        var config = _configManager.Configuration;
        var dtos = users.Select(u => MapToUserSyncUserDto(
            u.SourceUserId,
            u.LocalUserId,
            u.SourceUserName,
            u.LocalUserName,
            u.Items,
            config)).ToList();

        return Ok(new PaginatedResult<UserSyncUserDto>
        {
            Items = dtos,
            TotalCount = totalCount
        });
    }

    /// <summary>
    /// GetUserSyncUserDetail
    /// Gets detailed information for a specific user mapping.
    /// Returns all property categories for the modal view.
    /// </summary>
    /// <param name="sourceUserId">The source server user ID.</param>
    /// <param name="localUserId">The local server user ID.</param>
    /// <returns>User sync user detail DTO.</returns>
    [HttpGet("UserSyncUsers/{sourceUserId}/{localUserId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserSyncUserDetailDto> GetUserSyncUserDetail(string sourceUserId, string localUserId)
    {
        var items = _databaseProvider.Database.GetUserSyncUserDetail(sourceUserId, localUserId);
        if (items.Count == 0)
        {
            return NotFound();
        }

        var config = _configManager.Configuration;
        var dto = MapToUserSyncUserDetailDto(sourceUserId, localUserId, items, config);

        return Ok(dto);
    }

    /// <summary>
    /// IgnoreUserSyncUsers
    /// Ignores all categories for specified user mappings.
    /// </summary>
    /// <param name="request">Bulk user mappings request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("UserSyncUsers/Ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult IgnoreUserSyncUsers([FromBody] BulkUserMappingsRequest request)
    {
        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("No user mappings specified");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = _databaseProvider.Database.BatchUpdateUserSyncStatusByMappings(mappings, BaseSyncStatus.Ignored);

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// QueueUserSyncUsers
    /// Queues all categories for specified user mappings.
    /// </summary>
    /// <param name="request">Bulk user mappings request.</param>
    /// <returns>Action result with updated count.</returns>
    [HttpPost("UserSyncUsers/Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult QueueUserSyncUsers([FromBody] BulkUserMappingsRequest request)
    {
        if (request?.UserMappings == null || request.UserMappings.Count == 0)
        {
            return BadRequest("No user mappings specified");
        }

        var mappings = request.UserMappings.Select(m => (m.SourceUserId, m.LocalUserId));
        var successCount = _databaseProvider.Database.BatchUpdateUserSyncStatusByMappings(mappings, BaseSyncStatus.Queued);

        return Ok(new { Updated = successCount });
    }

    /// <summary>
    /// Maps user sync items to a consolidated UserSyncUserDto.
    /// </summary>
    private static UserSyncUserDto MapToUserSyncUserDto(
        string sourceUserId,
        string localUserId,
        string? sourceUserName,
        string? localUserName,
        List<UserSyncItem> items,
        PluginConfiguration config)
    {
        var policyItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Policy);
        var configItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Configuration);
        var imageItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.ProfileImage);

        var dto = new UserSyncUserDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = sourceUserName ?? items.FirstOrDefault()?.SourceUserName,
            LocalUserName = localUserName ?? items.FirstOrDefault()?.LocalUserName,

            // Record IDs
            PolicyId = policyItem?.Id,
            ConfigurationId = configItem?.Id,
            ProfileImageId = imageItem?.Id,

            // Individual statuses
            PolicyStatus = policyItem?.Status.ToString(),
            ConfigurationStatus = configItem?.Status.ToString(),
            ProfileImageStatus = imageItem?.Status.ToString(),

            // Individual change flags
            PolicyHasChanges = policyItem?.HasChanges ?? false,
            ConfigurationHasChanges = configItem?.HasChanges ?? false,
            ProfileImageHasChanges = imageItem?.HasChanges ?? false,

            // Individual change summaries
            PolicyChangesSummary = policyItem?.ChangesSummary,
            ConfigurationChangesSummary = configItem?.ChangesSummary,
            ProfileImageChangesSummary = imageItem?.ChangesSummary,

            // Aggregate has changes
            HasChanges = (policyItem?.HasChanges ?? false) ||
                        (configItem?.HasChanges ?? false) ||
                        (imageItem?.HasChanges ?? false),

            // Aggregate last sync time (most recent)
            LastSyncTime = new[] { policyItem?.LastSyncTime, configItem?.LastSyncTime, imageItem?.LastSyncTime }
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .DefaultIfEmpty()
                .Max(),

            // Aggregate error message
            ErrorMessage = string.Join("; ", new[] { policyItem?.ErrorMessage, configItem?.ErrorMessage, imageItem?.ErrorMessage }
                .Where(e => !string.IsNullOrEmpty(e)))
        };

        dto.OverallStatus = ComputeOverallStatus(policyItem?.Status, configItem?.Status, imageItem?.Status);

        // Compute total changes display
        dto.TotalChanges = ComputeTotalChangesDisplay(
            policyItem?.HasChanges ?? false, policyItem?.ChangesSummary,
            configItem?.HasChanges ?? false, configItem?.ChangesSummary,
            imageItem?.HasChanges ?? false);

        // Set empty error message to null
        if (string.IsNullOrEmpty(dto.ErrorMessage))
        {
            dto.ErrorMessage = null;
        }

        return dto;
    }

    /// <summary>
    /// Maps user sync items to a detailed UserSyncUserDetailDto for the modal.
    /// </summary>
    private static UserSyncUserDetailDto MapToUserSyncUserDetailDto(
        string sourceUserId,
        string localUserId,
        List<UserSyncItem> items,
        PluginConfiguration config)
    {
        var policyItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Policy);
        var configItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.Configuration);
        var imageItem = items.FirstOrDefault(i => i.PropertyCategory == UserPropertyCategory.ProfileImage);

        var dto = new UserSyncUserDetailDto
        {
            SourceUserId = sourceUserId,
            LocalUserId = localUserId,
            SourceUserName = items.FirstOrDefault()?.SourceUserName,
            LocalUserName = items.FirstOrDefault()?.LocalUserName,

            // Map full item details
            PolicyItem = policyItem != null ? MapToUserSyncItemDto(policyItem) : null,
            ConfigurationItem = configItem != null ? MapToUserSyncItemDto(configItem) : null,
            ProfileImageItem = imageItem != null ? MapToUserSyncItemDto(imageItem) : null,

            // Config flags
            PolicyEnabled = config.UserSyncPolicy,
            ConfigurationEnabled = config.UserSyncConfiguration,
            ProfileImageEnabled = config.UserSyncProfileImage,

            // Aggregate last sync time
            LastSyncTime = new[] { policyItem?.LastSyncTime, configItem?.LastSyncTime, imageItem?.LastSyncTime }
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .DefaultIfEmpty()
                .Max(),

            // Aggregate error message
            ErrorMessage = string.Join("; ", new[] { policyItem?.ErrorMessage, configItem?.ErrorMessage, imageItem?.ErrorMessage }
                .Where(e => !string.IsNullOrEmpty(e)))
        };

        dto.OverallStatus = ComputeOverallStatus(policyItem?.Status, configItem?.Status, imageItem?.Status);

        // Set empty error message to null
        if (string.IsNullOrEmpty(dto.ErrorMessage))
        {
            dto.ErrorMessage = null;
        }

        return dto;
    }

    /// <summary>
    /// Computes the total changes display string (e.g., "1 policy, 2 config").
    /// </summary>
    private static string ComputeTotalChangesDisplay(
        bool policyHasChanges, string? policySummary,
        bool configHasChanges, string? configSummary,
        bool imageHasChanges)
    {
        var parts = new List<string>();

        if (policyHasChanges)
        {
            var count = ExtractChangeCount(policySummary);
            parts.Add(count > 1 ? $"{count} policy" : "1 policy");
        }

        if (configHasChanges)
        {
            var count = ExtractChangeCount(configSummary);
            parts.Add(count > 1 ? $"{count} config" : "1 config");
        }

        if (imageHasChanges)
        {
            parts.Add("1 image");
        }

        return parts.Count == 0 ? "No Changes" : string.Join(", ", parts);
    }

    /// <summary>
    /// Extracts the change count from a summary string like "X differences".
    /// </summary>
    private static int ExtractChangeCount(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return 1;
        }

        var match = Regex.Match(summary, @"(\d+)\s+difference");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
        {
            return count;
        }

        return 1;
    }
}
