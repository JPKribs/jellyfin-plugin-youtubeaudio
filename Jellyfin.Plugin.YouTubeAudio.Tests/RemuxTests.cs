using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for the ffmpeg stream-copy remux, run against stand-in ffmpeg scripts and, when it is
/// installed, the real ffmpeg.
/// </summary>
public sealed class RemuxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yta remux {Guid.NewGuid():N}");

    public RemuxTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [SkippableFact]
    public async Task ChattyFfmpeg_DoesNotStallOnAFullPipe()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as ffmpeg.");

        // Far more output than a pipe buffer holds, on both streams, before ffmpeg can finish.
        var ffmpeg = Script("head -c 2000000 /dev/zero | tr '\\0' 'x' >&2\nhead -c 2000000 /dev/zero | tr '\\0' 'y'\ncp \"$in\" \"$out\"");
        var file = Audio("song.m4a", "original");

        var watch = Stopwatch.StartNew();
        var ok = await DownloadService.RemuxAsync(ffmpeg, file, TimeSpan.FromSeconds(20), NullLogger.Instance, CancellationToken.None);

        Assert.True(ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.Equal([file], Directory.GetFiles(_dir, "*.m4a"));
    }

    [SkippableFact]
    public async Task ArgumentsArePassedOneByOne_WithQuietOutput()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as ffmpeg.");
        var argsFile = Path.Combine(_dir, "args.txt");
        var ffmpeg = Script($"for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '{argsFile}'\ncp \"$in\" \"$out\"");
        var file = Audio("Song \"with\" quotes.m4a", "x");

        Assert.True(await DownloadService.RemuxAsync(ffmpeg, file, TimeSpan.FromSeconds(20), NullLogger.Instance, CancellationToken.None));

        var args = await File.ReadAllLinesAsync(argsFile);
        Assert.Equal(["-nostdin", "-nostats", "-loglevel", "error", "-y", "-i", file, "-c", "copy", file + ".remux.m4a"], args);
    }

    [SkippableFact]
    public async Task HangingFfmpeg_IsKilledAtTheTimeout_AndItsTempFileRemoved()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as ffmpeg.");
        var pidFile = Path.Combine(_dir, "pid");
        var ffmpeg = Script($"printf partial > \"$out\"\necho $$ > '{pidFile}'\nexec sleep 60");
        var file = Audio("song.m4a", "original");

        var watch = Stopwatch.StartNew();
        var ok = await DownloadService.RemuxAsync(ffmpeg, file, TimeSpan.FromSeconds(1), NullLogger.Instance, CancellationToken.None);

        Assert.False(ok);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(file + ".remux.m4a"));
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WaitForExit(pid);
    }

    [SkippableFact]
    public async Task Cancellation_KillsFfmpegAndThrows()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as ffmpeg.");
        var ffmpeg = Script("printf partial > \"$out\"\nexec sleep 60");
        var file = Audio("song.m4a", "original");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadService.RemuxAsync(ffmpeg, file, TimeSpan.FromSeconds(30), NullLogger.Instance, cts.Token));

        Assert.False(File.Exists(file + ".remux.m4a"));
        Assert.Equal("original", await File.ReadAllTextAsync(file));
    }

    [SkippableFact]
    public async Task FailingFfmpeg_KeepsTheOriginal()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as ffmpeg.");
        var ffmpeg = Script("printf partial > \"$out\"\necho 'Invalid data found when processing input' >&2\nexit 1");
        var file = Audio("song.m4a", "original");

        Assert.False(await DownloadService.RemuxAsync(ffmpeg, file, TimeSpan.FromSeconds(20), NullLogger.Instance, CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(file + ".remux.m4a"));
    }

    [SkippableFact]
    public async Task RealFfmpeg_RemuxesAnOpusFile()
    {
        var ffmpeg = new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg" }.FirstOrDefaultExisting();
        Skip.If(ffmpeg == null, "ffmpeg is not installed.");
        var file = Path.Combine(_dir, "song.opus");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "silence.opus"), file);

        Assert.True(await DownloadService.RemuxAsync(ffmpeg!, file, TimeSpan.FromSeconds(30), NullLogger.Instance, CancellationToken.None));

        using var tag = TagLib.File.Create(file);
        Assert.True(tag.Properties.Duration > TimeSpan.Zero);
        Assert.Single(Directory.GetFiles(_dir));
    }

    // Writes a stand-in ffmpeg; $in and $out are its input and output paths.
    private string Script(string body)
    {
        var path = Path.Combine(_dir, "ffmpeg-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(path, string.Join('\n',
            "#!/bin/sh",
            "in=''; prev=''; for a in \"$@\"; do if [ \"$prev\" = '-i' ]; then in=\"$a\"; fi; prev=\"$a\"; out=\"$a\"; done",
            body,
            string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private string Audio(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task WaitForExit(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Process {pid} is still running.");
    }
}

internal static class PathListExtensions
{
    public static string? FirstOrDefaultExisting(this string[] paths) => Array.Find(paths, File.Exists);
}
