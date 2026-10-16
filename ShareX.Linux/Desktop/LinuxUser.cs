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

using System.Runtime.InteropServices;

namespace ShareX.Linux.Desktop;

/// <summary>Who ShareX is running as, and whether that user could become root.</summary>
public static partial class LinuxUser
{
    private static readonly Lazy<bool> canElevate = new(DetectElevationRights);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    public static bool IsRoot => GetEffectiveUserId() == 0;

    /// <summary>
    /// True when the user belongs to a group that sudo normally grants root to. This is a hint
    /// for the interface, not an authorisation check.
    /// </summary>
    public static bool CanElevate => canElevate.Value;

    private static bool DetectElevationRights()
    {
        if (IsRoot) return true;

        string? groups = LinuxEnvironment.RunCommand("id", "-Gn");

        if (string.IsNullOrWhiteSpace(groups)) return false;

        string[] adminGroups = ["wheel", "sudo", "admin", "root"];

        return groups.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(group => adminGroups.Contains(group, StringComparer.Ordinal));
    }
}
