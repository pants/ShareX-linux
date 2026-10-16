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

#nullable enable

using System.Drawing;

namespace ShareX.HelpersLib;

/// <summary>
/// What ShareX knows about the window a capture came from, used for filename patterns such as
/// %pn and %t and for history metadata.
/// </summary>
/// <remarks>
/// On Wayland a client cannot inspect other applications' windows: there is no equivalent of
/// GetForegroundWindow, no window list and no way to read another surface's title. When a
/// capture goes through the compositor's own window picker the compositor keeps the identity of
/// the chosen window to itself, so these fields are usually empty and the filename patterns that
/// depend on them fall back to their defaults.
/// </remarks>
public sealed class WindowInfo
{
    public static WindowInfo Unknown { get; } = new();

    /// <summary>The window's title, when the desktop made it available.</summary>
    public string Text { get; init; } = "";

    /// <summary>The owning application's process name, when it is known.</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>The window's bounds in desktop coordinates, when they are known.</summary>
    public Rectangle Rectangle { get; init; }

    public bool IsValid => !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(ProcessName) || !Rectangle.IsEmpty;

    public override string ToString() => IsValid ? $"{Text} ({ProcessName})" : "unknown window";
}
