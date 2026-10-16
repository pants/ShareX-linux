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
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX
{
    public abstract class CaptureBase
    {
        public bool AllowAutoHideForm { get; set; } = true;
        public bool AllowAnnotation { get; set; } = true;

        public void Capture(bool autoHideForm)
        {
            Capture(null, autoHideForm);
        }

        public void Capture(TaskSettings taskSettings = null, bool autoHideForm = false)
        {
            if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

            if (taskSettings.GeneralSettings.ToastWindowAutoHide)
            {
                NotificationWindow.CloseActiveWindow();
            }

            _ = CaptureAsync(taskSettings, autoHideForm);
        }

        protected abstract TaskMetadata Execute(TaskSettings taskSettings);

        protected virtual Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            return Task.FromResult(Execute(taskSettings));
        }

        private async Task CaptureAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            try
            {
                if (taskSettings.CaptureSettings.ScreenshotDelay > 0)
                {
                    int delay = (int)(taskSettings.CaptureSettings.ScreenshotDelay * 1000);
                    await Task.Delay(delay);
                }

                await CaptureInternalAsync(taskSettings, autoHideForm);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
            }
        }

        private async Task CaptureInternalAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            bool wait = false;
            bool showMainForm = false;

            // Hiding desktop icons before a capture is a shell-specific trick with no portable
            // equivalent: each Linux desktop stores that preference its own way, if at all.

            if (autoHideForm && AllowAutoHideForm)
            {
                MainWindowIntegration.Hide();
                showMainForm = true;
                wait = true;
            }

            if (wait)
            {
                await Task.Delay(250);
            }

            TaskMetadata metadata = null;

            try
            {
                AllowAnnotation = true;
                metadata = await ExecuteAsync(taskSettings);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
            }
            finally
            {
                if (showMainForm)
                {
                    MainWindowIntegration.Activate();
                }

                AfterCapture(metadata, taskSettings);
            }
        }

        private void AfterCapture(TaskMetadata metadata, TaskSettings taskSettings)
        {
            if (metadata != null && metadata.Image != null)
            {
                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Capture, taskSettings);

                if (taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage) && !AllowAnnotation)
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AnnotateImage);
                }

                if (taskSettings.ImageSettings.ImageEffectOnlyRegionCapture &&
                    GetType() != typeof(CaptureRegion) && GetType() != typeof(CaptureLastRegion))
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AddImageEffects);
                }

                UploadManager.RunImageTask(metadata, taskSettings);
            }
        }

        protected TaskMetadata CreateMetadata()
        {
            return CreateMetadata(Rectangle.Empty, null);
        }

        protected TaskMetadata CreateMetadata(Rectangle insideRect)
        {
            return CreateMetadata(insideRect, "explorer");
        }

        /// <summary>
        /// Builds the metadata for a capture.
        /// </summary>
        /// <remarks>
        /// On Windows this recorded which window was in front at the moment of capture, feeding
        /// the %pn and %t filename patterns. Wayland gives a client no way to look at another
        /// application's windows, so the metadata is left empty and those patterns fall back to
        /// their defaults.
        /// </remarks>
        protected TaskMetadata CreateMetadata(Rectangle insideRect, string ignoreProcess)
        {
            return new TaskMetadata();
        }
    }
}
