using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Attribute that defines the visual appearance of a sync status.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StatusAppearanceAttribute : Attribute
{
    /// <summary>
    /// Gets the display name for the status.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the primary color (hex format, e.g., "#5cb85c").
    /// </summary>
    public string Color { get; }

    /// <summary>
    /// Gets the RGB values for the color (e.g., "92, 184, 92").
    /// </summary>
    public string ColorRgb { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatusAppearanceAttribute"/> class.
    /// </summary>
    /// <param name="displayName">Display name for the status.</param>
    /// <param name="color">Primary color in hex format.</param>
    /// <param name="colorRgb">RGB values as comma-separated string.</param>
    public StatusAppearanceAttribute(string displayName, string color, string colorRgb)
    {
        DisplayName = displayName;
        Color = color;
        ColorRgb = colorRgb;
    }

    /// <summary>
    /// Gets the background color with alpha (rgba format).
    /// </summary>
    /// <param name="alpha">Alpha value (0.0 to 1.0).</param>
    /// <returns>RGBA color string.</returns>
    public string GetBackgroundRgba(double alpha = 0.2) =>
        string.Create(CultureInfo.InvariantCulture, $"rgba({ColorRgb}, {alpha})");

    /// <summary>
    /// Gets the border color with alpha (rgba format).
    /// </summary>
    /// <param name="alpha">Alpha value (0.0 to 1.0).</param>
    /// <returns>RGBA color string.</returns>
    public string GetBorderRgba(double alpha = 0.3) =>
        string.Create(CultureInfo.InvariantCulture, $"rgba({ColorRgb}, {alpha})");
}

/// <summary>
/// Standard status appearances used across all sync modules.
/// </summary>
public enum StatusAppearanceType
{
    /// <summary>Synced - Green.</summary>
    [StatusAppearance("Synced", "#5cb85c", "92, 184, 92")]
    Synced,

    /// <summary>Queued - Orange/Yellow.</summary>
    [StatusAppearance("Queued", "#f0ad4e", "240, 173, 78")]
    Queued,

    /// <summary>Errored - Red.</summary>
    [StatusAppearance("Errored", "#d9534f", "217, 83, 79")]
    Errored,

    /// <summary>Ignored - Gray.</summary>
    [StatusAppearance("Ignored", "#999999", "153, 153, 153")]
    Ignored,

    /// <summary>Pending - Purple (generic).</summary>
    [StatusAppearance("Pending", "#9b59b6", "155, 89, 182")]
    Pending,

    /// <summary>Pending Download - Purple.</summary>
    [StatusAppearance("Pending Download", "#9b59b6", "155, 89, 182")]
    PendingDownload,

    /// <summary>Pending Replacement - Blue.</summary>
    [StatusAppearance("Pending Replacement", "#3498db", "52, 152, 219")]
    PendingReplacement,

    /// <summary>Pending Deletion - Red.</summary>
    [StatusAppearance("Pending Deletion", "#d9534f", "217, 83, 79")]
    PendingDeletion,

    /// <summary>Deleting - Brighter Red.</summary>
    [StatusAppearance("Deleting", "#e74c3c", "231, 76, 60")]
    Deleting
}

/// <summary>
/// Helper class for retrieving status appearance information.
/// </summary>
public static class StatusAppearanceHelper
{
    private static readonly Dictionary<StatusAppearanceType, StatusAppearanceAttribute> _cache = new();

    static StatusAppearanceHelper()
    {
        foreach (StatusAppearanceType status in Enum.GetValues<StatusAppearanceType>())
        {
            var field = typeof(StatusAppearanceType).GetField(status.ToString());
            var attr = field?.GetCustomAttribute<StatusAppearanceAttribute>();
            if (attr != null)
            {
                _cache[status] = attr;
            }
        }
    }

    /// <summary>
    /// Gets the appearance attribute for a status type.
    /// </summary>
    /// <param name="status">The status type.</param>
    /// <returns>The appearance attribute, or null if not found.</returns>
    public static StatusAppearanceAttribute? GetAppearance(StatusAppearanceType status)
    {
        return _cache.TryGetValue(status, out var attr) ? attr : null;
    }

    /// <summary>
    /// Gets all status appearances.
    /// </summary>
    /// <returns>Dictionary of status types to their appearances.</returns>
    public static IReadOnlyDictionary<StatusAppearanceType, StatusAppearanceAttribute> GetAllAppearances()
    {
        return _cache;
    }

    /// <summary>
    /// Generates CSS variables for all status appearances.
    /// </summary>
    /// <returns>CSS variable declarations.</returns>
    public static string GenerateCssVariables()
    {
        var lines = new List<string>();
        foreach (var (status, attr) in _cache)
        {
            // Use a cleaner approach for CSS variable names
            var cssName = status switch
            {
                StatusAppearanceType.Synced => "synced",
                StatusAppearanceType.Queued => "queued",
                StatusAppearanceType.Errored => "errored",
                StatusAppearanceType.Ignored => "ignored",
                StatusAppearanceType.Pending => "pending",
                StatusAppearanceType.PendingDownload => "pending-download",
                StatusAppearanceType.PendingReplacement => "pending-replacement",
                StatusAppearanceType.PendingDeletion => "pending-deletion",
                StatusAppearanceType.Deleting => "deleting",
                _ => status.ToString().ToLowerInvariant()
            };

            lines.Add($"--status-{cssName}-bg: {attr.GetBackgroundRgba()};");
            lines.Add($"--status-{cssName}-color: {attr.Color};");
            lines.Add($"--status-{cssName}-border: {attr.GetBorderRgba()};");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Gets status metadata as a dictionary for JSON serialization.
    /// </summary>
    /// <returns>Dictionary of status metadata.</returns>
    public static Dictionary<string, StatusMetadata> GetStatusMetadata()
    {
        var result = new Dictionary<string, StatusMetadata>();
        foreach (var (status, attr) in _cache)
        {
            var cssClass = status switch
            {
                StatusAppearanceType.PendingDownload => "Pending-Download",
                StatusAppearanceType.PendingReplacement => "Pending-Replacement",
                StatusAppearanceType.PendingDeletion => "Pending-Deletion",
                _ => status.ToString()
            };

            result[status.ToString()] = new StatusMetadata
            {
                DisplayName = attr.DisplayName,
                CssClass = cssClass,
                Color = attr.Color,
                BackgroundColor = attr.GetBackgroundRgba(),
                BorderColor = attr.GetBorderRgba()
            };
        }

        return result;
    }
}

/// <summary>
/// Status metadata for API responses.
/// </summary>
public class StatusMetadata
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the CSS class name.
    /// </summary>
    public string CssClass { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary color (hex).
    /// </summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the background color (rgba).
    /// </summary>
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the border color (rgba).
    /// </summary>
    public string BorderColor { get; set; } = string.Empty;
}
