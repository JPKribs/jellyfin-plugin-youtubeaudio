using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utilities for configuration validation.
/// </summary>
public static class ConfigurationUtilities
{
    /// <summary>
    /// Checks if valid authentication configuration is present.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>True if authentication is properly configured.</returns>
    public static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.SourceServerUrl) &&
               !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }
}
