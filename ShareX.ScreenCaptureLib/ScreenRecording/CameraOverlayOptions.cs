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

namespace ShareX.ScreenCaptureLib
{
    public enum CameraOverlayPosition { TopLeft, TopRight, BottomLeft, BottomRight }

    public enum CameraOverlayShape { Rectangle, Circle }

    public enum CameraCaptureResolution { Size640x480, Size1280x720, Size1920x1080 }

    /// <summary>
    /// A webcam picture-in-picture composited into a screen recording.
    /// </summary>
    /// <remarks>
    /// The camera is a second ffmpeg input and the overlay is built with a filter graph, so this
    /// works with any V4L2 device and needs no GPU-specific support.
    /// </remarks>
    public class CameraOverlayOptions
    {
        public bool Enabled { get; set; }

        /// <summary>The V4L2 node to read, for example "/dev/video0".</summary>
        public string DevicePath { get; set; } = "";

        public CameraCaptureResolution Resolution { get; set; } = CameraCaptureResolution.Size1280x720;
        public int FPS { get; set; } = 30;
        public CameraOverlayPosition Position { get; set; } = CameraOverlayPosition.BottomRight;
        public CameraOverlayShape Shape { get; set; } = CameraOverlayShape.Rectangle;

        /// <summary>Overlay width as a percentage of the recording's width.</summary>
        public int WidthPercent { get; set; } = 20;

        /// <summary>Gap between the overlay and the edge of the recording, in pixels.</summary>
        public int Margin { get; set; } = 20;

        public bool IsUsable => Enabled && !string.IsNullOrEmpty(DevicePath);

        public (int Width, int Height) GetCaptureSize() => Resolution switch
        {
            CameraCaptureResolution.Size640x480 => (640, 480),
            CameraCaptureResolution.Size1920x1080 => (1920, 1080),
            _ => (1280, 720)
        };
    }
}
