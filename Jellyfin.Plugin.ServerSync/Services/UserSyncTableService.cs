using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for scanning users and populating the user sync table.
/// Creates one record per property category (Policy, Configuration, ProfileImage) per user mapping.
/// </summary>
public class UserSyncTableService
{
    private readonly ILogger<UserSyncTableService> _logger;
    private readonly SyncDatabase _database;
    private readonly IUserManager _userManager;

    public UserSyncTableService(
        ILogger<UserSyncTableService> logger,
        SyncDatabase database,
        IUserManager userManager)
    {
        _logger = logger;
        _database = database;
        _userManager = userManager;
    }

    /// <summary>
    /// Refreshes the user sync table by comparing source and local user data.
    /// Creates up to 3 records per user mapping (Policy, Configuration, ProfileImage).
    /// </summary>
    /// <param name="sourceClient">Source server client.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of records processed.</returns>
    public async Task<int> RefreshUserSyncTableAsync(
        SourceServerClient sourceClient,
        PluginConfiguration config,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var enabledMappings = config.UserMappings.Where(m => m.IsEnabled).ToList();
        if (enabledMappings.Count == 0)
        {
            _logger.LogWarning("No enabled user mappings found, skipping user sync table refresh");
            return 0;
        }

        var itemsProcessed = 0;
        var currentMapping = 0;

        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mappingProgress = (double)currentMapping / enabledMappings.Count * 100;
            progress.Report(mappingProgress);

            try
            {
                var sourceUserId = Guid.Parse(mapping.SourceUserId);
                var localUserId = Guid.Parse(mapping.LocalUserId);

                // Get source user details
                var sourceUser = await sourceClient.GetUserAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
                if (sourceUser == null)
                {
                    _logger.LogWarning("Source user {UserId} not found, skipping", mapping.SourceUserId);
                    continue;
                }

                // Get local user details
                var localUser = _userManager.GetUserById(localUserId);
                if (localUser == null)
                {
                    _logger.LogWarning("Local user {UserId} not found, skipping", mapping.LocalUserId);
                    continue;
                }

                // Get local user DTO to access Policy and Configuration
                var localUserDto = _userManager.GetUserDto(localUser);

                // Process Policy sync item
                if (config.UserSyncPolicy)
                {
                    var policyItem = await CreatePolicySyncItemAsync(
                        mapping, sourceUser, localUserDto, config).ConfigureAwait(false);
                    if (policyItem != null)
                    {
                        _database.UpsertUserSyncItem(policyItem);
                        itemsProcessed++;
                    }
                }

                // Process Configuration sync item
                if (config.UserSyncConfiguration)
                {
                    var configItem = await CreateConfigurationSyncItemAsync(
                        mapping, sourceUser, localUserDto).ConfigureAwait(false);
                    if (configItem != null)
                    {
                        _database.UpsertUserSyncItem(configItem);
                        itemsProcessed++;
                    }
                }

                // Process ProfileImage sync item
                if (config.UserSyncProfileImage)
                {
                    var imageItem = await CreateProfileImageSyncItemAsync(
                        mapping, sourceUser, localUser, sourceClient, cancellationToken).ConfigureAwait(false);
                    if (imageItem != null)
                    {
                        _database.UpsertUserSyncItem(imageItem);
                        itemsProcessed++;
                    }
                }

                _logger.LogDebug(
                    "Processed user mapping {SourceUser} -> {LocalUser}",
                    sourceUser.Name,
                    localUserDto.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing user mapping {SourceUser} -> {LocalUser}",
                    mapping.SourceUserName, mapping.LocalUserName);
            }

            currentMapping++;
        }

        progress.Report(100);
        _logger.LogInformation("User sync table refresh complete. {Count} records processed", itemsProcessed);
        return itemsProcessed;
    }

    private Task<UserSyncItem?> CreatePolicySyncItemAsync(
        UserMapping mapping,
        dynamic sourceUser,
        dynamic localUserDto,
        PluginConfiguration config)
    {
        var sourcePolicy = UserSyncMergeService.ExtractPolicyJson(sourceUser.Policy);
        var localPolicy = UserSyncMergeService.ExtractPolicyJson(localUserDto.Policy);
        var mergedPolicy = UserSyncMergeService.ComputeMergedPolicy(sourcePolicy, config.LibraryMappings);

        // Get existing item to check status
        var existingItem = _database.GetUserSyncItem(
            mapping.SourceUserId, mapping.LocalUserId, UserPropertyCategory.Policy);

        var item = new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            SourceUserName = sourceUser.Name,
            LocalUserName = localUserDto.Name,
            PropertyCategory = UserPropertyCategory.Policy,
            SourceValue = sourcePolicy,
            LocalValue = localPolicy,
            MergedValue = mergedPolicy,
            StatusDate = DateTime.UtcNow
        };

        // Determine status - compare MERGED to LOCAL (what we want vs what exists)
        // Use semantic JSON comparison instead of string comparison
        var hasChanges = !UserSyncMergeService.JsonEquals(mergedPolicy, localPolicy);
        item.Status = hasChanges ? BaseSyncStatus.Queued : BaseSyncStatus.Synced;

        // Preserve LastSyncTime and Ignored status
        if (existingItem != null)
        {
            if (existingItem.Status == BaseSyncStatus.Ignored)
            {
                item.Status = BaseSyncStatus.Ignored;
            }

            if (!hasChanges)
            {
                item.LastSyncTime = existingItem.LastSyncTime;
            }
        }
        else if (!hasChanges)
        {
            // New item that's already in sync - set LastSyncTime to now
            item.LastSyncTime = DateTime.UtcNow;
        }

        return Task.FromResult<UserSyncItem?>(item);
    }

    private Task<UserSyncItem?> CreateConfigurationSyncItemAsync(
        UserMapping mapping,
        dynamic sourceUser,
        dynamic localUserDto)
    {
        var sourceConfig = UserSyncMergeService.ExtractConfigurationJson(sourceUser.Configuration);
        var localConfig = UserSyncMergeService.ExtractConfigurationJson(localUserDto.Configuration);
        var mergedConfig = sourceConfig; // Source-wins

        // Get existing item to check status
        var existingItem = _database.GetUserSyncItem(
            mapping.SourceUserId, mapping.LocalUserId, UserPropertyCategory.Configuration);

        var item = new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            SourceUserName = sourceUser.Name,
            LocalUserName = localUserDto.Name,
            PropertyCategory = UserPropertyCategory.Configuration,
            SourceValue = sourceConfig,
            LocalValue = localConfig,
            MergedValue = mergedConfig,
            StatusDate = DateTime.UtcNow
        };

        // Determine status - compare MERGED to LOCAL (what we want vs what exists)
        // Use semantic JSON comparison instead of string comparison
        var hasChanges = !UserSyncMergeService.JsonEquals(mergedConfig, localConfig);
        item.Status = hasChanges ? BaseSyncStatus.Queued : BaseSyncStatus.Synced;

        // Preserve LastSyncTime and Ignored status
        if (existingItem != null)
        {
            if (existingItem.Status == BaseSyncStatus.Ignored)
            {
                item.Status = BaseSyncStatus.Ignored;
            }

            if (!hasChanges)
            {
                item.LastSyncTime = existingItem.LastSyncTime;
            }
        }
        else if (!hasChanges)
        {
            // New item that's already in sync - set LastSyncTime to now
            item.LastSyncTime = DateTime.UtcNow;
        }

        return Task.FromResult<UserSyncItem?>(item);
    }

    private async Task<UserSyncItem?> CreateProfileImageSyncItemAsync(
        UserMapping mapping,
        dynamic sourceUser,
        Jellyfin.Database.Implementations.Entities.User localUser,
        SourceServerClient sourceClient,
        CancellationToken cancellationToken)
    {
        var sourceUserId = Guid.Parse(mapping.SourceUserId);
        var sourceUserName = (string)sourceUser.Name;
        string? sourceImageHash = null;
        long? sourceImageSize = null;
        string? localImageHash = null;
        long? localImageSize = null;

        // Get source image hash (and size for display)
        if (!string.IsNullOrEmpty(sourceUser.PrimaryImageTag as string))
        {
            _logger.LogDebug(
                "ProfileImage: Fetching source image hash for user {SourceUser} ({SourceUserId})",
                sourceUserName, sourceUserId);

            sourceImageHash = await sourceClient.GetUserImageHashAsync(sourceUserId, cancellationToken).ConfigureAwait(false);
            sourceImageSize = await sourceClient.GetUserImageSizeAsync(sourceUserId, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "ProfileImage: Source image for {SourceUser}: hash={Hash}, size={Size}",
                sourceUserName, sourceImageHash ?? "(none)", sourceImageSize);
        }
        else
        {
            _logger.LogDebug("ProfileImage: Source user {SourceUser} has no profile image", sourceUserName);
        }

        // Get local image hash (and size for display)
        if (localUser.ProfileImage != null && !string.IsNullOrEmpty(localUser.ProfileImage.Path))
        {
            try
            {
                if (File.Exists(localUser.ProfileImage.Path))
                {
                    var fileInfo = new FileInfo(localUser.ProfileImage.Path);
                    localImageSize = fileInfo.Length;

                    using var fileStream = File.OpenRead(localUser.ProfileImage.Path);
                    localImageHash = HashUtilities.ComputeSha256Hash(fileStream);

                    _logger.LogDebug(
                        "ProfileImage: Local image for {LocalUser}: hash={Hash}, size={Size}, path={Path}",
                        localUser.Username, localImageHash, localImageSize, localUser.ProfileImage.Path);
                }
                else
                {
                    _logger.LogDebug(
                        "ProfileImage: Local profile image path does not exist for {LocalUser}: {Path}",
                        localUser.Username, localUser.ProfileImage.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ProfileImage: Failed to get local profile image hash for user {LocalUser}",
                    localUser.Username);
            }
        }
        else
        {
            _logger.LogDebug("ProfileImage: Local user {LocalUser} has no profile image", localUser.Username);
        }

        // Get existing item to preserve synced hash
        var existingItem = _database.GetUserSyncItem(
            mapping.SourceUserId, mapping.LocalUserId, UserPropertyCategory.ProfileImage);

        // Determine SyncedImageHash:
        // 1. If we have an existing record, use its SyncedImageHash
        // 2. If source == local hash, they're already in sync
        // 3. Otherwise, leave null (needs sync)
        string? syncedImageHash = existingItem?.SyncedImageHash;
        if (string.IsNullOrEmpty(syncedImageHash) &&
            !string.IsNullOrEmpty(sourceImageHash) &&
            string.Equals(sourceImageHash, localImageHash, StringComparison.OrdinalIgnoreCase))
        {
            syncedImageHash = sourceImageHash;
        }

        // Build display values
        string sourceDisplay = !string.IsNullOrEmpty(sourceImageHash)
            ? $"{FormatUtilities.FormatBytes(sourceImageSize ?? 0)} ({sourceImageHash[..8]}...)"
            : "No image";
        string localDisplay = !string.IsNullOrEmpty(localImageHash)
            ? $"{FormatUtilities.FormatBytes(localImageSize ?? 0)} ({localImageHash[..8]}...)"
            : "No image";

        var item = new UserSyncItem
        {
            SourceUserId = mapping.SourceUserId,
            LocalUserId = mapping.LocalUserId,
            SourceUserName = sourceUser.Name,
            LocalUserName = localUser.Username,
            PropertyCategory = UserPropertyCategory.ProfileImage,
            SourceValue = sourceDisplay,
            LocalValue = localDisplay,
            MergedValue = sourceDisplay,
            SourceImageHash = sourceImageHash,
            LocalImageHash = localImageHash,
            SyncedImageHash = syncedImageHash,
            // Keep size for legacy/display purposes
            SourceImageSize = sourceImageSize,
            LocalImageSize = localImageSize,
            SyncedImageSize = existingItem?.SyncedImageSize,
            StatusDate = DateTime.UtcNow
        };

        // Determine status based on hash comparison (primary) or size comparison (fallback)
        // This must match the HasChanges property logic in UserSyncItem
        bool hasChanges;
        if (!string.IsNullOrEmpty(sourceImageHash))
        {
            // Primary: hash comparison
            hasChanges = !string.Equals(sourceImageHash, localImageHash, StringComparison.OrdinalIgnoreCase);
            _logger.LogDebug(
                "ProfileImage: Hash comparison for {SourceUser} -> {LocalUser}: source={SourceHash}, local={LocalHash}, hasChanges={HasChanges}",
                sourceUserName, localUser.Username,
                sourceImageHash, localImageHash ?? "(none)", hasChanges);
        }
        else
        {
            // Fallback: size comparison (legacy or no source image)
            hasChanges = sourceImageSize.HasValue &&
                         sourceImageSize > 0 &&
                         sourceImageSize != localImageSize;
            _logger.LogDebug(
                "ProfileImage: Size comparison fallback for {SourceUser} -> {LocalUser}: source={SourceSize}, local={LocalSize}, hasChanges={HasChanges}",
                sourceUserName, localUser.Username,
                sourceImageSize, localImageSize, hasChanges);
        }

        item.Status = hasChanges ? BaseSyncStatus.Queued : BaseSyncStatus.Synced;

        _logger.LogDebug(
            "ProfileImage: Final status for {SourceUser} -> {LocalUser}: {Status}",
            sourceUserName, localUser.Username, item.Status);

        // Preserve LastSyncTime and Ignored status
        if (existingItem != null)
        {
            if (existingItem.Status == BaseSyncStatus.Ignored)
            {
                item.Status = BaseSyncStatus.Ignored;
                _logger.LogDebug(
                    "ProfileImage: Preserving Ignored status for {SourceUser} -> {LocalUser}",
                    sourceUserName, localUser.Username);
            }

            if (!hasChanges)
            {
                item.LastSyncTime = existingItem.LastSyncTime;
            }
        }
        else if (!hasChanges)
        {
            // New item that's already in sync - set LastSyncTime to now
            item.LastSyncTime = DateTime.UtcNow;
        }

        return item;
    }
}
