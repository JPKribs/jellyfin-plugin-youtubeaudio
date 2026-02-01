using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Utility class for semantic JSON comparison.
/// Used across sync modules for comparing configuration values.
/// </summary>
public static class JsonComparisonUtility
{
    /// <summary>
    /// Compares two JSON strings for semantic equality.
    /// Handles differences in property ordering and formatting.
    /// </summary>
    /// <param name="json1">First JSON string.</param>
    /// <param name="json2">Second JSON string.</param>
    /// <returns>True if semantically equal, false otherwise.</returns>
    public static bool JsonEquals(string? json1, string? json2)
    {
        // Handle null/empty cases
        if (string.IsNullOrEmpty(json1) && string.IsNullOrEmpty(json2))
        {
            return true;
        }

        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            return false;
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            return JsonElementEquals(doc1.RootElement, doc2.RootElement);
        }
        catch
        {
            // If parsing fails, fall back to string comparison
            return string.Equals(json1, json2, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Counts the number of differing properties between two JSON objects.
    /// </summary>
    /// <param name="json1">First JSON string.</param>
    /// <param name="json2">Second JSON string.</param>
    /// <returns>Number of differing properties.</returns>
    public static int CountDifferences(string? json1, string? json2)
    {
        if (string.IsNullOrEmpty(json1) || string.IsNullOrEmpty(json2))
        {
            return !string.IsNullOrEmpty(json1) || !string.IsNullOrEmpty(json2) ? 1 : 0;
        }

        try
        {
            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);

            var obj1 = doc1.RootElement;
            var obj2 = doc2.RootElement;

            if (obj1.ValueKind != JsonValueKind.Object || obj2.ValueKind != JsonValueKind.Object)
            {
                return JsonElementEquals(obj1, obj2) ? 0 : 1;
            }

            int diffCount = 0;

            // Count properties in obj1 that differ from obj2
            foreach (var prop in obj1.EnumerateObject())
            {
                if (!obj2.TryGetProperty(prop.Name, out var prop2))
                {
                    diffCount++;
                    continue;
                }

                if (!JsonElementEquals(prop.Value, prop2))
                {
                    diffCount++;
                }
            }

            // Count properties in obj2 that don't exist in obj1
            foreach (var prop in obj2.EnumerateObject())
            {
                if (!obj1.TryGetProperty(prop.Name, out _))
                {
                    diffCount++;
                }
            }

            return diffCount;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Recursively compares two JsonElements for equality.
    /// </summary>
    /// <param name="e1">First element.</param>
    /// <param name="e2">Second element.</param>
    /// <returns>True if equal, false otherwise.</returns>
    public static bool JsonElementEquals(JsonElement e1, JsonElement e2)
    {
        if (e1.ValueKind != e2.ValueKind)
        {
            return false;
        }

        switch (e1.ValueKind)
        {
            case JsonValueKind.Object:
                var props1 = e1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var props2 = e2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                if (props1.Count != props2.Count)
                {
                    return false;
                }

                foreach (var kvp in props1)
                {
                    if (!props2.TryGetValue(kvp.Key, out var value2))
                    {
                        return false;
                    }

                    if (!JsonElementEquals(kvp.Value, value2))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var arr1 = e1.EnumerateArray().ToList();
                var arr2 = e2.EnumerateArray().ToList();

                if (arr1.Count != arr2.Count)
                {
                    return false;
                }

                for (int i = 0; i < arr1.Count; i++)
                {
                    if (!JsonElementEquals(arr1[i], arr2[i]))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return e1.GetString() == e2.GetString();

            case JsonValueKind.Number:
                return e1.GetRawText() == e2.GetRawText();

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;

            default:
                return e1.GetRawText() == e2.GetRawText();
        }
    }
}
