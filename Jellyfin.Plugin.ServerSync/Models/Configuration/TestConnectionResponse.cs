namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from a connection test.
/// </summary>
public class TestConnectionResponse
{
    public bool Success { get; set; }

    public string? ServerName { get; set; }

    public string? ServerId { get; set; }

    public string? Message { get; set; }
}
