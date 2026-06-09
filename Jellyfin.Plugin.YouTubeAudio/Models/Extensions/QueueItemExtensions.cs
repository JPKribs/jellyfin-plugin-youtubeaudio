namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Mapping helpers for <see cref="QueueItem"/>, kept on the model so callers do not duplicate the
/// projection.
/// </summary>
public static class QueueItemExtensions
{
    /// <summary>Projects a queue item to the DTO returned by the API.</summary>
    /// <param name="item">The queue item.</param>
    /// <returns>A DTO copy of the item.</returns>
    public static QueueItemDto ToDto(this QueueItem item)
    {
        return new QueueItemDto
        {
            Id = item.Id,
            Url = item.Url,
            Title = item.Title,
            FileName = item.FileName,
            Status = item.Status.ToString(),
            StatusCode = (int)item.Status,
            ErrorMessage = item.ErrorMessage,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }
}
