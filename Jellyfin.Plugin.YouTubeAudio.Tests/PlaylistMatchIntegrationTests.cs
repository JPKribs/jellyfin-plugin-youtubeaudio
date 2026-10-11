using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;
using YoutubeDLSharp;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Runs the real YouTube search for every track in the Apple Music fixture, exactly as
/// DownloadService does, and checks the matcher accepts an upload from the artist for each.
/// Opt-in like <see cref="DownloadIntegrationTests"/>:
///
///   YTA_RUN_INTEGRATION=1 dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class PlaylistMatchIntegrationTests
{
    [SkippableFact]
    public async Task RealSearch_MatchesEveryFixtureTrack()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("YTA_RUN_INTEGRATION"), "1", StringComparison.Ordinal),
            "Set YTA_RUN_INTEGRATION=1 to run network tests.");

        var ytdlp = DownloadIntegrationTests.Which("yt-dlp");
        Skip.If(ytdlp is null, "yt-dlp not found on PATH.");

        var bytes = File.ReadAllBytes(Path.Combine("TestData", "AppleMusicPlaylist.txt"));
        var playlist = new AppleMusicPlaylistSource().Parse("Study Session.txt", PlaylistImportService.DecodeText(bytes))[0];
        var ytdl = new YoutubeDL { YoutubeDLPath = ytdlp! };

        var results = await Task.WhenAll(playlist.Tracks.Select(async track =>
        {
            var query = YouTubeMatcher.BuildSearchQuery(track);
            var search = await ytdl.RunVideoDataFetch($"ytsearch{YouTubeMatcher.SearchResultCount}:{query}", flat: true).ConfigureAwait(false);
            if (!search.Success)
            {
                // A burst of parallel searches is occasionally refused; one retry tells a
                // transient failure apart from a real problem.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                search = await ytdl.RunVideoDataFetch($"ytsearch{YouTubeMatcher.SearchResultCount}:{query}", flat: true).ConfigureAwait(false);
            }

            Assert.True(search.Success, $"YouTube search failed for {query}: {string.Join("; ", search.ErrorOutput ?? [])}");
            var candidates = (search.Data?.Entries ?? [])
                .Where(e => !string.IsNullOrEmpty(e.ID))
                .Select(e => new MatchCandidate(e.ID, e.Title ?? string.Empty, e.Channel ?? e.Uploader, e.Duration));
            return (Query: query, Match: YouTubeMatcher.PickBest(track, candidates));
        })).ConfigureAwait(false);

        var unmatched = results.Where(r => r.Match is null || !r.Match.ChannelMatchesArtist).Select(r => r.Query).ToList();
        Assert.True(unmatched.Count == 0, "No artist upload matched: " + string.Join(", ", unmatched));
    }
}
