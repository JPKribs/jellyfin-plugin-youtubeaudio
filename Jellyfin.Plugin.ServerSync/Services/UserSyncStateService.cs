using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for applying synced user data to local users.
/// Processes per-property sync items (Policy, Configuration, ProfileImage).
/// </summary>
public class UserSyncStateService
{
    private readonly ILogger<UserSyncStateService> _logger;
    private readonly SyncDatabase _database;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    public UserSyncStateService(
        ILogger<UserSyncStateService> logger,
        SyncDatabase database,
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfigurationManager)
    {
        _logger = logger;
        _database = database;
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <summary>
    /// Applies queued user sync items to local users.
    /// </summary>
    /// <param name="sourceClient">Source server client for fetching images.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items synced.</returns>
    public async Task<int> ApplyQueuedChangesAsync(
        SourceServerClient sourceClient,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var queuedItems = _database.GetUserSyncItemsByStatus(BaseSyncStatus.Queued);
        if (queuedItems.Count == 0)
        {
            _logger.LogInformation("No queued user sync items to process");
            return 0;
        }

        _logger.LogInformation("Processing {Count} queued user sync items", queuedItems.Count);

        var successCount = 0;
        var errorCount = 0;
        var current = 0;

        foreach (var item in queuedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build context string for error messages
            var syncContext = $"[{item.PropertyCategory}] {item.SourceUserName ?? item.SourceUserId} -> {item.LocalUserName ?? item.LocalUserId}";

            try
            {
                var localUserId = Guid.Parse(item.LocalUserId);
                var localUser = _userManager.GetUserById(localUserId);

                if (localUser == null)
                {
                    var errorMsg = $"Local user not found: {item.LocalUserName ?? item.LocalUserId} (ID: {item.LocalUserId})";
                    _logger.LogWarning("{Context}: {Error}", syncContext, errorMsg);
                    _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                    errorCount++;
                    continue;
                }

                _logger.LogDebug("{Context}: Starting sync", syncContext);

                bool success;
                switch (item.PropertyCategory)
                {
                    case UserPropertyCategory.Policy:
                        success = await ApplyPolicyChangesAsync(localUser, item, syncContext).ConfigureAwait(false);
                        break;

                    case UserPropertyCategory.Configuration:
                        success = await ApplyConfigurationChangesAsync(localUser, item, syncContext).ConfigureAwait(false);
                        break;

                    case UserPropertyCategory.ProfileImage:
                        success = await ApplyProfileImageAsync(localUser, item, sourceClient, syncContext, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        var errorMsg = $"Unknown property category: {item.PropertyCategory}";
                        _logger.LogWarning("{Context}: {Error}", syncContext, errorMsg);
                        _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                        errorCount++;
                        continue;
                }

                if (success)
                {
                    successCount++;
                    _logger.LogDebug("{Context}: Sync completed successfully", syncContext);
                }
                else
                {
                    errorCount++;
                    _logger.LogWarning("{Context}: Sync failed", syncContext);
                }

                current++;
                progress.Report((double)current / queuedItems.Count * 100);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Unexpected error: {ex.Message}";
                _logger.LogError(ex, "{Context}: {Error}", syncContext, errorMsg);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                errorCount++;
            }
        }

        progress.Report(100);
        _logger.LogInformation("User sync complete. {Success} succeeded, {Errors} errors", successCount, errorCount);
        return successCount;
    }

    private async Task<bool> ApplyPolicyChangesAsync(User localUser, UserSyncItem item, string syncContext)
    {
        try
        {
            if (string.IsNullOrEmpty(item.MergedValue))
            {
                _logger.LogDebug("{Context}: No policy changes to apply (empty merged value)", syncContext);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            // Get current policy from UserDto
            var localUserDto = _userManager.GetUserDto(localUser);
            var localPolicy = localUserDto.Policy;
            if (localPolicy == null)
            {
                var errorMsg = $"Could not retrieve current policy for user {localUser.Username}";
                _logger.LogError("{Context}: {Error}", syncContext, errorMsg);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                return false;
            }

            // Parse merged policy JSON
            var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
            if (mergedProps == null)
            {
                _logger.LogDebug("{Context}: No policy properties to apply (null after deserialize)", syncContext);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            var policyType = localPolicy.GetType();
            var modified = false;
            var appliedProperties = new List<string>();

            foreach (var kvp in mergedProps)
            {
                try
                {
                    var property = policyType.GetProperty(kvp.Key);
                    if (property == null || !property.CanWrite)
                    {
                        _logger.LogDebug("{Context}: Skipping property {Property} (not found or read-only)", syncContext, kvp.Key);
                        continue;
                    }

                    // Deserialize and set the value
                    var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                    property.SetValue(localPolicy, value);
                    modified = true;
                    appliedProperties.Add(kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Context}: Failed to apply policy property {Property}", syncContext, kvp.Key);
                }
            }

            if (modified)
            {
                _logger.LogDebug("{Context}: Applying {Count} policy changes: {Properties}",
                    syncContext, appliedProperties.Count, string.Join(", ", appliedProperties));
                await _userManager.UpdatePolicyAsync(localUser.Id, localPolicy).ConfigureAwait(false);
            }

            // Update LocalValue to match MergedValue so HasChanges becomes false
            UpdateLocalValueAfterSync(item.Id, item.MergedValue);
            _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
            return true;
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to save policy changes: {ex.Message}";
            _logger.LogError(ex, "{Context}: {Error}", syncContext, errorMsg);
            _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
            return false;
        }
    }

    private async Task<bool> ApplyConfigurationChangesAsync(User localUser, UserSyncItem item, string syncContext)
    {
        try
        {
            if (string.IsNullOrEmpty(item.MergedValue))
            {
                _logger.LogDebug("{Context}: No configuration changes to apply (empty merged value)", syncContext);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            // Get current configuration from UserDto
            var localUserDto = _userManager.GetUserDto(localUser);
            var localConfig = localUserDto.Configuration;
            if (localConfig == null)
            {
                var errorMsg = $"Could not retrieve current configuration for user {localUser.Username}";
                _logger.LogError("{Context}: {Error}", syncContext, errorMsg);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                return false;
            }

            // Parse merged configuration JSON
            var mergedProps = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
            if (mergedProps == null)
            {
                _logger.LogDebug("{Context}: No configuration properties to apply (null after deserialize)", syncContext);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            var configType = localConfig.GetType();
            var modified = false;
            var appliedProperties = new List<string>();

            foreach (var kvp in mergedProps)
            {
                try
                {
                    var property = configType.GetProperty(kvp.Key);
                    if (property == null || !property.CanWrite)
                    {
                        _logger.LogDebug("{Context}: Skipping property {Property} (not found or read-only)", syncContext, kvp.Key);
                        continue;
                    }

                    // Deserialize and set the value
                    var value = JsonSerializer.Deserialize(kvp.Value.GetRawText(), property.PropertyType);
                    property.SetValue(localConfig, value);
                    modified = true;
                    appliedProperties.Add(kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Context}: Failed to apply configuration property {Property}", syncContext, kvp.Key);
                }
            }

            if (modified)
            {
                _logger.LogDebug("{Context}: Applying {Count} configuration changes: {Properties}",
                    syncContext, appliedProperties.Count, string.Join(", ", appliedProperties));
                await _userManager.UpdateConfigurationAsync(localUser.Id, localConfig).ConfigureAwait(false);
            }

            // Update LocalValue to match MergedValue so HasChanges becomes false
            UpdateLocalValueAfterSync(item.Id, item.MergedValue);
            _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
            return true;
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to save configuration changes: {ex.Message}";
            _logger.LogError(ex, "{Context}: {Error}", syncContext, errorMsg);
            _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
            return false;
        }
    }

    private async Task<bool> ApplyProfileImageAsync(
        User localUser,
        UserSyncItem item,
        SourceServerClient sourceClient,
        string syncContext,
        CancellationToken cancellationToken)
    {
        try
        {
            // Check if images are already in sync (by hash first, then size as fallback)
            if (!string.IsNullOrEmpty(item.SourceImageHash) && !string.IsNullOrEmpty(item.LocalImageHash))
            {
                if (string.Equals(item.SourceImageHash, item.LocalImageHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("{Context}: Images already in sync (hash: {Hash})", syncContext, item.SourceImageHash);
                    _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                    return true;
                }
            }
            else if (item.SourceImageSize.HasValue && item.LocalImageSize.HasValue &&
                     item.SourceImageSize == item.LocalImageSize)
            {
                // Fallback to size comparison if hashes not available
                _logger.LogDebug("{Context}: Images already in sync (size: {Size} bytes)", syncContext, item.SourceImageSize);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            // Check if source has no image
            bool sourceHasNoImage = string.IsNullOrEmpty(item.SourceImageHash) &&
                                    (!item.SourceImageSize.HasValue || item.SourceImageSize <= 0);
            if (sourceHasNoImage)
            {
                // Source has no image - remove local image if present
                if (localUser.ProfileImage != null)
                {
                    _logger.LogDebug("{Context}: Removing local profile image (source has none)", syncContext);
                    await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);
                    _logger.LogInformation("{Context}: Removed profile image", syncContext);
                }

                // Update synced hash/size to indicate no image, and clear local hash so HasChanges becomes false
                UpdateSyncedImageData(item.Id, null, 0, updateLocalHash: true);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }

            // Source has an image - download and apply
            var sourceUserId = Guid.Parse(item.SourceUserId);
            _logger.LogDebug("{Context}: Downloading profile image from source", syncContext);

            using var imageStream = await sourceClient.GetUserImageAsync(sourceUserId, cancellationToken).ConfigureAwait(false);

            if (imageStream == null)
            {
                var errorMsg = $"Failed to download profile image from source server (user: {item.SourceUserName ?? item.SourceUserId})";
                _logger.LogError("{Context}: {Error}", syncContext, errorMsg);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                return false;
            }

            // Save to a temp file first
            var tempPath = Path.GetTempFileName() + ".jpg";
            try
            {
                using (var fileStream = File.Create(tempPath))
                {
                    await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                // Compute hash of downloaded image for verification
                string downloadedHash;
                using (var verifyStream = File.OpenRead(tempPath))
                {
                    downloadedHash = HashUtilities.ComputeSha256Hash(verifyStream);
                }

                _logger.LogDebug("{Context}: Downloaded image hash: {Hash}", syncContext, downloadedHash);

                // Clear existing profile image first (this is how Jellyfin's API does it)
                if (localUser.ProfileImage != null)
                {
                    _logger.LogDebug("{Context}: Clearing existing profile image before setting new one", syncContext);
                    await _userManager.ClearProfileImageAsync(localUser).ConfigureAwait(false);

                    // Re-fetch user after clearing to get fresh entity state
                    var refreshedUser = _userManager.GetUserById(localUser.Id);
                    if (refreshedUser == null)
                    {
                        var errorMsg = "User disappeared after clearing profile image";
                        _logger.LogError("{Context}: {Error}", syncContext, errorMsg);
                        _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
                        return false;
                    }

                    localUser = refreshedUser;
                }

                // Get user data path and set profile image
                var userDataPath = Path.Combine(
                    _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath,
                    localUser.Username);
                Directory.CreateDirectory(userDataPath);

                var profilePath = Path.Combine(userDataPath, "profile.jpg");

                // Save the image file via provider manager FIRST
                using (var profileStream = File.OpenRead(tempPath))
                {
                    await _providerManager.SaveImage(profileStream, "image/jpeg", profilePath).ConfigureAwait(false);
                }

                // Now set the profile image on the user and save
                localUser.ProfileImage = new ImageInfo(profilePath);

                // Update the user to persist the profile image reference
                _logger.LogDebug("{Context}: Saving user with new profile image", syncContext);
                await _userManager.UpdateUserAsync(localUser).ConfigureAwait(false);

                // Update the synced image hash/size to track what we've synced
                // Also update LocalImageHash to match so HasChanges becomes false
                var downloadedSize = new FileInfo(tempPath).Length;
                UpdateSyncedImageData(item.Id, downloadedHash, downloadedSize, updateLocalHash: true);

                _logger.LogInformation("{Context}: Updated profile image (hash: {Hash}, size: {Size} bytes)",
                    syncContext, downloadedHash, downloadedSize);
                _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Synced);
                return true;
            }
            finally
            {
                // Clean up temp file
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to apply profile image: {ex.Message}";
            _logger.LogError(ex, "{Context}: {Error}", syncContext, errorMsg);
            _database.UpdateUserSyncItemStatusById(item.Id, BaseSyncStatus.Errored, errorMsg);
            return false;
        }
    }

    /// <summary>
    /// Updates the synced image hash and size after successfully syncing an image.
    /// </summary>
    /// <param name="itemId">The item ID to update.</param>
    /// <param name="imageHash">The synced image hash.</param>
    /// <param name="imageSize">The synced image size.</param>
    /// <param name="updateLocalHash">If true, also updates LocalImageHash to match SourceImageHash so HasChanges becomes false.</param>
    private void UpdateSyncedImageData(long itemId, string? imageHash, long? imageSize, bool updateLocalHash = false)
    {
        try
        {
            var item = _database.GetUserSyncItemById(itemId);
            if (item != null)
            {
                item.SyncedImageHash = imageHash;
                item.SyncedImageSize = imageSize;
                item.LastSyncTime = DateTime.UtcNow;

                // Update LocalImageHash/Size to match Source so HasChanges becomes false
                if (updateLocalHash)
                {
                    item.LocalImageHash = item.SourceImageHash;
                    item.LocalImageSize = item.SourceImageSize;
                    // Also update LocalValue display to match source
                    item.LocalValue = item.SourceValue;
                }

                _database.UpsertUserSyncItem(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error updating synced image data");
        }
    }

    /// <summary>
    /// Updates the LocalValue to match MergedValue after a successful sync.
    /// This ensures HasChanges becomes false without requiring a full table refresh.
    /// </summary>
    private void UpdateLocalValueAfterSync(long itemId, string? mergedValue)
    {
        try
        {
            var item = _database.GetUserSyncItemById(itemId);
            if (item != null)
            {
                item.LocalValue = mergedValue;
                item.LastSyncTime = DateTime.UtcNow;
                _database.UpsertUserSyncItem(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error updating LocalValue after sync");
        }
    }
}
