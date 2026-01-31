using System;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for merging watch history data from source and local servers.
/// Implements two-way merge logic: takes the "best" of both servers' data.
/// </summary>
public static class HistorySyncMergeService
{
    /// <summary>
    /// Merges source and local history data to produce the target merged state.
    /// Merge strategy:
    /// - PlayCount: MAX(source, local)
    /// - LastPlayedDate: MAX(source, local)
    /// - PlaybackPositionTicks: From whichever has more recent LastPlayedDate
    /// - IsFavorite: TRUE if either is true (OR logic)
    /// - IsPlayed: TRUE if either is true (OR logic)
    /// </summary>
    /// <param name="item">History sync item to update with merged values.</param>
    public static void MergeHistoryData(HistorySyncItem item)
    {
        // IsPlayed: OR logic - true if either server has it marked as played
        item.MergedIsPlayed = (item.SourceIsPlayed ?? false) || (item.LocalIsPlayed ?? false);

        // PlayCount: Take the maximum
        item.MergedPlayCount = Math.Max(item.SourcePlayCount ?? 0, item.LocalPlayCount ?? 0);

        // IsFavorite: OR logic - true if either server has it as favorite
        item.MergedIsFavorite = (item.SourceIsFavorite ?? false) || (item.LocalIsFavorite ?? false);

        // LastPlayedDate and PlaybackPositionTicks: Use the more recent activity
        MergePlaybackPosition(item);
    }

    /// <summary>
    /// Merges playback position based on which server has the more recent activity.
    /// </summary>
    private static void MergePlaybackPosition(HistorySyncItem item)
    {
        var sourceDate = item.SourceLastPlayedDate;
        var localDate = item.LocalLastPlayedDate;

        // If neither has a date, use source position if available
        if (!sourceDate.HasValue && !localDate.HasValue)
        {
            item.MergedLastPlayedDate = null;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks ?? item.LocalPlaybackPositionTicks;
            return;
        }

        // If only source has a date
        if (sourceDate.HasValue && !localDate.HasValue)
        {
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
            return;
        }

        // If only local has a date
        if (!sourceDate.HasValue && localDate.HasValue)
        {
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
            return;
        }

        // Both have dates - use the more recent one
        if (sourceDate!.Value >= localDate!.Value)
        {
            item.MergedLastPlayedDate = sourceDate;
            item.MergedPlaybackPositionTicks = item.SourcePlaybackPositionTicks;
        }
        else
        {
            item.MergedLastPlayedDate = localDate;
            item.MergedPlaybackPositionTicks = item.LocalPlaybackPositionTicks;
        }
    }

    /// <summary>
    /// Determines if there are meaningful changes to sync for this item.
    /// </summary>
    /// <param name="item">History sync item to check.</param>
    /// <returns>True if there are changes that need to be synced to local.</returns>
    public static bool HasChangesToSync(HistorySyncItem item)
    {
        // Check if merged values differ from local values
        if (item.MergedIsPlayed != item.LocalIsPlayed && item.MergedIsPlayed.HasValue)
        {
            return true;
        }

        if (item.MergedPlayCount != item.LocalPlayCount && item.MergedPlayCount.HasValue && item.MergedPlayCount > 0)
        {
            return true;
        }

        if (item.MergedPlaybackPositionTicks != item.LocalPlaybackPositionTicks && item.MergedPlaybackPositionTicks.HasValue)
        {
            return true;
        }

        if (item.MergedIsFavorite != item.LocalIsFavorite && item.MergedIsFavorite.HasValue)
        {
            return true;
        }

        // Also check if we have source data but no local data was matched yet
        if (string.IsNullOrEmpty(item.LocalItemId) && !string.IsNullOrEmpty(item.SourceItemId))
        {
            // Item exists on source but hasn't been matched locally
            return false; // Can't sync without local item
        }

        return false;
    }

    /// <summary>
    /// Creates a summary of changes for logging/display purposes.
    /// </summary>
    /// <param name="item">History sync item.</param>
    /// <returns>Human-readable summary of changes.</returns>
    public static string GetChangeSummary(HistorySyncItem item)
    {
        var changes = new System.Collections.Generic.List<string>();

        if (item.MergedIsPlayed != item.LocalIsPlayed && item.MergedIsPlayed.HasValue)
        {
            changes.Add($"Played: {item.LocalIsPlayed ?? false} -> {item.MergedIsPlayed.Value}");
        }

        if (item.MergedPlayCount != item.LocalPlayCount && item.MergedPlayCount.HasValue)
        {
            changes.Add($"PlayCount: {item.LocalPlayCount ?? 0} -> {item.MergedPlayCount.Value}");
        }

        if (item.MergedPlaybackPositionTicks != item.LocalPlaybackPositionTicks && item.MergedPlaybackPositionTicks.HasValue)
        {
            var localPos = TimeSpan.FromTicks(item.LocalPlaybackPositionTicks ?? 0);
            var mergedPos = TimeSpan.FromTicks(item.MergedPlaybackPositionTicks.Value);
            changes.Add($"Position: {localPos:hh\\:mm\\:ss} -> {mergedPos:hh\\:mm\\:ss}");
        }

        if (item.MergedIsFavorite != item.LocalIsFavorite && item.MergedIsFavorite.HasValue)
        {
            changes.Add($"Favorite: {item.LocalIsFavorite ?? false} -> {item.MergedIsFavorite.Value}");
        }

        return changes.Count > 0 ? string.Join(", ", changes) : "No changes";
    }
}
