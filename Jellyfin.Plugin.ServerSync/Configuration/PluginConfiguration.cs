using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ServerSync.Configuration;

// PluginConfiguration
// Configuration settings for the Server Sync plugin.
public class PluginConfiguration : BasePluginConfiguration
{
    public string SourceServerUrl { get; set; } = string.Empty;

    public string SourceServerApiKey { get; set; } = string.Empty;

    public string SourceServerName { get; set; } = string.Empty;

    public string SourceServerId { get; set; } = string.Empty;

    public bool EnableContentSync { get; set; }

    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    public string? TempDownloadPath { get; set; }

    public bool IncludeCompanionFiles { get; set; } = true;

    public int MaxConcurrentDownloads { get; set; } = 2;

    // MaxDownloadSpeed
    // Maximum download speed value (0 = unlimited).
    public int MaxDownloadSpeed { get; set; } = 0;

    // DownloadSpeedUnit
    // Unit for MaxDownloadSpeed (KB, MB, GB).
    public string DownloadSpeedUnit { get; set; } = "MB";

    // GetMaxDownloadSpeedBytes
    // Calculates the max download speed in bytes per second.
    public long GetMaxDownloadSpeedBytes()
    {
        if (MaxDownloadSpeed == 0) return 0;

        return DownloadSpeedUnit switch
        {
            "KB" => MaxDownloadSpeed * 1024L,
            "MB" => MaxDownloadSpeed * 1024L * 1024L,
            "GB" => MaxDownloadSpeed * 1024L * 1024L * 1024L,
            _ => MaxDownloadSpeed * 1024L * 1024L // Default to MB
        };
    }

    // GetScheduledDownloadSpeedBytes
    // Calculates the scheduled download speed in bytes per second.
    public long GetScheduledDownloadSpeedBytes()
    {
        if (ScheduledDownloadSpeed == 0) return 0;

        return ScheduledDownloadSpeedUnit switch
        {
            "KB" => ScheduledDownloadSpeed * 1024L,
            "MB" => ScheduledDownloadSpeed * 1024L * 1024L,
            "GB" => ScheduledDownloadSpeed * 1024L * 1024L * 1024L,
            _ => ScheduledDownloadSpeed * 1024L * 1024L // Default to MB
        };
    }

    // GetEffectiveDownloadSpeedBytes
    // Returns the appropriate download speed based on current time and scheduling settings.
    public long GetEffectiveDownloadSpeedBytes()
    {
        if (!EnableBandwidthScheduling)
        {
            return GetMaxDownloadSpeedBytes();
        }

        var currentHour = DateTime.Now.Hour;
        var isInScheduledWindow = ScheduledStartHour <= ScheduledEndHour
            ? currentHour >= ScheduledStartHour && currentHour < ScheduledEndHour
            : currentHour >= ScheduledStartHour || currentHour < ScheduledEndHour;

        return isInScheduledWindow ? GetScheduledDownloadSpeedBytes() : GetMaxDownloadSpeedBytes();
    }

    // DownloadNewContentMode
    // Controls how new content (items on source that don't exist locally) is handled.
    // Enabled: Automatically queue for download
    // RequireApproval: Require manual approval before downloading
    // Disabled: Don't download new content
    public ApprovalMode DownloadNewContentMode { get; set; } = ApprovalMode.Enabled;

    // ReplaceExistingContentMode
    // Controls how updated content (items that differ from local version) is handled.
    // Enabled: Automatically queue for re-download
    // RequireApproval: Require manual approval before replacing
    // Disabled: Don't replace existing content
    public ApprovalMode ReplaceExistingContentMode { get; set; } = ApprovalMode.Enabled;

    // DeleteMissingContentMode
    // Controls how missing content (items on local that don't exist on source) is handled.
    // Enabled: Automatically queue for deletion
    // RequireApproval: Require manual approval before deleting
    // Disabled: Don't delete missing content
    public ApprovalMode DeleteMissingContentMode { get; set; } = ApprovalMode.Disabled;

    // DetectUpdatedFiles
    // Re-queue files with size or date mismatches when enabled.
    public bool DetectUpdatedFiles { get; set; } = true;

    // EnableBandwidthScheduling
    // Enable time-based bandwidth scheduling with alternate speed.
    public bool EnableBandwidthScheduling { get; set; }

    // ScheduledStartHour
    // Hour of day (0-23) when scheduled bandwidth starts.
    public int ScheduledStartHour { get; set; } = 0;

    // ScheduledEndHour
    // Hour of day (0-24) when scheduled bandwidth ends.
    public int ScheduledEndHour { get; set; } = 6;

    // ScheduledDownloadSpeed
    // Download speed during scheduled hours.
    public int ScheduledDownloadSpeed { get; set; } = 0;

    // ScheduledDownloadSpeedUnit
    // Unit for scheduled download speed (KB, MB, GB).
    public string ScheduledDownloadSpeedUnit { get; set; } = "MB";

    // MinimumFreeDiskSpaceGb
    // Minimum free disk space required before downloads (in GB).
    public int MinimumFreeDiskSpaceGb { get; set; } = 10;

    // LastConnectionCheck
    // Timestamp of last successful connection check.
    public DateTime? LastConnectionCheck { get; set; }

    // LastSyncStartTime
    // Timestamp when the last sync started.
    public DateTime? LastSyncStartTime { get; set; }

    // LastSyncEndTime
    // Timestamp when the last sync completed.
    public DateTime? LastSyncEndTime { get; set; }

    // EnableRecyclingBin
    // Move deleted/replaced files to a recycling bin instead of permanent deletion.
    public bool EnableRecyclingBin { get; set; }

    // RecyclingBinPath
    // Path to the recycling bin directory for soft-deleted files.
    public string? RecyclingBinPath { get; set; }

    // RecyclingBinRetentionDays
    // Number of days to keep files in the recycling bin before permanent deletion.
    public int RecyclingBinRetentionDays { get; set; } = 7;

    // ValidateConfiguration
    // Validates configuration values and returns a list of validation errors.
    public List<string> ValidateConfiguration()
    {
        var errors = new List<string>();

        // Validate URL
        if (!string.IsNullOrWhiteSpace(SourceServerUrl))
        {
            if (!Uri.TryCreate(SourceServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                errors.Add("Source server URL must be a valid HTTP or HTTPS URL");
            }
        }

        // Validate authentication
        if (EnableContentSync)
        {
            if (string.IsNullOrWhiteSpace(SourceServerUrl))
            {
                errors.Add("Source server URL is required when content sync is enabled");
            }

            if (string.IsNullOrWhiteSpace(SourceServerApiKey))
            {
                errors.Add("API key is required for authentication");
            }
        }

        // Validate numeric ranges
        if (MaxConcurrentDownloads < 1 || MaxConcurrentDownloads > 10)
        {
            errors.Add("Max concurrent downloads must be between 1 and 10");
        }

        if (MaxDownloadSpeed < 0)
        {
            errors.Add("Max download speed cannot be negative");
        }

        if (MinimumFreeDiskSpaceGb < 0 || MinimumFreeDiskSpaceGb > 1000)
        {
            errors.Add("Minimum free disk space must be between 0 and 1000 GB");
        }

        // Validate bandwidth scheduling
        if (EnableBandwidthScheduling)
        {
            if (ScheduledStartHour < 0 || ScheduledStartHour > 23)
            {
                errors.Add("Scheduled start hour must be between 0 and 23");
            }

            if (ScheduledEndHour < 0 || ScheduledEndHour > 24)
            {
                errors.Add("Scheduled end hour must be between 0 and 24");
            }

            if (ScheduledDownloadSpeed < 0)
            {
                errors.Add("Scheduled download speed cannot be negative");
            }
        }

        // Validate library mappings
        foreach (var mapping in LibraryMappings.Where(m => m.IsEnabled))
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceLibraryId))
            {
                errors.Add($"Library mapping '{mapping.SourceLibraryName}' is missing source library ID");
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalRootPath))
            {
                errors.Add($"Library mapping '{mapping.SourceLibraryName}' is missing local root path");
            }
        }

        // Validate recycling bin settings
        if (EnableRecyclingBin)
        {
            if (string.IsNullOrWhiteSpace(RecyclingBinPath))
            {
                errors.Add("Recycling bin path is required when recycling bin is enabled");
            }

            if (RecyclingBinRetentionDays < 1 || RecyclingBinRetentionDays > 365)
            {
                errors.Add("Recycling bin retention must be between 1 and 365 days");
            }
        }

        return errors;
    }

    // IsValid
    // Returns true if the configuration passes validation.
    public bool IsValid()
    {
        return ValidateConfiguration().Count == 0;
    }

    // SanitizeValues
    // Clamps configuration values to valid ranges.
    public void SanitizeValues()
    {
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        MaxDownloadSpeed = Math.Max(0, MaxDownloadSpeed);
        MinimumFreeDiskSpaceGb = Math.Clamp(MinimumFreeDiskSpaceGb, 0, 1000);
        ScheduledStartHour = Math.Clamp(ScheduledStartHour, 0, 23);
        ScheduledEndHour = Math.Clamp(ScheduledEndHour, 0, 24);
        ScheduledDownloadSpeed = Math.Max(0, ScheduledDownloadSpeed);
        RecyclingBinRetentionDays = Math.Clamp(RecyclingBinRetentionDays, 1, 365);

        // Normalize URL
        if (!string.IsNullOrWhiteSpace(SourceServerUrl))
        {
            SourceServerUrl = SourceServerUrl.TrimEnd('/');
        }
    }
}
