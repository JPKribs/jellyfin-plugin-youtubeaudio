namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>
/// Plugin capabilities for the UI.
/// </summary>
public class CapabilitiesResponse
{
    public bool CanDeleteItems { get; set; }

    public bool SupportsCompanionFiles { get; set; }

    public bool SupportsBandwidthScheduling { get; set; }
}
