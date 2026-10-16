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
using System.Linq;
using ShareX.HelpersLib;
using ShareX.Linux;

namespace ShareX
{
    /// <summary>
    /// Hooks ShareX into the rest of the desktop.
    /// </summary>
    /// <remarks>
    /// On Linux this is just the browser native-messaging manifests: each browser reads them
    /// from a well-known directory under the user's profile, so there is nothing to register
    /// with the system. File-type associations and launcher entries are left to packaging and to
    /// the user's own desktop configuration.
    /// </remarks>
    public static class IntegrationHelpers
    {
        private const string ChromeHostName = "com.getsharex.sharex";
        private const string FirefoxHostName = "ShareX";
        private const string ChromeExtensionId = "chrome-extension://nlkoigbdolhchiicbonbihbphgamnaoc/";
        private const string FirefoxExtensionId = "firefox@getsharex.com";

        /// <summary>Chromium-family profile directories that read native-messaging manifests.</summary>
        private static readonly string[] chromiumProfileDirectories =
        [
            "google-chrome",
            "google-chrome-beta",
            "chromium",
            "BraveSoftware/Brave-Browser",
            "microsoft-edge",
            "vivaldi"
        ];

        private static string NativeMessagingHostPath => FileHelpers.GetAbsolutePath("ShareX_NativeMessagingHost");

        public static bool CheckChromeExtensionSupport() =>
            GetChromiumManifestPaths().Any(File.Exists);

        public static void CreateChromeExtensionSupport(bool create)
        {
            try
            {
                string manifest = BuildManifest(ChromeHostName, "allowed_origins", ChromeExtensionId);

                foreach (string path in GetChromiumManifestPaths())
                {
                    WriteOrRemoveManifest(path, manifest, create);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        public static bool CheckFirefoxAddonSupport() => File.Exists(GetFirefoxManifestPath());

        public static void CreateFirefoxAddonSupport(bool create)
        {
            try
            {
                string manifest = BuildManifest(FirefoxHostName, "allowed_extensions", FirefoxExtensionId);

                WriteOrRemoveManifest(GetFirefoxManifestPath(), manifest, create);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        /// <summary>Removes everything ShareX wrote outside its own configuration directory.</summary>
        public static void Uninstall()
        {
            CreateChromeExtensionSupport(false);
            CreateFirefoxAddonSupport(false);
        }

        private static IEnumerable<string> GetChromiumManifestPaths()
        {
            // Chromium browsers look under the profile root, except when they are in a Flatpak,
            // which ShareX cannot write into anyway.
            foreach (string browser in chromiumProfileDirectories)
            {
                string root = Path.Combine(LinuxEnvironment.ConfigHome, browser);

                // Only offer a browser the user actually has.
                if (Directory.Exists(root))
                {
                    yield return Path.Combine(root, "NativeMessagingHosts", ChromeHostName + ".json");
                }
            }
        }

        private static string GetFirefoxManifestPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mozilla", "native-messaging-hosts", FirefoxHostName + ".json");

        private static string BuildManifest(string name, string allowedKey, string allowedValue) =>
            $$"""
            {
              "name": "{{name}}",
              "description": "ShareX",
              "path": "{{NativeMessagingHostPath}}",
              "type": "stdio",
              "{{allowedKey}}": [
                "{{allowedValue}}"
              ]
            }
            """;

        private static void WriteOrRemoveManifest(string path, string manifest, bool create)
        {
            if (create)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, manifest);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
