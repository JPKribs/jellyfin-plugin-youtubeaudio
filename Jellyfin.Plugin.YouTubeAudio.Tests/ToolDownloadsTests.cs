using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for downloading yt-dlp and Deno: each file must match the checksum its release
/// publishes before it is put in place and made executable. GitHub is replaced by canned
/// responses.
/// </summary>
public sealed class ToolDownloadsTests : IDisposable
{
    private const string Tag = "2026.10.01";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yta-dl-{Guid.NewGuid():N}");

    public ToolDownloadsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // MARK: yt-dlp

    [Fact]
    public async Task YtDlp_MatchingChecksum_IsInstalledFromTheSameRelease()
    {
        var binary = Encoding.UTF8.GetBytes("#!/bin/sh\necho 2026.10.01\n");
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/SHA2-256SUMS"] = Text(Sums(("yt-dlp_linux.zip", Sha(binary.AsSpan(1).ToArray())), ("yt-dlp_linux", Sha(binary)))),
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/yt-dlp_linux"] = Bytes(binary)
        };
        using var client = new HttpClient(handler);
        var dest = Path.Combine(_dir, "yt-dlp_linux");

        await ToolDownloads.DownloadYtDlpAsync(client, dest, "yt-dlp_linux", Tag, CancellationToken.None);

        Assert.Equal(binary, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".download"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(dest).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task YtDlp_ChecksumMismatch_IsDiscardedAndTheOldCopyKept()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/SHA2-256SUMS"] = Text(Sums(("yt-dlp_linux", Sha(Encoding.UTF8.GetBytes("the real one"))))),
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/yt-dlp_linux"] = Bytes(Encoding.UTF8.GetBytes("tampered"))
        };
        using var client = new HttpClient(handler);
        var dest = Path.Combine(_dir, "yt-dlp_linux");
        await File.WriteAllTextAsync(dest, "old copy");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ToolDownloads.DownloadYtDlpAsync(client, dest, "yt-dlp_linux", Tag, CancellationToken.None));

        Assert.Contains("Checksum mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Equal("old copy", await File.ReadAllTextAsync(dest));
        Assert.Equal([dest], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task YtDlp_NotInTheChecksumList_IsNeverDownloaded()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/SHA2-256SUMS"] = Text(Sums(("yt-dlp_macos", new string('a', 64))))
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolDownloads.DownloadYtDlpAsync(client, Path.Combine(_dir, "yt-dlp_linux"), "yt-dlp_linux", Tag, CancellationToken.None));

        Assert.DoesNotContain(handler.Requested, url => url.EndsWith("/yt-dlp_linux", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task YtDlp_WithoutATag_UsesTheLatestLinks()
    {
        var binary = Encoding.UTF8.GetBytes("binary");
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.YtDlpReleases}/latest/download/SHA2-256SUMS"] = Text(Sums(("yt-dlp_macos", Sha(binary)))),
            [$"{ToolDownloads.YtDlpReleases}/latest/download/yt-dlp_macos"] = Bytes(binary)
        };
        using var client = new HttpClient(handler);

        await ToolDownloads.DownloadYtDlpAsync(client, Path.Combine(_dir, "yt-dlp_macos"), "yt-dlp_macos", null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dir, "yt-dlp_macos")));
    }

    [Fact]
    public async Task YtDlp_ServerError_LeavesNothingBehind()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/SHA2-256SUMS"] = Text(Sums(("yt-dlp_linux", new string('a', 64)))),
            [$"{ToolDownloads.YtDlpReleases}/download/{Tag}/yt-dlp_linux"] = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => ToolDownloads.DownloadYtDlpAsync(client, Path.Combine(_dir, "yt-dlp_linux"), "yt-dlp_linux", Tag, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    // MARK: Deno

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deno_MatchingChecksum_IsExtracted(bool powerShellFormat)
    {
        var archive = DenoZip("deno", "deno binary");
        var hash = Sha(archive);
        var checksum = powerShellFormat
            ? $"\r\nAlgorithm : SHA256\r\nHash      : {hash.ToUpperInvariant()}\r\nPath      : C:\\a\\deno\\deno-x86_64-unknown-linux-gnu.zip\r\n"
            : $"{hash}  deno-x86_64-unknown-linux-gnu.zip\n";
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.DenoReleases}/download/v2.9.7/deno-x86_64-unknown-linux-gnu.zip.sha256sum"] = Text(checksum),
            [$"{ToolDownloads.DenoReleases}/download/v2.9.7/deno-x86_64-unknown-linux-gnu.zip"] = Bytes(archive)
        };
        using var client = new HttpClient(handler);
        var dest = Path.Combine(_dir, "deno");

        await ToolDownloads.DownloadDenoAsync(client, dest, "x86_64-unknown-linux-gnu", "v2.9.7", CancellationToken.None);

        Assert.Equal("deno binary", await File.ReadAllTextAsync(dest));
        Assert.Equal([dest], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Deno_ChecksumMismatch_IsDiscarded()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.DenoReleases}/download/v2.9.7/deno-x86_64-unknown-linux-gnu.zip.sha256sum"] = Text(Sha(DenoZip("deno", "real")) + "  deno-x86_64-unknown-linux-gnu.zip"),
            [$"{ToolDownloads.DenoReleases}/download/v2.9.7/deno-x86_64-unknown-linux-gnu.zip"] = Bytes(DenoZip("deno", "tampered"))
        };
        using var client = new HttpClient(handler);
        var dest = Path.Combine(_dir, "deno");

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolDownloads.DownloadDenoAsync(client, dest, "x86_64-unknown-linux-gnu", "v2.9.7", CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Deno_WithoutAChecksum_IsNeverDownloaded()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.DenoReleases}/download/v2.9.7/deno-x86_64-unknown-linux-gnu.zip.sha256sum"] = Text("<html>Not Found</html>")
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => ToolDownloads.DownloadDenoAsync(client, Path.Combine(_dir, "deno"), "x86_64-unknown-linux-gnu", "v2.9.7", CancellationToken.None));

        Assert.Single(handler.Requested);
    }

    // MARK: Parsing

    [Fact]
    public void FindChecksum_MatchesTheExactFileName()
    {
        var sums = Sums(("yt-dlp_linux.zip", new string('1', 64)), ("yt-dlp_linux", new string('2', 64)), ("yt-dlp_linux_aarch64", new string('3', 64)));

        Assert.Equal(new string('2', 64), ToolDownloads.FindChecksum(sums, "yt-dlp_linux"));
        Assert.Equal(new string('3', 64), ToolDownloads.FindChecksum(sums, "yt-dlp_linux_aarch64"));
        Assert.Equal(new string('a', 64), ToolDownloads.FindChecksum(new string('A', 64) + " *yt-dlp.exe\r\n", "yt-dlp.exe"));
        Assert.Null(ToolDownloads.FindChecksum(sums, "yt-dlp_macos"));
        Assert.Null(ToolDownloads.FindChecksum("abc  yt-dlp_linux", "yt-dlp_linux"));
    }

    [Theory]
    [InlineData("nothing here")]
    [InlineData("")]
    public void ParseChecksum_WithoutAHash_ReturnsNull(string text)
        => Assert.Null(ToolDownloads.ParseChecksum(text));

    [Fact]
    public void ParseChecksum_IgnoresLongerHexRuns()
        => Assert.Null(ToolDownloads.ParseChecksum(new string('a', 128)));

    [Fact]
    public async Task GetLatestTag_ReadsTheRedirect_AndRejectsOddTags()
    {
        var handler = new FakeGitHub
        {
            [$"{ToolDownloads.DenoReleases}/latest"] = Redirect($"{ToolDownloads.DenoReleases}/tag/v2.9.7"),
            [$"{ToolDownloads.YtDlpReleases}/latest"] = Redirect($"{ToolDownloads.YtDlpReleases}/tag/..%2F..%2Fevil")
        };
        using var client = new HttpClient(handler);

        Assert.Equal("v2.9.7", await ToolDownloads.GetLatestTagAsync(client, ToolDownloads.DenoReleases, CancellationToken.None));
        Assert.Null(await ToolDownloads.GetLatestTagAsync(client, ToolDownloads.YtDlpReleases, CancellationToken.None));
    }

    [Theory]
    [InlineData("deno 2.9.7 (stable, release, x86_64-unknown-linux-gnu) | v8 14.0 | typescript 5.9", "v2.9.7")]
    [InlineData("deno 2.1.4", "v2.1.4")]
    [InlineData("command not found", null)]
    [InlineData("", null)]
    public void ParseDenoVersion_ReadsTheReleaseTag(string output, string? expected)
        => Assert.Equal(expected, DownloadService.ParseDenoVersion(output));

    // MARK: Helpers

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Sums(params (string Name, string Hash)[] entries)
        => string.Concat(Array.ConvertAll(entries, e => $"{e.Hash}  {e.Name}\n"));

    private static byte[] DenoZip(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static Func<HttpResponseMessage> Text(string text) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static Func<HttpResponseMessage> Bytes(byte[] data) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static Func<HttpResponseMessage> Redirect(string location) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    };

    /// <summary>Answers requests from a URL map; anything else is a 404.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = new();

        public Func<HttpResponseMessage> this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(_responses.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
