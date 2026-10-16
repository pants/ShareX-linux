#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace ShareX.Linux.Desktop;

/// <summary>
/// Reads the shared thumbnail cache defined by the freedesktop thumbnail managing standard.
/// </summary>
/// <remarks>
/// File managers and other desktop applications all populate this cache, so ShareX can show a
/// preview for a video or a document it cannot decode itself. Thumbnails are keyed by the MD5 of
/// the file's URI - the spec's choice, used here as a cache key and nothing more - and are
/// ignored when the source file has changed since the thumbnail was made.
/// </remarks>
public static class FreedesktopThumbnails
{
    /// <summary>Cache directories in increasing size, as named by the specification.</summary>
    private static readonly (string Directory, int Size)[] sizes =
    [
        ("normal", 128),
        ("large", 256),
        ("x-large", 512),
        ("xx-large", 1024)
    ];

    /// <summary>
    /// Returns a cached thumbnail at least <paramref name="minimumSize"/> pixels on its longest
    /// edge, or null when the desktop has not generated one.
    /// </summary>
    public static SKBitmap? TryLoad(string filePath, int minimumSize)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        try
        {
            string key = GetCacheKey(filePath);
            string root = Path.Combine(LinuxEnvironment.CacheHome, "thumbnails");
            long modified = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();

            foreach ((string directory, int size) in sizes)
            {
                if (size < minimumSize) continue;

                string path = Path.Combine(root, directory, key + ".png");

                if (!File.Exists(path)) continue;

                SKBitmap? bitmap = LoadIfCurrent(path, modified);

                if (bitmap != null) return bitmap;
            }

            // Nothing big enough: a smaller thumbnail still beats no preview at all.
            for (int i = sizes.Length - 1; i >= 0; i--)
            {
                string path = Path.Combine(root, sizes[i].Directory, key + ".png");

                if (!File.Exists(path)) continue;

                SKBitmap? bitmap = LoadIfCurrent(path, modified);

                if (bitmap != null) return bitmap;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Reading a cached thumbnail for \"{filePath}\" failed: {exception.Message}");
        }

        return null;
    }

    /// <summary>The spec's cache key: the MD5 of the file's canonical URI, in lowercase hex.</summary>
    public static string GetCacheKey(string filePath)
    {
        string uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(uri)));
    }

    /// <summary>
    /// Loads a thumbnail, but only if its recorded source timestamp still matches the file, so a
    /// stale preview of an overwritten screenshot is never shown.
    /// </summary>
    private static SKBitmap? LoadIfCurrent(string thumbnailPath, long sourceModifiedTime)
    {
        long? recorded = ReadModifiedTimeTag(thumbnailPath);

        if (recorded.HasValue && recorded.Value != sourceModifiedTime) return null;

        return SKBitmap.Decode(thumbnailPath);
    }

    /// <summary>
    /// Reads the "Thumb::MTime" text chunk the standard requires thumbnail writers to embed.
    /// Returns null when the chunk is missing, which is treated as "cannot tell".
    /// </summary>
    private static long? ReadModifiedTimeTag(string thumbnailPath)
    {
        const string key = "Thumb::MTime";

        try
        {
            using FileStream stream = File.OpenRead(thumbnailPath);

            // The tEXt chunks live near the start of the file, well inside the first few KB.
            byte[] buffer = new byte[4096];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            string header = Encoding.Latin1.GetString(buffer, 0, read);

            int index = header.IndexOf(key, StringComparison.Ordinal);

            if (index < 0) return null;

            int start = index + key.Length + 1; // skip the NUL separating keyword and value
            int end = start;

            while (end < header.Length && char.IsAsciiDigit(header[end])) end++;

            return end > start && long.TryParse(header[start..end], CultureInfo.InvariantCulture, out long value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
