using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Provides a reusable paginated fetch loop with retry logic and path filtering.
/// Eliminates duplication across SyncTableService, HistorySyncTableService, and MetadataSyncTableService.
/// </summary>
public static class PaginatedFetchUtility
{
    /// <summary>
    /// Default number of items to fetch per page.
    /// </summary>
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Maximum number of consecutive fetch errors before aborting.
    /// </summary>
    public const int MaxConsecutiveErrors = 3;

    /// <summary>
    /// Delegate for fetching a page of items from the source server.
    /// </summary>
    /// <param name="startIndex">The starting index for the page.</param>
    /// <param name="batchSize">The number of items to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing the items.</returns>
    public delegate Task<BaseItemDtoQueryResult?> FetchPageAsync(int startIndex, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Delegate for processing a single item from a fetched page.
    /// </summary>
    /// <param name="item">The item to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the item was processed (counts toward total), false otherwise.</returns>
    public delegate Task<bool> ProcessItemAsync(BaseItemDto item, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a paginated fetch loop with retry logic and path filtering.
    /// </summary>
    /// <param name="fetchPage">Delegate that fetches a page of items.</param>
    /// <param name="processItem">Delegate that processes a single item. Returns true if the item was successfully processed.</param>
    /// <param name="libraryName">Library name for logging.</param>
    /// <param name="sourceRootPath">Source root path for ignored-path filtering.</param>
    /// <param name="ignoredPaths">List of ignored paths, or null.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback invoked after each item is visited (regardless of processing outcome).</param>
    /// <returns>The number of items successfully processed.</returns>
    public static async Task<int> FetchAllPagesAsync(
        FetchPageAsync fetchPage,
        ProcessItemAsync processItem,
        string libraryName,
        string? sourceRootPath,
        List<string>? ignoredPaths,
        ILogger logger,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            BaseItemDtoQueryResult? result;
            try
            {
                result = await fetchPage(startIndex, DefaultBatchSize, cancellationToken).ConfigureAwait(false);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                logger.LogWarning(ex,
                    "Failed to fetch items from library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    libraryName, startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "Too many consecutive errors fetching from {LibraryName}, stopping sync for this library",
                        libraryName);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(consecutiveErrors * 2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (result?.Items == null || result.Items.Count == 0)
            {
                break;
            }

            foreach (var item in result.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (item.Id == null || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                // Skip items under ignored folder paths
                if (PathUtilities.IsPathIgnored(item.Path, sourceRootPath ?? string.Empty, ignoredPaths))
                {
                    continue;
                }

                try
                {
                    var wasProcessed = await processItem(item, cancellationToken).ConfigureAwait(false);
                    if (wasProcessed)
                    {
                        processedItems++;
                    }

                    onItemProcessed?.Invoke();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                }
            }

            startIndex += DefaultBatchSize;

            if (result.Items.Count < DefaultBatchSize)
            {
                break;
            }
        }

        return processedItems;
    }
}
