using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility methods for stream operations.
/// </summary>
public static class StreamUtilities
{
    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies stream with optional speed limiting.
    /// </summary>
    /// <param name="source">Source stream to copy from.</param>
    /// <param name="destination">Destination stream to copy to.</param>
    /// <param name="bytesPerSecond">Maximum bytes per second (0 or negative for unlimited).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task CopyWithSpeedLimitAsync(
        Stream source,
        Stream destination,
        long bytesPerSecond,
        CancellationToken cancellationToken = default)
    {
        if (bytesPerSecond <= 0)
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[DefaultBufferSize];
        var stopwatch = Stopwatch.StartNew();
        long totalBytesRead = 0;

        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;

            var expectedTime = (double)totalBytesRead / bytesPerSecond * 1000;
            var actualTime = stopwatch.ElapsedMilliseconds;

            if (actualTime < expectedTime)
            {
                var delay = (int)(expectedTime - actualTime);
                if (delay > 10)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
