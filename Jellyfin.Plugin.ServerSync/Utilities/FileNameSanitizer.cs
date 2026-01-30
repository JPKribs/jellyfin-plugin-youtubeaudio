using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility for sanitizing file names to remove illegal characters.
/// </summary>
public static partial class FileNameSanitizer
{
    /// <summary>
    /// Characters that are invalid in file names across Windows, macOS, and Linux.
    /// </summary>
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Additional characters that may cause issues.
    /// </summary>
    private static readonly char[] AdditionalInvalidChars = { ':', '*', '?', '"', '<', '>', '|', '\0' };

    /// <summary>
    /// Maximum file name length (conservative limit for cross-platform compatibility).
    /// </summary>
    private const int MaxFileNameLength = 200;

    /// <summary>
    /// Sanitizes a file name by removing or replacing illegal characters.
    /// </summary>
    /// <param name="fileName">The file name to sanitize.</param>
    /// <param name="replacement">Character to replace illegal characters with (default: underscore).</param>
    /// <returns>Sanitized file name.</returns>
    public static string Sanitize(string? fileName, char replacement = '_')
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"unnamed_{Guid.NewGuid():N}";
        }

        var allInvalidChars = InvalidChars
            .Concat(AdditionalInvalidChars)
            .Distinct()
            .ToHashSet();

        var sb = new StringBuilder(fileName.Length);

        foreach (var c in fileName)
        {
            if (allInvalidChars.Contains(c) || char.IsControl(c))
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(c);
            }
        }

        var sanitized = sb.ToString();

        // Remove consecutive replacement characters
        sanitized = CollapseReplacements(sanitized, replacement);

        // Trim leading/trailing spaces and dots (problematic on Windows)
        sanitized = sanitized.Trim(' ', '.');

        // Handle reserved Windows file names
        sanitized = HandleReservedNames(sanitized);

        // Truncate if too long (preserve extension)
        if (sanitized.Length > MaxFileNameLength)
        {
            sanitized = TruncateWithExtension(sanitized, MaxFileNameLength);
        }

        // Final fallback if empty
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return $"unnamed_{Guid.NewGuid():N}";
        }

        return sanitized;
    }

    /// <summary>
    /// Sanitizes a file name for use as a temporary file.
    /// Combines an ID prefix with the sanitized original name.
    /// </summary>
    /// <param name="sourceItemId">Source item ID to use as prefix.</param>
    /// <param name="originalPath">Original file path to extract name from.</param>
    /// <returns>Sanitized temp file name.</returns>
    public static string SanitizeTempFileName(string sourceItemId, string? originalPath)
    {
        var originalName = !string.IsNullOrEmpty(originalPath)
            ? Path.GetFileName(originalPath)
            : null;

        var sanitizedName = Sanitize(originalName);
        var sanitizedId = Sanitize(sourceItemId);

        return $"{sanitizedId}_{sanitizedName}";
    }

    /// <summary>
    /// Collapses consecutive replacement characters into a single one.
    /// </summary>
    private static string CollapseReplacements(string input, char replacement)
    {
        var pattern = $"{Regex.Escape(replacement.ToString())}+";
        return Regex.Replace(input, pattern, replacement.ToString());
    }

    /// <summary>
    /// Handles reserved Windows file names by appending an underscore.
    /// </summary>
    private static string HandleReservedNames(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        var extension = Path.GetExtension(fileName);

        // Reserved names in Windows
        string[] reservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        if (reservedNames.Contains(nameWithoutExtension))
        {
            return $"{Path.GetFileNameWithoutExtension(fileName)}_{extension}";
        }

        return fileName;
    }

    /// <summary>
    /// Truncates a file name while preserving the extension.
    /// </summary>
    private static string TruncateWithExtension(string fileName, int maxLength)
    {
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var maxNameLength = maxLength - extension.Length;
        if (maxNameLength < 1)
        {
            // Extension too long, just truncate everything
            return fileName[..maxLength];
        }

        if (nameWithoutExtension.Length > maxNameLength)
        {
            nameWithoutExtension = nameWithoutExtension[..maxNameLength];
        }

        return nameWithoutExtension + extension;
    }
}
