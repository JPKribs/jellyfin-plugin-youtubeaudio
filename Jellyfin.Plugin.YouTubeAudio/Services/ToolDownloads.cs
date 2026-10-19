using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Downloads the yt-dlp and Deno binaries from their official GitHub releases. Every file is
/// checked against the SHA-256 checksum its release publishes before it is made executable;
/// a file that does not match is deleted and never run.
/// </summary>
internal static partial class ToolDownloads
{
    /// <summary>The yt-dlp releases page.</summary>
    internal const string YtDlpReleases = "https://github.com/yt-dlp/yt-dlp/releases";

    /// <summary>The Deno releases page.</summary>
    internal const string DenoReleases = "https://github.com/denoland/deno/releases";

    /// <summary>The checksum list each yt-dlp release publishes.</summary>
    internal const string YtDlpChecksumAsset = "SHA2-256SUMS";

    /// <summary>
    /// Reads the newest release tag (e.g. "2026.08.19" or "v2.9.7") from GitHub's latest-release
    /// redirect, without downloading anything. The client must not follow redirects.
    /// </summary>
    internal static async Task<string?> GetLatestTagAsync(HttpClient noRedirectClient, string releasesUrl, CancellationToken cancellationToken)
    {
        using var response = await noRedirectClient.GetAsync(new Uri(releasesUrl + "/latest"), cancellationToken).ConfigureAwait(false);
        var location = response.Headers.Location?.ToString();
        var marker = location?.LastIndexOf("/tag/", StringComparison.Ordinal) ?? -1;
        if (marker < 0)
        {
            return null;
        }

        var tag = location![(marker + "/tag/".Length)..].Trim();
        return IsSafeTag(tag) ? tag : null;
    }

    /// <summary>
    /// Gets the address of a release file: from the given tag, so the binary and its checksum
    /// come from the same release, or from the latest release when the tag is unknown.
    /// </summary>
    internal static Uri ReleaseAsset(string releasesUrl, string? tag, string asset)
        => new(string.IsNullOrEmpty(tag)
            ? $"{releasesUrl}/latest/download/{asset}"
            : $"{releasesUrl}/download/{tag}/{asset}");

    /// <summary>
    /// Downloads the standalone yt-dlp binary to <paramref name="destPath"/>, checked against the
    /// release's SHA2-256SUMS. Written to a temporary file and moved into place, so a copy that is
    /// running is never half-overwritten.
    /// </summary>
    /// <exception cref="InvalidDataException">The release has no checksum for the binary, or the download does not match it.</exception>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are inside the plugin directory.")]
    internal static async Task DownloadYtDlpAsync(HttpClient client, string destPath, string binaryName, string? tag, CancellationToken cancellationToken)
    {
        var sums = await client.GetStringAsync(ReleaseAsset(YtDlpReleases, tag, YtDlpChecksumAsset), cancellationToken).ConfigureAwait(false);
        var expected = FindChecksum(sums, binaryName)
            ?? throw new InvalidDataException($"The yt-dlp release lists no checksum for {binaryName}; the download was not used.");

        var tempPath = destPath + ".download";
        await DownloadVerifiedAsync(client, ReleaseAsset(YtDlpReleases, tag, binaryName), tempPath, expected, cancellationToken).ConfigureAwait(false);
        MakeExecutable(tempPath);
        File.Move(tempPath, destPath, overwrite: true);
    }

    /// <summary>
    /// Downloads the Deno release archive for <paramref name="target"/>, checks it against the
    /// release's .sha256sum file, and extracts the binary to <paramref name="destPath"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The checksum is missing, the archive does not match it, or it holds no Deno binary.</exception>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are inside the plugin directory.")]
    internal static async Task DownloadDenoAsync(HttpClient client, string destPath, string target, string? tag, CancellationToken cancellationToken)
    {
        var asset = $"deno-{target}.zip";
        var checksumText = await client.GetStringAsync(ReleaseAsset(DenoReleases, tag, asset + ".sha256sum"), cancellationToken).ConfigureAwait(false);
        var expected = ParseChecksum(checksumText)
            ?? throw new InvalidDataException($"The Deno release lists no checksum for {asset}; the download was not used.");

        var zipPath = destPath + ".zip.download";
        var tempPath = destPath + ".download";
        try
        {
            await DownloadVerifiedAsync(client, ReleaseAsset(DenoReleases, tag, asset), zipPath, expected, cancellationToken).ConfigureAwait(false);

            var binaryName = Path.GetFileName(destPath);
            var archive = await ZipFile.OpenReadAsync(zipPath, cancellationToken).ConfigureAwait(false);
            await using (archive.ConfigureAwait(false))
            {
                var entry = archive.GetEntry(binaryName) ?? throw new InvalidDataException("The Deno archive has no " + binaryName);
                await entry.ExtractToFileAsync(tempPath, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            MakeExecutable(tempPath);
            File.Move(tempPath, destPath, overwrite: true);
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Finds a file's SHA-256 in a "sha256sum" style list ("hash  name" per line, with an
    /// optional "*" before binary file names), or null when the file is not listed.
    /// </summary>
    internal static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IsSha256(parts[0]) && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.Ordinal))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the SHA-256 from a single-file checksum (either "hash  name" or PowerShell's
    /// "Hash : HASH" listing, both of which Deno publishes), or null when there is none.
    /// </summary>
    internal static string? ParseChecksum(string text)
    {
        var match = Sha256Regex().Match(text ?? string.Empty);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Downloads a file while hashing it, and deletes it unless its SHA-256 matches.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are inside the plugin directory.")]
    private static async Task DownloadVerifiedAsync(HttpClient client, Uri url, string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using (destination.ConfigureAwait(false))
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Checksum mismatch for {Path.GetFileName(url.AbsolutePath)}: expected {expected}, got {actual}. The download was discarded.");
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Marks a file as executable (no-op on Windows).
    /// </summary>
    internal static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next download to overwrite.
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && Sha256Regex().IsMatch(value);

    // A tag lands in a download URL; release tags are versions like "2026.08.19" or "v2.9.7".
    private static bool IsSafeTag(string tag)
        => tag.Length is > 0 and <= 64 && SafeTagRegex().IsMatch(tag);

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]+$")]
    private static partial Regex SafeTagRegex();
}
