using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Guards multi-genre handling. Regression context: genres were once written to the file as a
/// single joined value ("Classical; Baroque"), which Jellyfin shows as ONE genre. The correct storage
/// is one tag value per genre (a separate "GENRE=" Vorbis comment for Opus). These tests pin
/// both the parsing and the on-disk shape so that bug cannot return silently.
/// </summary>
public class GenreTests
{
    // MARK: ParseGenres parsing

    [Theory]
    [InlineData("Classical;Baroque")]      // no space (the form that used to break)
    [InlineData("Classical; Baroque")]     // one space
    [InlineData("Classical ; Baroque")]    // space either side
    [InlineData("Classical;Baroque;")]     // trailing delimiter
    [InlineData(" Classical ;  Baroque ")] // padded
    public void ParseGenres_SplitsIntoSeparateEntries(string input)
    {
        var result = DownloadService.ParseGenres(input);
        Assert.Equal(new[] { "Classical", "Baroque" }, result);
    }

    [Theory]
    [InlineData("Classical")]
    [InlineData("  Classical  ")]
    public void ParseGenres_SingleGenre_ReturnsOneTrimmedEntry(string input)
        => Assert.Equal(new[] { "Classical" }, DownloadService.ParseGenres(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData(" ; ")]
    public void ParseGenres_EmptyOrDelimiterOnly_ReturnsEmpty(string? input)
        => Assert.Empty(DownloadService.ParseGenres(input));

    // MARK: On-disk storage (the actual regression)

    [Fact]
    public void WritingMultipleGenres_StoresSeparateValues_NotOneJoinedString()
    {
        var path = CopyFixture();
        try
        {
            using (var write = TagLib.File.Create(path))
            {
                write.Tag.Genres = DownloadService.ParseGenres("Classical; Baroque");
                write.Save();
            }

            using var read = TagLib.File.Create(path);

            // Two distinct values, not a single "Classical; Baroque" entry.
            Assert.Equal(new[] { "Classical", "Baroque" }, read.Tag.Genres);
            Assert.DoesNotContain(read.Tag.Genres, g => g.Contains(';', StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WritingSingleGenre_RoundTripsAsOneValue()
    {
        var path = CopyFixture();
        try
        {
            using (var write = TagLib.File.Create(path))
            {
                write.Tag.Genres = DownloadService.ParseGenres("Classical");
                write.Save();
            }

            using var read = TagLib.File.Create(path);
            Assert.Equal(new[] { "Classical" }, read.Tag.Genres);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Copies the bundled silent Opus fixture to a unique temp path so each test writes freely.
    private static string CopyFixture()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "TestData", "silence.opus");
        var dst = Path.Combine(Path.GetTempPath(), $"yta-genre-{Guid.NewGuid():N}.opus");
        File.Copy(src, dst, overwrite: true);
        return dst;
    }
}
