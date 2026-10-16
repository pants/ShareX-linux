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
using System.Collections.Generic;
using System.IO;
using ShareX.HelpersLib;
using ShareX.Linux;

namespace ShareX
{
    /// <summary>
    /// Deployment policy an administrator can set for every user on the machine.
    /// </summary>
    /// <remarks>
    /// These were registry values under HKLM/HKCU on Windows. The Linux equivalent is a plain
    /// key=value file: /etc/sharex.conf for the machine, overridden per user by
    /// $XDG_CONFIG_HOME/ShareX/policy.conf.
    /// </remarks>
    public static class SystemOptions
    {
        private const string SystemPolicyPath = "/etc/sharex.conf";

        public static bool DisableUpdateCheck { get; private set; }
        public static bool DisableUpload { get; private set; }
        public static bool DisableLogging { get; private set; }
        public static string PersonalPath { get; private set; }

        public static void UpdateSystemOptions()
        {
            Dictionary<string, string> options = ReadPolicyFiles();

            DisableUpdateCheck = GetBoolean(options, "DisableUpdateCheck");
            DisableUpload = GetBoolean(options, "DisableUpload");
            DisableLogging = GetBoolean(options, "DisableLogging");
            PersonalPath = options.TryGetValue("PersonalPath", out string path) ? path : null;
        }

        private static Dictionary<string, string> ReadPolicyFiles()
        {
            Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);

            // The per-user file is read second so it wins.
            foreach (string path in new[] { SystemPolicyPath, Path.Combine(LinuxEnvironment.ConfigHome, "ShareX", "policy.conf") })
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    foreach (string line in File.ReadAllLines(path))
                    {
                        string entry = line.Trim();

                        if (entry.Length == 0 || entry[0] is '#' or ';') continue;

                        int separator = entry.IndexOf('=');

                        if (separator <= 0) continue;

                        options[entry[..separator].Trim()] = entry[(separator + 1)..].Trim();
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, $"Reading the policy file \"{path}\" failed.");
                }
            }

            return options;
        }

        private static bool GetBoolean(Dictionary<string, string> options, string name) =>
            options.TryGetValue(name, out string value) &&
            (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
