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

using System.Diagnostics;

namespace ShareX.Linux;

/// <summary>Desktop session facts that the rest of ShareX needs in order to pick a code path.</summary>
public static class LinuxEnvironment
{
    public const string ApplicationId = "com.getsharex.ShareX";
    public const string DesktopFileName = ApplicationId + ".desktop";

    /// <summary>True when the process talks to a Wayland compositor. ShareX only supports Wayland.</summary>
    public static bool IsWayland { get; } =
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>True when running inside a Flatpak or Snap sandbox, where host paths are not writable.</summary>
    public static bool IsSandboxed { get; } =
        File.Exists("/.flatpak-info") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"));

    /// <summary>Lower-cased desktop name, e.g. "gnome", "kde", "hyprland", or an empty string.</summary>
    public static string DesktopName { get; } =
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ??
         Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP") ?? "").ToLowerInvariant();

    /// <summary>
    /// Lower-cased name of the compositor actually running.
    /// </summary>
    /// <remarks>
    /// This is deliberately not <see cref="DesktopName"/>. Users of wlroots compositors often
    /// set XDG_CURRENT_DESKTOP to GNOME or KDE so that portals and file dialogs behave, which
    /// leaves that variable describing the look they want rather than the compositor they run.
    /// XDG_SESSION_DESKTOP keeps the real answer, and the compositor's own environment variable
    /// is better still.
    /// </remarks>
    public static string CompositorName { get; } = ResolveCompositorName();

    private static string ResolveCompositorName()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))) return "hyprland";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK"))) return "sway";

        string session = (Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP") ?? "").ToLowerInvariant();

        return string.IsNullOrEmpty(session) ? DesktopName : session;
    }

    public static string ConfigHome => ResolveXdgDirectory("XDG_CONFIG_HOME", ".config");
    public static string DataHome => ResolveXdgDirectory("XDG_DATA_HOME", ".local/share");
    public static string CacheHome => ResolveXdgDirectory("XDG_CACHE_HOME", ".cache");
    public static string StateHome => ResolveXdgDirectory("XDG_STATE_HOME", ".local/state");

    public static string RuntimeDir
    {
        get
        {
            string? dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            return Path.Combine(Path.GetTempPath(), $"sharex-{Environment.UserName}");
        }
    }

    public static string AutostartDirectory => Path.Combine(ConfigHome, "autostart");

    private static string ResolveXdgDirectory(string variable, string fallback)
    {
        string? dir = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(dir) && Path.IsPathRooted(dir)) return dir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
    }

    /// <summary>Looks a command up on PATH, returning its absolute path or null.</summary>
    public static string? FindExecutable(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";

        foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static bool HasExecutable(string command) => FindExecutable(command) != null;

    /// <summary>Runs a short-lived command and returns its standard output, or null when it is unavailable or fails.</summary>
    public static string? RunCommand(string command, params string[] arguments)
    {
        string? executable = FindExecutable(command);
        if (executable == null) return null;

        try
        {
            ProcessStartInfo startInfo = new(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process == null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
