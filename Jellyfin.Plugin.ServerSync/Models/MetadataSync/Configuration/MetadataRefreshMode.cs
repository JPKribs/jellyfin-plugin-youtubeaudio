namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync.Configuration;

/// <summary>
/// Controls how metadata items are processed during sync refresh.
/// </summary>
public enum MetadataRefreshMode
{
    /// <summary>Always pull and compare all metadata categories for every item (default).</summary>
    FullRefresh = 0,

    /// <summary>Skip items whose source ETag has not changed since last sync.</summary>
    SkipUnchanged = 1
}
