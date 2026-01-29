namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from URL validation.
/// </summary>
public class ValidateUrlResponse
{
    public bool IsValid { get; set; }

    public string? Message { get; set; }

    public string? NormalizedUrl { get; set; }
}
