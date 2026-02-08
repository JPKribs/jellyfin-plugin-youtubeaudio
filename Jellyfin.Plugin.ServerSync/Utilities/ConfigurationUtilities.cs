using System;
using System.Net;
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

    /// <summary>
    /// Validates a server URL for SSRF protection.
    /// Blocks path traversal sequences, non-HTTP schemes, and link-local/cloud metadata addresses.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>Null if valid, or an error message describing why the URL was rejected.</returns>
    public static string? ValidateServerUrlForSsrf(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "URL cannot be empty";
        }

        if (url.Contains("..", StringComparison.Ordinal) || url.Contains("./", StringComparison.Ordinal))
        {
            return "URL contains invalid path sequences";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Invalid URL format";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Only HTTP and HTTPS URLs are allowed";
        }

        // Block cloud metadata endpoints and link-local addresses
        if (IPAddress.TryParse(uri.Host, out var ipAddress))
        {
            var addrBytes = ipAddress.GetAddressBytes();
            if (ipAddress.IsIPv6LinkLocal ||
                (addrBytes.Length == 4 && addrBytes[0] == 169 && addrBytes[1] == 254))
            {
                return "Link-local addresses are not allowed";
            }
        }

        return null;
    }
}
