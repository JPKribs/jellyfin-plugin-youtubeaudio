using System;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;
using YoutubeDLSharp.Options;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for the pure helper logic in <see cref="DownloadService"/>.
/// </summary>
public class DownloadServiceTests
{
    // MARK: URL validation

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtube.com/watch?v=abc")]
    [InlineData("https://m.youtube.com/watch?v=abc")]
    [InlineData("https://music.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/watch?v=abc")]
    public void IsValidYouTubeUrl_AcceptsYouTubeHosts(string url)
        => Assert.True(DownloadService.IsValidYouTubeUrl(url));

    [Theory]
    [InlineData("https://vimeo.com/123")]
    [InlineData("https://notyoutube.com/watch?v=abc")]
    [InlineData("https://youtube.com.evil.com/watch?v=abc")]
    [InlineData("not a url")]
    [InlineData("")]
    public void IsValidYouTubeUrl_RejectsOthers(string url)
        => Assert.False(DownloadService.IsValidYouTubeUrl(url));

    [Theory]
    [InlineData("file://youtube.com/watch?v=abc")]
    [InlineData("ftp://www.youtube.com/watch?v=abc")]
    [InlineData("javascript://youtu.be/abc")]
    public void IsValidYouTubeUrl_RejectsNonHttpSchemes(string url)
        => Assert.False(DownloadService.IsValidYouTubeUrl(url));

    // MARK: Format mapping

    [Theory]
    [InlineData(AudioFormat.Opus, ".opus")]
    [InlineData(AudioFormat.Mp3, ".mp3")]
    [InlineData(AudioFormat.M4a, ".m4a")]
    public void ToFileExtension_MapsFormat(AudioFormat format, string expected)
        => Assert.Equal(expected, format.ToFileExtension());

    [Theory]
    [InlineData(AudioFormat.Opus, AudioConversionFormat.Opus)]
    [InlineData(AudioFormat.Mp3, AudioConversionFormat.Mp3)]
    [InlineData(AudioFormat.M4a, AudioConversionFormat.Aac)]
    public void MapToAudioConversionFormat_MapsFormat(AudioFormat format, AudioConversionFormat expected)
        => Assert.Equal(expected, DownloadService.MapToAudioConversionFormat(format));

    // MARK: Title cleaning

    [Theory]
    [InlineData("Lang Lang - Clair de lune", "Lang Lang", "Clair de lune")]
    [InlineData("Nocturne (Official Video)", null, "Nocturne")]
    [InlineData("Nocturne [Official Audio]", null, "Nocturne")]
    [InlineData("Yo-Yo Ma - Prélude (Official Music Video)", "Yo-Yo Ma", "Prélude")]
    [InlineData("Plain Title", null, "Plain Title")]
    public void CleanYouTubeTitle_StripsArtistPrefixAndSuffixes(string title, string? artist, string expected)
        => Assert.Equal(expected, DownloadService.CleanYouTubeTitle(title, artist));

    [Fact]
    public void CleanYouTubeTitle_EmptyInput_ReturnsEmpty()
        => Assert.Equal(string.Empty, DownloadService.CleanYouTubeTitle("   ", null));

    // MARK: Preview

    [Theory]
    [InlineData("a.opus", "audio/ogg")]
    [InlineData("a.MP3", "audio/mpeg")]
    [InlineData("a.m4a", "audio/mp4")]
    [InlineData("a.bin", "application/octet-stream")]
    public void GetAudioContentType_MapsCachedFormats(string file, string expected)
        => Assert.Equal(expected, DownloadService.GetAudioContentType(file));

    // MARK: Retries

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden")]
    [InlineData("ERROR: [youtube] abc: Unable to download webpage: HTTP Error 429: Too Many Requests")]
    [InlineData("ERROR: The read operation timed out")]
    public void IsTransientFailure_RetriesThrottlingAndNetworkErrors(string error)
        => Assert.True(DownloadService.IsTransientFailure(["WARNING: something", error]));

    [Theory]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This video has been removed by the uploader")]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access to this video")]
    public void IsTransientFailure_DoesNotRetryPermanentErrors(string error)
        => Assert.False(DownloadService.IsTransientFailure([error]));

    [Fact]
    public void IsTransientFailure_NoOutput_IsNotTransient()
        => Assert.False(DownloadService.IsTransientFailure(null));

    [Fact]
    public void GetDenoTarget_KnowsThisPlatform()
        => Assert.NotNull(DownloadService.GetDenoTarget());

    // MARK: Path sanitizing (filenames only — never applied to tags)

    [Theory]
    [InlineData("Bach/Busoni", "BachBusoni")]
    [InlineData("Argerich & Barenboim", "Argerich and Barenboim")]
    [InlineData("a#b%c", "abc")]
    [InlineData("  ..name..  ", "name")]
    [InlineData("Carnival of the Animals: XIII. The Swan", "Carnival of the Animals - XIII. The Swan")]
    [InlineData("Bagatelle \"Für Elise\"", "Bagatelle 'Für Elise'")]
    [InlineData("Who? Me*<>|", "Who Me")]
    public void SanitizePathComponent_RemovesProblemChars(string input, string expected)
        => Assert.Equal(expected, DownloadService.SanitizePathComponent(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("###")]
    public void SanitizePathComponent_EmptyResult_FallsBackToUnknown(string input)
        => Assert.Equal("Unknown", DownloadService.SanitizePathComponent(input));

    [Fact]
    public void SanitizePathComponent_ShortensLongNamesAtAWordBreak()
    {
        var name = "Goldberg Variations, BWV 988: Aria da capo e fine, as recorded in the 1981 session at the Columbia 30th Street Studio in New York";

        var result = DownloadService.SanitizePathComponent(name);

        Assert.True(result.Length <= DownloadService.MaxPathComponentLength, result);
        Assert.StartsWith("Goldberg Variations, BWV 988 - Aria da capo", result, StringComparison.Ordinal);
        Assert.False(result.EndsWith(' '));
    }

    // MARK: Channel names

    [Theory]
    [InlineData("Lang Lang - Topic", "Lang Lang")]
    [InlineData("HilaryHahnVEVO", "HilaryHahn")]
    [InlineData("Deutsche Grammophon", "Deutsche Grammophon")]
    [InlineData("  ", null)]
    public void ChannelToArtist_StripsTopicAndVevo(string channel, string? expected)
        => Assert.Equal(expected, DownloadService.ChannelToArtist(channel));

    // MARK: Log sanitizing

    [Fact]
    public void SanitizeForLog_StripsControlChars()
        => Assert.Equal("abc", DownloadService.SanitizeForLog("a\nb\tc"));

    [Fact]
    public void SanitizeForLog_NullOrEmpty_ReturnsPlaceholder()
        => Assert.Equal("[empty]", DownloadService.SanitizeForLog(null));

    [Fact]
    public void SanitizeForLog_Truncates_LongInput()
    {
        var result = DownloadService.SanitizeForLog(new string('x', 500));
        Assert.EndsWith("...", result);
        Assert.Equal(203, result.Length); // 200 chars + "..."
    }
}
