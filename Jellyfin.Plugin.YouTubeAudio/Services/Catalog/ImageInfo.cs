using System;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Catalog;

/// <summary>
/// Reads the type and size of a JPEG or PNG image from its header, without decoding it.
/// </summary>
public static class ImageInfo
{
    /// <summary>
    /// Returns the image's media type and size, or null when it is not a readable JPEG or PNG.
    /// </summary>
    public static (string MimeType, int Width, int Height)? Read(ReadOnlySpan<byte> data)
    {
        // PNG: signature, then the IHDR chunk with big-endian width and height.
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ("image/png", BigEndian(data[16..20]), BigEndian(data[20..24]));
        }

        // JPEG: walk the segments to the start-of-frame, which holds height then width.
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            var length = (data[i + 2] << 8) | data[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0 ? ("image/jpeg", width, height) : null;
            }

            i += 2 + length;
        }

        return null;
    }

    private static int BigEndian(ReadOnlySpan<byte> bytes) => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
