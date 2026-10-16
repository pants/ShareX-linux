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

using Avalonia;
using Avalonia.Controls;
using ShareX.Linux.Portals;

namespace ShareX.AvaloniaUI.Windows;

/// <summary>
/// Lets the user pick a colour from anywhere on screen.
/// </summary>
/// <remarks>
/// On Wayland the compositor owns the pixels, so it also owns this interaction: the Screenshot
/// portal's PickColor shows the desktop's own magnifier and returns the colour the user
/// settled on. ShareX therefore has no overlay of its own here, and the magnifier options in
/// <see cref="ScreenColorPickerOptions"/> are decided by the compositor instead.
/// </remarks>
public sealed class ScreenColorPickerWindow
{
    private readonly ScreenColorPickerOptions options;

    public ScreenColorPickerWindow() : this(new ScreenColorPickerOptions())
    {
    }

    public ScreenColorPickerWindow(ScreenColorPickerOptions options) =>
        this.options = options ?? new ScreenColorPickerOptions();

    /// <summary>Returns the colour the user picked, or null when they cancelled.</summary>
    public async Task<ScreenColorPickerResult?> PickAsync(Window? owner = null)
    {
        try
        {
            PortalColor color = await new ScreenshotPortal().PickColorAsync().ConfigureAwait(true);

            // The portal reports a colour, not where it came from: the position would say which
            // part of the screen the user looked at, which it deliberately does not disclose.
            return new ScreenColorPickerResult(
                Avalonia.Media.Color.FromRgb(color.R, color.G, color.B), default, false);
        }
        catch (PortalCancelledException)
        {
            return null;
        }
    }
}
