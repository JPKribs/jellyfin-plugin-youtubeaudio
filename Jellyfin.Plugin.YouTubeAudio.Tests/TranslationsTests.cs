using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTubeAudio.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests that every key the pages use has a string, and that server messages follow the page's language.
/// </summary>
public sealed class TranslationsTests
{
    private static readonly string[] KeyPatterns =
    [
        @"data-str(?:-[a-z]+)?=""(\w+)""",
        @"\btr\(\s*'(\w+)'",
        @"\bformat\(\s*'(\w+)'",
        @"countText\(\s*t\s*,[^,]+,\s*'(\w+)',\s*'(\w+)'",
        @"\bkey: '(\w+)'",
        @"(?<=_KEYS = \{[^}]*)'(\w+)'"
    ];

    [Fact]
    public void EveryKeyThePagesUseHasAString()
    {
        var project = Path.Combine(FindRepository(), "Jellyfin.Plugin.YouTubeAudio");
        using var local = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "Localization", "en-us.json")));
        var known = local.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(project, "Jellyfin.Plugin.YouTubeAudio.csproj")), @"<JellyfinString Include=""(\w+)"""))
        {
            known.Add(m.Groups[1].Value);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(project, "Web")))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in KeyPatterns)
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    foreach (var group in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success))
                    {
                        used.Add(group.Value);
                    }
                }
            }
        }

        Assert.True(used.Count > 250, "Found only " + used.Count + " keys, so a pattern no longer matches the pages.");
        Assert.All(used, key => Assert.True(known.Contains(key), key + " is neither in Localization/en-us.json nor a JellyfinString"));
    }

    [Fact]
    public void EnglishIsBuiltInWithTheDeclaredJellyfinStrings()
    {
        var english = BaseTranslations.For("en-us");
        Assert.Equal("Queue", english[TranslationKeys.Queue]);
        Assert.Equal("Download", english[TranslationKeys.Download]);
        Assert.Equal("Löschen", BaseTranslations.Get(TranslationKeys.Delete, "de"));
    }

    [Fact]
    public void KeyedException_HasEnglishMessage_AndTranslatesForThePage()
    {
        var ex = UserMessages.Argument(TranslationKeys.ErrorPlaylistTooLarge, 200);

        Assert.Equal("This playlist has more than 200 videos, the most you can queue at once. Ask an administrator to add it.", ex.Message);
        Assert.Equal(ex.Message, UserMessages.Text(ex, "de"));
        Assert.Equal("plain", UserMessages.Text(new InvalidOperationException("plain"), "de"));
    }

    /// <summary>
    /// An error body follows the language the page sends in Base's header, ahead of the browser's.
    /// True: a page in German shows a server message in German.
    /// False: a server message would follow the browser and not the page.
    /// </summary>
    [Fact]
    public void MessageBody_FollowsThePageLanguageHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "en-US";
        context.Request.Headers[BaseTranslations.LanguageHeader] = "de";

        var body = UserMessages.MessageBody(TranslationKeys.Delete, context.Request);

        Assert.Equal("{ Error = Löschen }", body.ToString());
    }

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.YouTubeAudio.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
