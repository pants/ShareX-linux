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

using ShareX.HelpersLib;
using ShareX.Linux.Capture;
using System.Drawing;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Takes still screenshots.
    /// </summary>
    /// <remarks>
    /// Every grab goes through the compositor: a Wayland client has no access to the framebuffer
    /// and no way to address another application's window, so ShareX asks
    /// org.freedesktop.portal.Screenshot for an image and crops it. The user grants the
    /// permission once; after that, captures are silent on compositors that remember the grant.
    /// </remarks>
    public partial class Screenshot
    {
        /// <summary>
        /// Whether the pointer is drawn into the capture. The compositor decides this for
        /// portal screenshots, so the preference is only honoured for screen recording.
        /// </summary>
        public bool CaptureCursor { get; set; } = false;

        public bool CaptureClientArea { get; set; } = false;
        public bool RemoveOutsideScreenArea { get; set; } = true;
        public bool CaptureShadow { get; set; } = false;
        public int ShadowOffset { get; set; } = 20;
        public bool AutoHideTaskbar { get; set; } = false;
        public bool HDRScreenshotColorCorrection { get; set; } = false;

        public Bitmap CaptureRectangle(Rectangle rect)
        {
            if (RemoveOutsideScreenArea)
            {
                Rectangle bounds = CaptureHelpers.GetScreenBounds();
                rect = Rectangle.Intersect(bounds, rect);
            }

            if (rect.Width == 0 || rect.Height == 0)
            {
                return null;
            }

            return WaylandScreenCapture.CaptureRectangle(rect);
        }

        public Bitmap CaptureFullscreen()
        {
            return WaylandScreenCapture.CaptureDesktop();
        }

        /// <summary>
        /// Captures a single window, along with whatever the compositor will say about it.
        /// </summary>
        /// <remarks>
        /// Wayland gives clients no way to address someone else's window, so the compositor
        /// decides: Hyprland answers over its IPC socket, and elsewhere the portal shows its own
        /// window picker. <paramref name="window"/> is null when the compositor says nothing.
        /// </remarks>
        public Bitmap CaptureWindow(out WindowDetails window)
        {
            return WaylandScreenCapture.CaptureWindow(out window);
        }

        public Bitmap CaptureActiveMonitor()
        {
            Rectangle bounds = CaptureHelpers.GetActiveScreenBounds();

            return CaptureRectangle(bounds);
        }
    }
}
