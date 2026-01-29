using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from configuration validation.
/// </summary>
public class ConfigurationValidationResponse
{
    public bool IsValid { get; set; }

    public List<string> Errors { get; set; } = new();
}
