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
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ShareX.HelpersLib;

/// <summary>
/// Keeps track of where the pointer is on the desktop.
/// </summary>
/// <remarks>
/// Wayland has no equivalent of GetCursorPos: a client only learns the pointer position while the
/// pointer is over one of its own surfaces. ShareX windows report what they see here, and the
/// last known position is used to answer questions like "which monitor is the user on". When
/// nothing has been seen yet the centre of the primary screen is a sane stand-in.
/// </remarks>
public static class PointerPositionTracker
{
    private static readonly object positionLock = new();
    private static Point? lastKnownPosition;

    /// <summary>The most recent pointer position in desktop coordinates.</summary>
    public static Point Current
    {
        get
        {
            lock (positionLock)
            {
                if (lastKnownPosition is { } position) return position;
            }

            return GetPrimaryScreenCenter();
        }
    }

    /// <summary>True once a ShareX window has actually seen the pointer.</summary>
    public static bool HasPosition
    {
        get
        {
            lock (positionLock) return lastKnownPosition.HasValue;
        }
    }

    /// <summary>Starts reporting pointer positions seen by this window. Safe to call more than once.</summary>
    public static void Track(Window window)
    {
        if (window == null) return;

        window.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
        window.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static void Report(Point position)
    {
        lock (positionLock) lastKnownPosition = position;
    }

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Window window) return;

        Avalonia.Point point = e.GetPosition(window);
        double scaling = window.RenderScaling;

        // Window.Position is in physical pixels; pointer coordinates are in logical units.
        Report(new Point(
            window.Position.X + (int)(point.X * scaling),
            window.Position.Y + (int)(point.Y * scaling)));
    }

    private static Point GetPrimaryScreenCenter()
    {
        DesktopScreen? screen = DesktopScreen.PrimaryScreen;

        if (screen == null) return Point.Empty;

        return new Point(screen.Bounds.X + (screen.Bounds.Width / 2), screen.Bounds.Y + (screen.Bounds.Height / 2));
    }
}
