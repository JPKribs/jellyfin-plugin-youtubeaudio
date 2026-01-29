namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Result of a connection test containing server info.
/// </summary>
public class ConnectionTestResult
{
    public bool Success { get; set; }

    public string? ServerName { get; set; }

    public string? ServerId { get; set; }

    public string? ErrorMessage { get; set; }
}
