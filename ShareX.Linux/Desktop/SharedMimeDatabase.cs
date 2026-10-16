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

namespace ShareX.Linux.Desktop;

/// <summary>
/// Looks up MIME types in the freedesktop shared MIME database, the Linux counterpart of the
/// file-type entries ShareX used to read from the Windows registry.
/// </summary>
public static class SharedMimeDatabase
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> globs = new(LoadGlobs);

    /// <summary>Returns the MIME type registered for an extension such as ".png", or null.</summary>
    public static string? GetMimeTypeFromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;

        string suffix = extension.TrimStart('.').ToLowerInvariant();

        return globs.Value.TryGetValue(suffix, out string? mimeType) ? mimeType : null;
    }

    /// <summary>Asks the database what a specific file is, which also inspects its contents.</summary>
    public static string? GetMimeTypeFromFile(string path)
    {
        string? output = LinuxEnvironment.RunCommand("xdg-mime", "query", "filetype", path);

        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    /// <summary>
    /// Reads the "globs" index that update-mime-database generates. It is a flat
    /// "mimetype:pattern" list, so the whole thing is parsed once and kept.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadGlobs()
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in GetMimeDirectories())
        {
            string path = Path.Combine(directory, "globs");

            try
            {
                if (!File.Exists(path)) continue;

                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;

                    int separator = line.IndexOf(':');
                    if (separator <= 0) continue;

                    string pattern = line[(separator + 1)..];

                    // Only simple "*.ext" globs map cleanly onto an extension lookup.
                    if (!pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.Length <= 2) continue;

                    string suffix = pattern[2..];
                    if (suffix.Contains('*') || suffix.Contains('?') || suffix.Contains('[')) continue;

                    // Earlier directories take priority, matching the spec's search order.
                    result.TryAdd(suffix, line[..separator]);
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Reading \"{path}\" failed: {exception.Message}");
            }
        }

        return result;
    }

    private static IEnumerable<string> GetMimeDirectories()
    {
        yield return Path.Combine(LinuxEnvironment.DataHome, "mime");

        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share";

        foreach (string directory in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory, "mime");
        }
    }
}
