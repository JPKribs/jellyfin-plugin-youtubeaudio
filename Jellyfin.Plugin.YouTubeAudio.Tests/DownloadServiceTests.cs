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

    // MARK: Format mapping

    [Theory]
    [InlineData(AudioFormat.Opus, ".opus")]
    [InlineData(AudioFormat.Mp3, ".mp3")]
    [InlineData(AudioFormat.M4a, ".m4a")]
    public void GetFileExtension_MapsFormat(AudioFormat format, string expected)
        => Assert.Equal(expected, DownloadService.GetFileExtension(format));

    [Theory]
    [InlineData(AudioFormat.Opus, AudioConversionFormat.Opus)]
    [InlineData(AudioFormat.Mp3, AudioConversionFormat.Mp3)]
    [InlineData(AudioFormat.M4a, AudioConversionFormat.Aac)]
    public void MapToAudioConversionFormat_MapsFormat(AudioFormat format, AudioConversionFormat expected)
        => Assert.Equal(expected, DownloadService.MapToAudioConversionFormat(format));

    // MARK: Title cleaning

    [Theory]
    [InlineData("Disturbed - Ten Thousand Fists", "Disturbed", "Ten Thousand Fists")]
    [InlineData("Song (Official Video)", null, "Song")]
    [InlineData("Song [Official Audio]", null, "Song")]
    [InlineData("Disturbed - Song (Official Music Video)", "Disturbed", "Song")]
    [InlineData("Plain Title", null, "Plain Title")]
    public void CleanYouTubeTitle_StripsArtistPrefixAndSuffixes(string title, string? artist, string expected)
        => Assert.Equal(expected, DownloadService.CleanYouTubeTitle(title, artist));

    [Fact]
    public void CleanYouTubeTitle_EmptyInput_ReturnsEmpty()
        => Assert.Equal(string.Empty, DownloadService.CleanYouTubeTitle("   ", null));

    // MARK: Path sanitizing (filenames only — never applied to tags)

    [Theory]
    [InlineData("AC/DC", "ACDC")]
    [InlineData("Foo & Bar", "Foo and Bar")]
    [InlineData("a#b%c", "abc")]
    [InlineData("  ..name..  ", "name")]
    public void SanitizePathComponent_RemovesProblemChars(string input, string expected)
        => Assert.Equal(expected, DownloadService.SanitizePathComponent(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("###")]
    public void SanitizePathComponent_EmptyResult_FallsBackToUnknown(string input)
        => Assert.Equal("Unknown", DownloadService.SanitizePathComponent(input));

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
