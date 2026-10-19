using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using NSubstitute;
using Xunit;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for running the queue against a stand-in yt-dlp script: cancelling, cleaning up after
/// failures, and the cache maintenance actions.
/// </summary>
[Collection("Plugin")]
public sealed class QueueProcessingTests : IDisposable
{
    private readonly string _scripts = Path.Combine(Path.GetTempPath(), $"yta-tools-{Guid.NewGuid():N}");
    private readonly string? _fakeDeno;

    public QueueProcessingTests()
    {
        Directory.CreateDirectory(_scripts);

        // The plugin folder is the test output folder. A Deno there stops the service from
        // downloading the real one.
        var deno = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
        if (!File.Exists(deno))
        {
            File.WriteAllText(deno, "#!/bin/sh\necho deno 0.0.0\n");
            _fakeDeno = deno;
        }
    }

    public void Dispose()
    {
        if (_fakeDeno != null)
        {
            File.Delete(_fakeDeno);
        }

        Directory.Delete(_scripts, recursive: true);
    }

    // MARK: Cancel

    [SkippableFact]
    public async Task CancelDownloads_KillsYtDlp_DeletesPartialFiles_AndRequeuesTheSong()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        var pidFile = Path.Combine(_scripts, "pid");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo $$ > '" + pidFile + "'\nexec sleep 60"));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];
        var waiting = (await fx.Service.QueueUrlAsync("https://youtu.be/def"))[0];

        fx.Service.StartProcessing([item.Id, waiting.Id]);
        await WaitUntil(() => File.Exists(pidFile) && CacheFiles(fx, item.FileName).Length == 2);
        Assert.Equal(QueueStatus.Downloading, fx.Db.GetItemById(item.Id)!.Status);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(fx.Service.CancelDownloads());
        await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Queued);

        Assert.Empty(CacheFiles(fx, item.FileName));
        Assert.Equal(QueueStatus.Queued, fx.Db.GetItemById(waiting.Id)!.Status);
        await WaitUntil(() => !IsRunning(pid));
        Assert.False(fx.Service.CancelDownloads());
    }

    [SkippableFact]
    public async Task CancelDownloads_KillsTheWholeTree_WithoutPgrepOrKill()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        var pidFile = Path.Combine(_scripts, "pid");
        var childFile = Path.Combine(_scripts, "child");

        // yt-dlp starts a child (as it does ffmpeg), and both keep writing the partial file.
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp(string.Join('\n',
            "( while :; do printf x >> \"$part\"; sleep 0.05; done ) &",
            "echo $! > '" + childFile + "'",
            "echo $$ > '" + pidFile + "'",
            "while :; do printf y >> \"$part\"; sleep 0.05; done")));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];

        // The Jellyfin image has neither pgrep nor kill; hide them from this process too.
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _scripts);
        try
        {
            fx.Service.StartProcessing([item.Id]);
            await WaitUntil(() => File.Exists(pidFile) && File.Exists(childFile) && CacheFiles(fx, item.FileName).Length == 2);
            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var child = int.Parse(File.ReadAllText(childFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(IsRunning(child));

            Assert.True(fx.Service.CancelDownloads());
            await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Queued);

            Assert.False(IsRunning(pid));
            Assert.False(IsRunning(child));
            Assert.Empty(CacheFiles(fx, item.FileName));
            await Task.Delay(1000);
            Assert.Empty(CacheFiles(fx, item.FileName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    [SkippableFact]
    public async Task DownloadCommandLine_MatchesYoutubeDLSharp()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        Skip.If(_fakeDeno == null, "A real Deno is in the test output folder.");
        var argsFile = Path.Combine(_scripts, "args");
        var ytdlp = FakeYtDlp("for a in \"$@\"; do printf '%s\\n' \"$a\"; done > '" + argsFile + "'\nexit 1");
        using var fx = new ServiceFixture(c =>
        {
            c.YtDlpPath = ytdlp;
            c.CacheDirectoryOverride = Path.Combine(Path.GetTempPath(), $"yta cache {Guid.NewGuid():N}");
        });
        var ffmpeg = Path.Combine(_scripts, "ffmpeg");
        File.WriteAllText(ffmpeg, string.Empty);
        fx.Encoder.EncoderPath.Returns(ffmpeg);
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        var item = (await fx.Service.QueueUrlAsync("https://WWW.youtube.com/watch?v=abc"))[0];
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var output = Path.Combine(fx.CacheDir, stem + ".%(ext)s");

        try
        {
            fx.Service.StartProcessing([item.Id]);
            await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Error);
            var actual = await File.ReadAllLinesAsync(argsFile);

            // What YoutubeDLSharp's own RunAudioDownload passes for the same download.
            File.Delete(argsFile);
            var reference = new YoutubeDL { YoutubeDLPath = ytdlp, FFmpegPath = ffmpeg, OutputFolder = fx.CacheDir };
            var overrides = new OptionSet { WriteInfoJson = true, EmbedThumbnail = false, NoPlaylist = true, Output = output };
            overrides.AddCustomOption("--js-runtimes", "deno:" + _fakeDeno);
            await reference.RunAudioDownload("https://www.youtube.com/watch?v=abc", AudioConversionFormat.Opus, overrideOptions: overrides);
            var expected = await File.ReadAllLinesAsync(argsFile);

            Assert.Equal(expected, actual);
            Assert.Contains(" ", output, StringComparison.Ordinal);
            int At(string arg) => Array.IndexOf(actual, arg);
            Assert.True(At("-x") >= 0);
            Assert.Equal("opus", actual[At("--audio-format") + 1]);
            Assert.Equal(output, actual[At("-o") + 1]);
            Assert.Equal(ffmpeg, actual[At("--ffmpeg-location") + 1]);
            Assert.Equal("deno:" + _fakeDeno, actual[At("--js-runtimes") + 1]);
            Assert.Equal(["--", "https://www.youtube.com/watch?v=abc"], actual[^2..]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(fx.CacheDir)!, recursive: true);
        }
    }

    [SkippableFact]
    public async Task ResetQueue_KillsARunningDownloadBeforeDeletingItsFiles()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        var pidFile = Path.Combine(_scripts, "pid");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo $$ > '" + pidFile + "'\nwhile :; do printf y >> \"$part\"; sleep 0.05; done"));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];
        fx.Service.StartProcessing([item.Id]);
        await WaitUntil(() => File.Exists(pidFile) && CacheFiles(fx, item.FileName).Length == 2);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        await fx.Service.ResetQueueAsync();

        Assert.False(IsRunning(pid));
        Assert.Empty(fx.Db.GetAllItems());
        await Task.Delay(500);
        Assert.Empty(CacheFiles(fx, item.FileName));
    }

    [SkippableFact]
    public async Task CancelledQueue_DoesNotStopLaterRuns()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo 'ERROR: [youtube] abc: Video unavailable' >&2\nexit 1"));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        Assert.False(fx.Service.CancelDownloads());
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];

        fx.Service.StartProcessing([item.Id]);

        await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Error);
    }

    [SkippableFact]
    public async Task CancelDownloads_DoesNotClaimToCancelAnUpdate()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        var started = Path.Combine(_scripts, "started");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("touch '" + started + "'\nsleep 2\necho updated"));

        var update = fx.Service.UpdateYtDlpAsync();
        await WaitUntil(() => File.Exists(started));

        Assert.False(fx.Service.CancelDownloads());
        Assert.Contains("updated", await update, StringComparison.Ordinal);
    }

    // MARK: Tool updates

    [SkippableFact]
    public async Task DenoThatCannotStart_DoesNotFailTheDownloads()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        Skip.If(_fakeDeno == null, "A real Deno is in the test output folder.");
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.GetUnixFileMode(_fakeDeno!).HasFlag(UnixFileMode.UserExecute));
        }

        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo 'ERROR: [youtube] abc: Video unavailable' >&2\nexit 1"));
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];

        // The 12-hourly update check is due, and runs "deno --version" on a file that cannot start.
        fx.Service.StartProcessing([item.Id]);
        await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Error);

        Assert.Contains("Video unavailable", fx.Db.GetItemById(item.Id)!.ErrorMessage, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task UpdateButton_WithADenoThatCannotStart_StillReports()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        Skip.If(_fakeDeno == null, "A real Deno is in the test output folder.");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo 'yt-dlp is up to date'"));

        var message = await fx.Service.UpdateYtDlpAsync();

        Assert.Contains("yt-dlp is up to date", message, StringComparison.Ordinal);
        Assert.Contains("Deno could not be updated", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "yt-dlp_linux")]
    [InlineData(false, true, "yt-dlp_linux_aarch64")]
    [InlineData(true, false, "yt-dlp_musllinux")]
    [InlineData(true, true, "yt-dlp_musllinux_aarch64")]
    public void GetLinuxBinaryName_PicksTheMuslBuildOnMusl(bool musl, bool arm64, string expected)
        => Assert.Equal(expected, DownloadService.GetLinuxBinaryName(musl, arm64));

    // MARK: Failed downloads

    [SkippableFact]
    public async Task FailedDownload_LeavesNoPartialFilesInTheCache()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("echo 'ERROR: [youtube] abc: Video unavailable' >&2\nexit 1"));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;
        var item = (await fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc"))[0];

        fx.Service.StartProcessing([item.Id]);
        await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Error);

        Assert.Contains("Video unavailable", fx.Db.GetItemById(item.Id)!.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(CacheFiles(fx, item.FileName));
    }

    [SkippableFact]
    public async Task StoredLinkThatIsNotYouTube_IsNeverHandedToYtDlp()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses a shell script as yt-dlp.");
        var ran = Path.Combine(_scripts, "ran");
        using var fx = new ServiceFixture(c => c.YtDlpPath = FakeYtDlp("touch '" + ran + "'\nexit 1"));
        fx.Service.LastToolUpdateCheckUtc = DateTime.UtcNow;

        // A row written before links were normalized.
        var item = new QueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = "https://www.youtube.com/watch?v=abc\" \"https://example.com/x",
            FileName = Guid.NewGuid().ToString("N") + ".opus",
            Status = QueueStatus.Queued,
            CreatedAt = "2026-10-19T00:00:00Z",
            UpdatedAt = "2026-10-19T00:00:00Z"
        };
        fx.Db.AddItem(item);

        fx.Service.StartProcessing([item.Id]);
        await WaitUntil(() => fx.Db.GetItemById(item.Id)!.Status == QueueStatus.Error);

        Assert.False(File.Exists(ran));
    }

    // MARK: Links

    [Fact]
    public async Task QueueUrl_StoresTheNormalizedLink_AndRefusesInjectedArguments()
    {
        using var fx = new ServiceFixture();

        var item = (await fx.Service.QueueUrlAsync("  https://WWW.YouTube.com/watch?v=abc  "))[0];

        Assert.Equal("https://www.youtube.com/watch?v=abc", item.Url);
        await Assert.ThrowsAsync<ArgumentException>(() => fx.Service.QueueUrlAsync("https://www.youtube.com/watch?v=abc\" --exec \"touch /tmp/x"));
        Assert.Throws<ArgumentException>(() => fx.Service.ChangeUrl(item.Id, "https://youtu.be/abc\" \"https://example.com"));
        Assert.Single(fx.Db.GetAllItems());
    }

    // MARK: Cache maintenance

    [Fact]
    public void DeleteCachedFile_RemovesEveryFileOfTheItemOnly()
    {
        using var fx = new ServiceFixture();
        var item = fx.AddDownloaded("Clair de lune", "Lang Lang", "Piano Book", 3);
        var other = fx.AddDownloaded("Für Elise", "Lang Lang", "Piano Book", 2);
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        foreach (var leftover in new[] { ".info.json", ".webm.part", ".f251.webm", ".temp.opus" })
        {
            File.WriteAllText(Path.Combine(fx.CacheDir, stem + leftover), "x");
        }

        fx.Service.DeleteCachedFile(item.Id);

        Assert.Empty(CacheFiles(fx, item.FileName));
        Assert.Null(fx.Db.GetItemById(item.Id));
        Assert.Equal([other.FileName], CacheFiles(fx, other.FileName));
    }

    [Fact]
    public async Task ResetQueue_DeletesTheQueuedItemsFiles()
    {
        using var fx = new ServiceFixture();
        var item = fx.AddDownloaded("Clair de lune", "Lang Lang", "Piano Book", 3);
        File.WriteAllText(Path.Combine(fx.CacheDir, Path.GetFileNameWithoutExtension(item.FileName) + ".info.json"), "{}");
        var unrelated = Path.Combine(fx.CacheDir, "readme.txt");
        File.WriteAllText(unrelated, "x");

        await fx.Service.ResetQueueAsync();

        Assert.Empty(fx.Db.GetAllItems());
        Assert.Empty(CacheFiles(fx, item.FileName));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task ResetCache_DeletesEverythingInTheCache()
    {
        using var fx = new ServiceFixture();
        fx.AddDownloaded("Clair de lune", "Lang Lang", "Piano Book", 3);
        File.WriteAllText(Path.Combine(fx.CacheDir, "readme.txt"), "x");

        await fx.Service.ResetCacheAsync();

        Assert.Empty(fx.Db.GetAllItems());
        Assert.Empty(Directory.GetFiles(fx.CacheDir));
    }

    [Fact]
    public void CleanUpCache_DeletesOrphans_KeepsDownloads_AndRequeuesInterruptedItems()
    {
        using var fx = new ServiceFixture();
        var downloaded = fx.AddDownloaded("Clair de lune", "Lang Lang", "Piano Book", 3);
        var downloadedInfo = Path.Combine(fx.CacheDir, Path.GetFileNameWithoutExtension(downloaded.FileName) + ".info.json");
        File.WriteAllText(downloadedInfo, "{}");
        var interrupted = fx.AddDownloaded("Für Elise", "Lang Lang", "Piano Book", 2);
        fx.Db.UpdateStatus(interrupted.Id, QueueStatus.Downloading);
        var orphan = Path.Combine(fx.CacheDir, Guid.NewGuid().ToString("N") + ".webm.part");
        File.WriteAllText(orphan, "x");
        var foreign = Path.Combine(fx.CacheDir, "notes.txt");
        File.WriteAllText(foreign, "x");

        // Everything above was left by an earlier server run.
        foreach (var file in Directory.GetFiles(fx.CacheDir))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));
        }

        // A cover being cropped right now, by this server run.
        var cropping = Path.Combine(fx.CacheDir, Guid.NewGuid().ToString("N") + ".cover-in");
        File.WriteAllText(cropping, "x");

        var removed = fx.Service.CleanUpCache();

        Assert.Equal(2, removed);
        Assert.True(File.Exists(cropping));
        Assert.True(File.Exists(Path.Combine(fx.CacheDir, downloaded.FileName)));
        Assert.True(File.Exists(downloadedInfo));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(orphan));
        Assert.Empty(CacheFiles(fx, interrupted.FileName));
        Assert.Equal(QueueStatus.Queued, fx.Db.GetItemById(interrupted.Id)!.Status);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.opus", "0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef.webm.part", "0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("notes.txt", null)]
    [InlineData("", null)]
    public void GetCacheStem_OnlyAcceptsPluginMadeNames(string name, string? expected)
        => Assert.Equal(expected, DownloadService.GetCacheStem(name));

    // MARK: Helpers

    /// <summary>
    /// Writes a stand-in yt-dlp that creates the partial files a real download leaves (named
    /// from its -o template), then runs <paramref name="body"/>.
    /// </summary>
    private string FakeYtDlp(string body)
    {
        var path = Path.Combine(_scripts, "yt-dlp-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(path, string.Join('\n',
            "#!/bin/sh",
            "PATH=/bin:/usr/bin; export PATH",
            "out=''; prev=''; part=/dev/null",
            "for a in \"$@\"; do if [ \"$prev\" = '-o' ]; then out=\"$a\"; fi; prev=\"$a\"; done",
            "if [ -n \"$out\" ]; then",
            "  part=\"$(printf '%s' \"$out\" | sed 's/%(ext)s/webm.part/')\"",
            "  printf partial > \"$part\"",
            "  printf '{}' > \"$(printf '%s' \"$out\" | sed 's/%(ext)s/info.json/')\"",
            "fi",
            body,
            string.Empty));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string[] CacheFiles(ServiceFixture fx, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Directory.GetFiles(fx.CacheDir).Select(Path.GetFileName).Where(f => f!.StartsWith(stem, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray()!;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50);
        }
    }
}
