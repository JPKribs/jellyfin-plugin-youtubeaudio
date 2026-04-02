namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Supported audio output formats.
/// </summary>
public enum AudioFormat
{
    /// <summary>Opus audio (.opus) - default, best quality/size ratio.</summary>
    Opus = 0,

    /// <summary>MP3 audio (.mp3) - widest compatibility.</summary>
    Mp3 = 1,

    /// <summary>AAC in M4A container (.m4a) - Apple ecosystem.</summary>
    M4a = 2
}
