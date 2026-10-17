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
using ShareX.Linux.Portals;
using SkiaSharp;

namespace ShareX.Linux.Capture;

/// <summary>
/// Screen capture through org.freedesktop.portal.Screenshot.
/// </summary>
/// <remarks>
/// The portal is the only option on compositors that do not implement wlr-screencopy, notably
/// GNOME and KDE. The compositor renders the image, writes it to a file and hands back the URI,
/// after asking the user for permission. It cannot crop, so a region is a crop of the whole
/// desktop, and it is slower than a direct capture because of the round trip and the file.
/// </remarks>
public sealed class PortalScreenCaptureBackend : IScreenCaptureBackend
{
    public string Name => "xdg-desktop-portal Screenshot";

    public bool IsAvailable => PortalConnection.IsAvailable;

    public SKBitmap? CaptureDesktop() => Capture(interactive: false);

    /// <summary>The portal returns the whole desktop; cropping is left to the caller.</summary>
    public SKBitmap? CaptureRectangle(Rectangle bounds) => null;

    public SKBitmap? CaptureInteractive() => Capture(interactive: true);

    private static SKBitmap? Capture(bool interactive)
    {
        try
        {
            string path = new ScreenshotPortal()
                .CaptureAsync(interactive, modal: false)
                .GetAwaiter().GetResult();

            return LoadAndDelete(path);
        }
        catch (PortalCancelledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            WaylandScreenCapture.ExceptionLogger?.Invoke(exception,
                "The screenshot portal failed. On a wlroots compositor this usually means " +
                "XDG_CURRENT_DESKTOP does not name your compositor, so xdg-desktop-portal picked " +
                "a backend that cannot take screenshots.");
            return null;
        }
    }

    private static SKBitmap? LoadAndDelete(string path)
    {
        try
        {
            return WaylandScreenCapture.DecodeImage(File.ReadAllBytes(path));
        }
        finally
        {
            DeleteIfTemporary(path);
        }
    }

    /// <summary>
    /// Removes the file the portal produced. The portal hands the image to us and expects the
    /// application to clean it up, but a user who pointed the portal at their Pictures folder
    /// wants to keep it, so only transient locations are touched.
    /// </summary>
    private static void DeleteIfTemporary(string path)
    {
        string[] transientRoots =
        [
            Path.GetTempPath(),
            LinuxEnvironment.RuntimeDir,
            LinuxEnvironment.CacheHome,
            "/tmp"
        ];

        try
        {
            string full = Path.GetFullPath(path);

            foreach (string root in transientRoots)
            {
                if (!string.IsNullOrEmpty(root) && full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
                {
                    File.Delete(full);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            WaylandScreenCapture.ExceptionLogger?.Invoke(exception,
                $"Removing the temporary screenshot \"{path}\" failed.");
        }
    }
}
