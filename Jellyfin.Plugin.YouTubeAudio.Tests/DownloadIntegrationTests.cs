using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// End-to-end download tests against a real YouTube URL. These hit the network and require
/// yt-dlp + ffmpeg/ffprobe, so they are OPT-IN: set the environment variable
/// YTA_RUN_INTEGRATION=1 to run them, otherwise each test is skipped. They mirror the exact
/// download configuration used by <see cref="DownloadService"/> (format mapping, OptionSet) so
/// a real download of every supported format is validated to produce a playable file.
///
/// Validation uses ffprobe — the same probe Jellyfin uses — rather than TagLib, because some
/// containers (notably yt-dlp's M4a) are not directly TagLib-readable and the plugin reads them
/// only after an ffmpeg repair pass. ffprobe answers the real question: "is this good audio?"
///
///   YTA_RUN_INTEGRATION=1 dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class DownloadIntegrationTests
{
    // "Me at the zoo" — the first YouTube video. Short (~19s) and about as permanent as a
    // YouTube URL gets, which makes it a stable fixture.
    private const string TestUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    [SkippableTheory]
    [InlineData(AudioFormat.Opus, "opus")]
    [InlineData(AudioFormat.Mp3, "mp3")]
    [InlineData(AudioFormat.M4a, "aac")]
    public async Task RealDownload_ProducesValidAudio_ForEveryFormat(AudioFormat format, string expectedCodec)
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("YTA_RUN_INTEGRATION"), "1", StringComparison.Ordinal),
            "Set YTA_RUN_INTEGRATION=1 to run network download tests.");

        var ytdlp = Which("yt-dlp");
        var ffmpeg = FindBinary("ffmpeg");
        var ffprobe = FindBinary("ffprobe");
        Skip.If(ytdlp is null, "yt-dlp not found on PATH.");
        Skip.If(ffmpeg is null, "ffmpeg not found.");
        Skip.If(ffprobe is null, "ffprobe not found.");

        var dir = Path.Combine(Path.GetTempPath(), $"yta-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const string name = "track";
            // Same configuration the plugin uses in ProcessQueueAsync.
            var options = new OptionSet
            {
                WriteInfoJson = true,
                EmbedThumbnail = false,
                Output = Path.Combine(dir, name + ".%(ext)s")
            };

            var ytdl = new YoutubeDL { YoutubeDLPath = ytdlp!, FFmpegPath = ffmpeg!, OutputFolder = dir };
            var result = await ytdl.RunAudioDownload(
                TestUrl,
                DownloadService.MapToAudioConversionFormat(format),
                overrideOptions: options).ConfigureAwait(false);

            Assert.True(
                result.Success,
                $"Download failed for {format}: {string.Join("; ", result.ErrorOutput ?? Array.Empty<string>())}");

            // 1. The audio file exists with the extension the plugin expects for this format.
            var expectedExt = DownloadService.GetFileExtension(format);
            var audio = Path.Combine(dir, name + expectedExt);
            Assert.True(File.Exists(audio), $"Expected {audio} to exist for {format}.");
            Assert.True(new FileInfo(audio).Length > 0, "Audio file is empty.");

            // 2. ffprobe (Jellyfin's own validator) sees a real audio stream of the right codec
            //    with a positive duration.
            var (codec, duration) = Probe(ffprobe!, audio);
            Assert.Equal(expectedCodec, codec);
            Assert.True(duration > 0, $"Expected positive duration for {format}, got {duration}.");

            // 3. The info.json sidecar is written (and is what import later cleans up).
            Assert.True(File.Exists(Path.Combine(dir, name + ".info.json")), "info.json was not written.");

            // 4. M4a-specific: yt-dlp's AAC output is not directly TagLib-readable, but the
            //    plugin's one-time "-c copy" remux makes it healthy. Lock that contract in.
            if (format == AudioFormat.M4a)
            {
                Assert.Throws<TagLib.CorruptFileException>(() => TagLib.File.Create(audio));

                var remuxed = Path.Combine(dir, "remuxed.m4a");
                Remux(ffmpeg!, audio, remuxed);
                using var tag = TagLib.File.Create(remuxed);
                Assert.True(tag.Properties.Duration > TimeSpan.Zero, "Remuxed M4a has no duration.");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Returns (audio codec name, duration in seconds) from the first audio stream.
    private static (string Codec, double Duration) Probe(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_name:format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(file);

        using var proc = Process.Start(psi)!;
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var codec = root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0
            ? streams[0].GetProperty("codec_name").GetString() ?? string.Empty
            : string.Empty;
        var duration = root.TryGetProperty("format", out var fmt)
            && fmt.TryGetProperty("duration", out var dur)
            && double.TryParse(dur.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : 0;
        return (codec, duration);
    }

    // Stream-copy remux, mirroring the plugin's TryRepairWithFfmpeg ("-c copy").
    private static void Remux(string ffmpeg, string src, string dst)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-y", "-loglevel", "error", "-i", src, "-c", "copy", dst })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }

    private static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindBinary(string exe)
        => Which(exe)
            ?? new[] { $"/Applications/Jellyfin.app/Contents/MacOS/{exe}", $"/usr/lib/jellyfin-ffmpeg/{exe}" }
                .FirstOrDefault(File.Exists);
}
