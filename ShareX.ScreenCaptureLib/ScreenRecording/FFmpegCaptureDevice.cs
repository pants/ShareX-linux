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
    public class FFmpegCaptureDevice
    {
        public string Value { get; set; }
        public string Title { get; set; }

        public FFmpegCaptureDevice(string value, string title)
        {
            Value = value;
            Title = title;
        }

        public static FFmpegCaptureDevice None { get; } = new FFmpegCaptureDevice("", "None");

        /// <summary>
        /// Screen capture through the ScreenCast portal. The compositor publishes the chosen
        /// monitor or window as a PipeWire stream, which ShareX feeds to ffmpeg.
        /// </summary>
        public static FFmpegCaptureDevice PipeWire { get; } = new FFmpegCaptureDevice("pipewire", "Screen (PipeWire via xdg-desktop-portal)");

        /// <summary>Records whatever is playing, by reading a PulseAudio monitor source.</summary>
        public static FFmpegCaptureDevice DesktopAudio { get; } = new FFmpegCaptureDevice("@DEFAULT_MONITOR@", "Desktop audio (default output monitor)");

        /// <summary>The session's default microphone.</summary>
        public static FFmpegCaptureDevice DefaultMicrophone { get; } = new FFmpegCaptureDevice("@DEFAULT_SOURCE@", "Default microphone");

        public override string ToString()
        {
            return Title;
        }
    }
}