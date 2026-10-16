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

using System;
using System.IO;
using ShareX.HelpersLib;
using ShareX.Linux;

namespace ShareX
{
    /// <summary>
    /// Starting ShareX when the user logs in, through the freedesktop autostart specification:
    /// a desktop entry in the autostart directory, which every desktop environment honours.
    /// </summary>
    public static class StartupManager
    {
        private static string AutostartFilePath =>
            Path.Combine(LinuxEnvironment.AutostartDirectory, "com.getsharex.ShareX-autostart.desktop");

        public static string StartupTargetPath => Environment.ProcessPath;

        public static StartupState State
        {
            get
            {
                try
                {
                    if (!File.Exists(AutostartFilePath)) return StartupState.Disabled;

                    // Desktops mark an entry the user switched off in their own settings with
                    // X-GNOME-Autostart-enabled=false rather than deleting it.
                    foreach (string line in File.ReadLines(AutostartFilePath))
                    {
                        if (line.StartsWith("X-GNOME-Autostart-enabled=", StringComparison.OrdinalIgnoreCase) &&
                            line.EndsWith("false", StringComparison.OrdinalIgnoreCase))
                        {
                            return StartupState.DisabledByUser;
                        }
                    }

                    return StartupState.Enabled;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                    return StartupState.Disabled;
                }
            }
            set
            {
                if (value != StartupState.Enabled && value != StartupState.Disabled)
                {
                    throw new NotSupportedException();
                }

                try
                {
                    if (value == StartupState.Enabled)
                    {
                        Directory.CreateDirectory(LinuxEnvironment.AutostartDirectory);
                        File.WriteAllText(AutostartFilePath, BuildDesktopEntry());
                    }
                    else if (File.Exists(AutostartFilePath))
                    {
                        File.Delete(AutostartFilePath);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }
        }

        private static string BuildDesktopEntry() =>
            $"""
            [Desktop Entry]
            Type=Application
            Name=ShareX
            Comment=Screen capture, file sharing and productivity tool
            Exec="{StartupTargetPath}" -silent
            Icon={LinuxEnvironment.ApplicationId}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
    }
}
