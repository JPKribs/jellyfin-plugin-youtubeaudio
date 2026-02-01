using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Property categories for user sync items.
/// </summary>
public static class UserPropertyCategory
{
    /// <summary>
    /// User policy settings (permissions, restrictions).
    /// </summary>
    public const string Policy = "Policy";

    /// <summary>
    /// User configuration settings (preferences).
    /// </summary>
    public const string Configuration = "Configuration";

    /// <summary>
    /// User profile image.
    /// </summary>
    public const string ProfileImage = "ProfileImage";
}

/// <summary>
/// Represents a single property sync record for a user mapping.
/// One record per property category (Policy, Configuration, ProfileImage) per user mapping.
/// Similar to HistorySyncItem in structure.
/// </summary>
public class UserSyncItem
{
    /// <summary>
    /// Gets or sets the unique database identifier.
    /// </summary>
    public long Id { get; set; }

    // ===== User Mapping =====

    /// <summary>
    /// Gets or sets the source server user ID.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server user ID.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source user's display name.
    /// </summary>
    public string? SourceUserName { get; set; }

    /// <summary>
    /// Gets or sets the local user's display name.
    /// </summary>
    public string? LocalUserName { get; set; }

    // ===== Property Info =====

    /// <summary>
    /// Gets or sets the property category (Policy, Configuration, ProfileImage).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    // ===== Values =====

    /// <summary>
    /// Gets or sets the source value (JSON for Policy/Config, size string for ProfileImage).
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Gets or sets the local value.
    /// </summary>
    public string? LocalValue { get; set; }

    /// <summary>
    /// Gets or sets the merged value (what will be applied).
    /// </summary>
    public string? MergedValue { get; set; }

    // ===== Profile Image Specific (for hash-based comparison) =====

    /// <summary>
    /// Gets or sets the source profile image hash (SHA256, truncated).
    /// </summary>
    public string? SourceImageHash { get; set; }

    /// <summary>
    /// Gets or sets the local profile image hash (SHA256, truncated).
    /// </summary>
    public string? LocalImageHash { get; set; }

    /// <summary>
    /// Gets or sets the last synced image hash (to detect if we already synced).
    /// </summary>
    public string? SyncedImageHash { get; set; }

    // Legacy size fields - kept for backward compatibility during migration
    /// <summary>
    /// Gets or sets the source profile image size in bytes (legacy, use hash instead).
    /// </summary>
    public long? SourceImageSize { get; set; }

    /// <summary>
    /// Gets or sets the local profile image size in bytes (legacy, use hash instead).
    /// </summary>
    public long? LocalImageSize { get; set; }

    /// <summary>
    /// Gets or sets the last synced image size (legacy, use hash instead).
    /// </summary>
    public long? SyncedImageSize { get; set; }

    // ===== Sync Tracking =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public BaseSyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when the status was last changed.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets when the item was last synced.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error message if status is Errored.
    /// </summary>
    public string? ErrorMessage { get; set; }

    // ===== Computed Properties =====

    /// <summary>
    /// Gets a value indicating whether there are changes to sync.
    /// For ProfileImage: compares hashes. For others: compares merged vs local JSON values.
    /// </summary>
    public bool HasChanges
    {
        get
        {
            if (PropertyCategory == UserPropertyCategory.ProfileImage)
            {
                // Compare by hash for profile images (most accurate)
                // Has changes if source has an image and hash differs from local
                if (!string.IsNullOrEmpty(SourceImageHash))
                {
                    return !string.Equals(SourceImageHash, LocalImageHash, StringComparison.OrdinalIgnoreCase);
                }

                // Fallback to size comparison if hash not available (legacy data)
                return SourceImageSize.HasValue &&
                       SourceImageSize > 0 &&
                       SourceImageSize != LocalImageSize;
            }

            // For Policy and Configuration, compare merged value (what we want) to local value (what exists)
            // using semantic JSON comparison from shared utility
            return !JsonComparisonUtility.JsonEquals(MergedValue, LocalValue);
        }
    }

    /// <summary>
    /// Gets a display-friendly summary of the change.
    /// </summary>
    public string ChangesSummary
    {
        get
        {
            if (!HasChanges)
            {
                return "No changes";
            }

            if (PropertyCategory == UserPropertyCategory.ProfileImage)
            {
                var sourceSize = SourceImageSize.HasValue ? FormatUtilities.FormatBytes(SourceImageSize.Value) : "None";
                return sourceSize;
            }

            // Count the number of differing properties using shared utility
            var diffCount = JsonComparisonUtility.CountDifferences(MergedValue, LocalValue);
            if (diffCount == 0)
            {
                return "No changes";
            }

            return diffCount == 1 ? "1 difference" : $"{diffCount} differences";
        }
    }
}
