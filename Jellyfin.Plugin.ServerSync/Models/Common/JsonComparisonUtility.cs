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
    /// Properties where both values are "empty" (null, empty string, empty array) are not counted.
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

            var props1 = obj1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var props2 = obj2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

            // Get all unique property names
            var allKeys = new HashSet<string>(props1.Keys);
            allKeys.UnionWith(props2.Keys);

            foreach (var key in allKeys)
            {
                var has1 = props1.TryGetValue(key, out var val1);
                var has2 = props2.TryGetValue(key, out var val2);

                if (has1 && has2)
                {
                    // Both have the property - compare values
                    if (!JsonElementEquals(val1, val2))
                    {
                        diffCount++;
                    }
                }
                else if (has1)
                {
                    // Only obj1 has property - count as diff only if non-empty
                    if (!IsEmptyValue(val1))
                    {
                        diffCount++;
                    }
                }
                else if (has2)
                {
                    // Only obj2 has property - count as diff only if non-empty
                    if (!IsEmptyValue(val2))
                    {
                        diffCount++;
                    }
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
    /// Checks if a JsonElement represents an "empty" value (null, empty string, empty array, empty object).
    /// </summary>
    private static bool IsEmptyValue(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrEmpty(e.GetString()),
            JsonValueKind.Array => e.GetArrayLength() == 0,
            JsonValueKind.Object => !e.EnumerateObject().Any(),
            _ => false
        };
    }

    /// <summary>
    /// Recursively compares two JsonElements for equality.
    /// Treats null, undefined, empty string, empty array, and empty object as equivalent.
    /// </summary>
    /// <param name="e1">First element.</param>
    /// <param name="e2">Second element.</param>
    /// <returns>True if equal, false otherwise.</returns>
    public static bool JsonElementEquals(JsonElement e1, JsonElement e2)
    {
        // If both are "empty" values, consider them equal
        if (IsEmptyValue(e1) && IsEmptyValue(e2))
        {
            return true;
        }

        // If only one is empty, they're different
        if (IsEmptyValue(e1) || IsEmptyValue(e2))
        {
            return false;
        }

        // At this point, neither is empty - check type match
        if (e1.ValueKind != e2.ValueKind)
        {
            return false;
        }

        switch (e1.ValueKind)
        {
            case JsonValueKind.Object:
                var props1 = e1.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var props2 = e2.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                // Get all unique property names
                var allKeys = new HashSet<string>(props1.Keys);
                allKeys.UnionWith(props2.Keys);

                foreach (var key in allKeys)
                {
                    var has1 = props1.TryGetValue(key, out var val1);
                    var has2 = props2.TryGetValue(key, out var val2);

                    if (has1 && has2)
                    {
                        // Both have the property - compare values
                        if (!JsonElementEquals(val1, val2))
                        {
                            return false;
                        }
                    }
                    else if (has1)
                    {
                        // Only obj1 has property - treat as equal if value is empty
                        if (!IsEmptyValue(val1))
                        {
                            return false;
                        }
                    }
                    else if (has2)
                    {
                        // Only obj2 has property - treat as equal if value is empty
                        if (!IsEmptyValue(val2))
                        {
                            return false;
                        }
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
                var s1 = e1.GetString();
                var s2 = e2.GetString();

                // Both null or empty are considered equal (already handled above, but double-check)
                if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
                {
                    return true;
                }

                // Direct string match
                if (s1 == s2)
                {
                    return true;
                }

                // Try to parse as dates and compare (handles timezone format differences)
                if (DateTime.TryParse(s1, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt1) &&
                    DateTime.TryParse(s2, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt2))
                {
                    // Compare as UTC to handle timezone differences like +00:00 vs Z
                    return dt1.ToUniversalTime() == dt2.ToUniversalTime();
                }

                return false;

            case JsonValueKind.Number:
                return e1.GetRawText() == e2.GetRawText();

            case JsonValueKind.True:
            case JsonValueKind.False:
                return e1.GetBoolean() == e2.GetBoolean();

            case JsonValueKind.Null:
                return true;

            default:
                return e1.GetRawText() == e2.GetRawText();
        }
    }
}
