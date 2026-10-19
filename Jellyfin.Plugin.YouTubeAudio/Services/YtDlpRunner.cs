using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// What a yt-dlp run returned.
/// </summary>
/// <param name="Success">Whether yt-dlp exited with code 0.</param>
/// <param name="ErrorOutput">The lines yt-dlp wrote to stderr.</param>
internal sealed record YtDlpRunResult(bool Success, string[] ErrorOutput);

/// <summary>
/// Runs yt-dlp for downloads in a process the plugin owns, so cancelling can stop it. YoutubeDLSharp
/// keeps its process private and stops it by running <c>pgrep</c> and <c>kill</c>, which the
/// official Jellyfin image does not have, so a "cancelled" download used to keep running and
/// writing to the cache. Here the whole process tree (yt-dlp and the ffmpeg it starts) is killed
/// with .NET's own <see cref="Process.Kill(bool)"/>, which needs no outside tools.
/// </summary>
internal static class YtDlpRunner
{
    /// <summary>How long to wait for a killed yt-dlp to exit before giving up on it.</summary>
    internal static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs yt-dlp with the given command line and waits for it. When
    /// <paramref name="cancellationToken"/> is cancelled, the process tree is killed at once and
    /// the call waits (up to <see cref="KillWait"/>) for it to exit before throwing, so nothing
    /// is still writing when the caller cleans up.
    /// </summary>
    /// <param name="executable">The yt-dlp binary.</param>
    /// <param name="arguments">The command line, as YoutubeDLSharp renders it.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>Whether yt-dlp succeeded, and its error output.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    internal static async Task<YtDlpRunResult> RunAsync(string executable, string arguments, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var errors = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (errors)
                {
                    errors.Add(e.Data);
                }
            }
        };
        process.OutputDataReceived += (_, _) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Cancel() runs this callback straight away, so the tree is dead before a cancel, reset
        // or shutdown goes on to clean the cache.
        using (cancellationToken.Register(() => KillTree(process, logger)))
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process, logger);
                using var wait = new CancellationTokenSource(KillWait);
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("yt-dlp did not exit within {Seconds} seconds of being stopped", KillWait.TotalSeconds);
                }

                throw;
            }
        }

        lock (errors)
        {
            return new YtDlpRunResult(process.ExitCode == 0, errors.ToArray());
        }
    }

    /// <summary>
    /// Kills a process and everything it started. Never throws.
    /// </summary>
    internal static void KillTree(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            logger.LogDebug(ex, "Could not stop yt-dlp; it may have exited already");
        }
    }
}
