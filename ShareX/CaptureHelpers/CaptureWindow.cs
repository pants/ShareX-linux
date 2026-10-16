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

namespace ShareX
{
    /// <summary>
    /// Captures a single window.
    /// </summary>
    /// <remarks>
    /// A Wayland client cannot see, name or address another application's window, so ShareX
    /// cannot pick one itself. The capture is handed to the compositor's interactive screenshot
    /// mode, where the user points at the window they want. For the same reason the capture
    /// carries no window title or process name in its metadata.
    /// </remarks>
    public class CaptureWindow : CaptureBase
    {
        public CaptureWindow()
        {
        }

        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            TaskMetadata metadata = CreateMetadata();

            metadata.Image = TaskHelpers.GetScreenshot(taskSettings).CaptureWindowInteractive();

            return metadata;
        }
    }
}
