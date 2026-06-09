namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Helpers for <see cref="AudioFormat"/>.
/// </summary>
public static class AudioFormatExtensions
{
    /// <summary>Returns the file extension for this audio format, including the leading dot.</summary>
    /// <param name="format">The audio format.</param>
    /// <returns>The file extension, such as <c>.opus</c>.</returns>
    public static string ToFileExtension(this AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Opus => ".opus",
            AudioFormat.Mp3 => ".mp3",
            AudioFormat.M4a => ".m4a",
            _ => ".opus"
        };
    }
}
