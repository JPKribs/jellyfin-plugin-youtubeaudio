using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
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

    public ConfigurationController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    // TestConnection
    // Tests connection to the source server.
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection([FromBody] TestConnectionRequest request)
    {
        using var client = new SourceServerClient(
            Plugin.Instance!.LoggerFactory.CreateLogger<SourceServerClient>(),
            request.ServerUrl,
            request.ApiKey);

        var result = await client.TestConnectionAsync().ConfigureAwait(false);

        return Ok(new TestConnectionResponse
        {
            Success = result.ServerName != null,
            ServerName = result.ServerName,
            ServerId = result.ServerId,
            Message = result.ServerName != null ? "Connection successful" : "Failed to connect to server"
        });
    }

    // GetSourceLibraries
    // Gets libraries from the source server.
    [HttpPost("GetSourceLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<LibraryDto>>> GetSourceLibraries([FromBody] TestConnectionRequest request)
    {
        using var client = new SourceServerClient(
            Plugin.Instance!.LoggerFactory.CreateLogger<SourceServerClient>(),
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
    public ActionResult IgnoreItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        foreach (var itemId in request.SourceItemIds)
        {
            plugin.Database.UpdateStatus(itemId, SyncStatus.Ignored);
        }

        return Ok();
    }

    // QueueItems
    // Moves items to Queued status (works for Pending, Ignored, Errored, and Synced items).
    [HttpPost("QueueItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult QueueItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        foreach (var itemId in request.SourceItemIds)
        {
            plugin.Database.UpdateStatus(itemId, SyncStatus.Queued);
        }

        return Ok();
    }

    // DeleteLocalItems
    // Deletes items from the LOCAL server only using the Jellyfin API.
    // This NEVER touches the source server. Companion files are also deleted.
    [HttpPost("DeleteLocalItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteLocalItems([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        var logger = plugin.LoggerFactory.CreateLogger<ConfigurationController>();
        var deletedCount = 0;
        var failedCount = 0;

        foreach (var sourceItemId in request.SourceItemIds)
        {
            var item = plugin.Database.GetBySourceItemId(sourceItemId);
            if (item == null)
            {
                continue;
            }

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

                        logger.LogInformation("Deleted local item {FileName}", System.IO.Path.GetFileName(item.LocalPath));
                        deletedCount++;
                    }
                    else
                    {
                        // Item not found in Jellyfin, just remove from tracking
                        logger.LogWarning("Local item {LocalItemId} not found in Jellyfin, removing from tracking", item.LocalItemId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete local item {FileName}", System.IO.Path.GetFileName(item.LocalPath));
                    failedCount++;
                    continue;
                }
            }

            // Remove from our tracking database
            plugin.Database.Delete(sourceItemId);
        }

        return Ok(new { Deleted = deletedCount, Failed = failedCount });
    }

    // RemoveFromTracking
    // Removes items from the sync tracking database without deleting the actual files.
    // Use this to stop tracking items without affecting the local files.
    [HttpPost("RemoveFromTracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RemoveFromTracking([FromBody] BulkItemsRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return NotFound();
        }

        foreach (var sourceItemId in request.SourceItemIds)
        {
            plugin.Database.Delete(sourceItemId);
        }

        return Ok();
    }
}
