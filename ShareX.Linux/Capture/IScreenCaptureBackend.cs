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

using System.Drawing;
using SkiaSharp;

namespace ShareX.Linux.Capture;

/// <summary>One way of getting pixels off the screen. Backends are tried in order of preference.</summary>
public interface IScreenCaptureBackend
{
    /// <summary>Short description used in the log, so it is clear which path a capture took.</summary>
    string Name { get; }

    /// <summary>False when this backend cannot work in the current session, or has already failed.</summary>
    bool IsAvailable { get; }

    SKBitmap? CaptureDesktop();

    /// <summary>Captures part of the desktop. Backends that cannot crop should return null.</summary>
    SKBitmap? CaptureRectangle(Rectangle bounds);

    /// <summary>Captures through a picker the user drives, or null when the backend has none.</summary>
    SKBitmap? CaptureInteractive();
}
