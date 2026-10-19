using System;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Thrown when a submitted playlist has more videos than the submitter may queue at once.
/// </summary>
public sealed class PlaylistTooLargeException : ArgumentException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistTooLargeException"/> class.
    /// </summary>
    public PlaylistTooLargeException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistTooLargeException"/> class.
    /// </summary>
    /// <param name="message">The message shown to the submitter.</param>
    public PlaylistTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistTooLargeException"/> class.
    /// </summary>
    /// <param name="message">The message shown to the submitter.</param>
    /// <param name="innerException">The underlying error.</param>
    public PlaylistTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
