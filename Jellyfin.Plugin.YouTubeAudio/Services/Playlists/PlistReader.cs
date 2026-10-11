using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Minimal reader for Apple XML property lists. Values become <see cref="Dictionary{TKey, TValue}"/>
/// (dict), <see cref="List{T}"/> (array), string, long (integer), double (real), bool, or
/// <see cref="DateTime"/> (date); data blobs are skipped.
/// </summary>
internal static class PlistReader
{
    /// <summary>
    /// Returns whether the text looks like an XML property list, without parsing all of it.
    /// </summary>
    public static bool LooksLikePlist(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var head = content.Length > 1024 ? content[..1024] : content;
        return head.Contains("<plist", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a property list and returns its root value, or null when the text is not one.
    /// </summary>
    public static object? Parse(string content)
    {
        // Plists declare an external DTD; never fetch or process it.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };

        try
        {
            using var stringReader = new StringReader(content);
            using var reader = XmlReader.Create(stringReader, settings);
            var root = XDocument.Load(reader).Root;
            if (root == null || root.Name.LocalName != "plist")
            {
                return null;
            }

            var value = root.Elements().FirstOrDefault();
            return value == null ? null : ReadValue(value);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static object? ReadValue(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? key = null;
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key")
                    {
                        key = child.Value;
                    }
                    else if (key != null)
                    {
                        dict[key] = ReadValue(child);
                        key = null;
                    }
                }

                return dict;
            case "array":
                return element.Elements().Select(ReadValue).ToList();
            case "string":
                return element.Value;
            case "integer":
                return long.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : null;
            case "real":
                return double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : null;
            case "true":
                return true;
            case "false":
                return false;
            case "date":
                return DateTime.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date) ? date : null;
            default:
                return null;
        }
    }
}
