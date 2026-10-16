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

using System.Buffers.Binary;
using System.Diagnostics;
using SkiaSharp;

namespace ShareX.Linux.Desktop;

/// <summary>One image from a cursor theme, with the point the cursor actually points at.</summary>
public sealed record CursorImage(SKBitmap Bitmap, int HotspotX, int HotspotY);

/// <summary>
/// Reads cursors out of the user's icon theme, in the Xcursor file format.
/// </summary>
/// <remarks>
/// Wayland gives a client no way to read the pointer the compositor is drawing, but the cursor
/// itself comes from a theme on disk that every toolkit reads the same way. Loading it here is
/// what lets the editor's cursor annotation look like the pointer the user actually has, rather
/// than a drawing of a generic arrow.
/// </remarks>
public static class XCursorTheme
{
    /// <summary>"Xcur", the magic at the start of every cursor file.</summary>
    private const uint Magic = 0x72756358;

    private const uint ImageChunkType = 0xfffd0002;

    /// <summary>Guards against a malformed file asking for an absurd allocation.</summary>
    private const int MaximumDimension = 512;

    private static readonly Lazy<string> themeName = new(ResolveThemeName);

    /// <summary>The cursor theme the session is configured to use.</summary>
    public static string ThemeName => themeName.Value;

    /// <summary>
    /// Loads a cursor by its freedesktop name, for example "default", "text" or "pointer",
    /// at the size closest to <paramref name="preferredSize"/>. Returns null when the theme
    /// has no such cursor.
    /// </summary>
    public static CursorImage? Load(string name, int preferredSize = 32)
    {
        foreach (string path in GetCandidatePaths(name))
        {
            try
            {
                if (!File.Exists(path)) continue;

                CursorImage? image = ReadFile(path, preferredSize);

                if (image != null) return image;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Reading the cursor \"{path}\" failed: {exception.Message}");
            }
        }

        return null;
    }

    /// <summary>Theme directories in search order, most specific first.</summary>
    private static IEnumerable<string> GetIconDirectories()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".icons");
        yield return Path.Combine(LinuxEnvironment.DataHome, "icons");

        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share";

        foreach (string directory in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory, "icons");
        }

        yield return "/usr/share/pixmaps";
    }

    private static IEnumerable<string> GetCandidatePaths(string name)
    {
        // The configured theme first, then the fallbacks every system is expected to have.
        foreach (string theme in new[] { ThemeName, "default", "Adwaita" }.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(theme)) continue;

            foreach (string directory in GetIconDirectories())
            {
                yield return Path.Combine(directory, theme, "cursors", name);
            }
        }
    }

    private static string ResolveThemeName()
    {
        string? theme = Environment.GetEnvironmentVariable("XCURSOR_THEME");

        if (!string.IsNullOrEmpty(theme)) return theme;

        // GTK and Xcursor both read this file; it is the closest thing to a shared setting.
        foreach (string path in new[]
                 {
                     Path.Combine(LinuxEnvironment.ConfigHome, "gtk-3.0", "settings.ini"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".icons", "default", "index.theme")
                 })
        {
            try
            {
                if (!File.Exists(path)) continue;

                foreach (string line in File.ReadLines(path))
                {
                    if (!line.StartsWith("gtk-cursor-theme-name=", StringComparison.Ordinal) &&
                        !line.StartsWith("Inherits=", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string value = line[(line.IndexOf('=') + 1)..].Trim();

                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Reading the cursor theme from \"{path}\" failed: {exception.Message}");
            }
        }

        return "default";
    }

    /// <summary>
    /// Parses an Xcursor file: a small header, a table of contents, then one chunk per image.
    /// Animated cursors hold several images per size; the first frame is the one wanted here.
    /// </summary>
    private static CursorImage? ReadFile(string path, int preferredSize)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length < 16 || ReadUInt32(data, 0) != Magic) return null;

        uint headerSize = ReadUInt32(data, 4);
        uint entryCount = ReadUInt32(data, 12);

        if (entryCount is 0 or > 4096) return null;

        int best = -1;
        long bestDistance = long.MaxValue;

        for (uint i = 0; i < entryCount; i++)
        {
            int entry = (int)headerSize + ((int)i * 12);

            if (entry + 12 > data.Length) break;

            if (ReadUInt32(data, entry) != ImageChunkType) continue;

            long distance = Math.Abs((long)ReadUInt32(data, entry + 4) - preferredSize);

            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = (int)ReadUInt32(data, entry + 8);
        }

        if (best < 0 || best + 36 > data.Length) return null;

        int width = (int)ReadUInt32(data, best + 16);
        int height = (int)ReadUInt32(data, best + 20);

        if (width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension) return null;

        int pixelsOffset = best + 36;
        int pixelCount = width * height;

        if (pixelsOffset + (pixelCount * 4) > data.Length) return null;

        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        try
        {
            // Xcursor stores premultiplied ARGB in native byte order, which on every platform
            // .NET runs on is little endian - the same layout as Skia's Bgra8888.
            data.AsSpan(pixelsOffset, pixelCount * 4).CopyTo(bitmap.GetPixelSpan());

            return new CursorImage(bitmap, (int)ReadUInt32(data, best + 24), (int)ReadUInt32(data, best + 28));
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}
