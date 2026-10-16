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

namespace ShareX.Linux.Desktop;

/// <summary>
/// Plays short notification sounds. .NET has no cross-platform audio output, so this shells out
/// to whichever PipeWire, PulseAudio or ALSA player the system provides.
/// </summary>
public static class SoundPlayback
{
    /// <summary>Players in the order they are preferred; the first one installed wins.</summary>
    private static readonly string[] playerCommands = ["pw-play", "paplay", "aplay", "ffplay"];

    private static readonly Lazy<string?> player = new(() =>
        playerCommands.Select(LinuxEnvironment.FindExecutable).FirstOrDefault(x => x != null));

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    public static bool IsAvailable => player.Value != null;

    /// <summary>Plays a sound file without waiting for it to finish.</summary>
    public static void Play(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        Start(filePath, deleteAfterwards: false);
    }

    /// <summary>
    /// Plays sound data from a stream. The player needs a file, so the data is spooled to a
    /// temporary one that is removed once playback ends.
    /// </summary>
    public static void Play(Stream stream)
    {
        if (stream == null) return;

        string path = Path.Combine(Path.GetTempPath(), $"sharex-sound-{Guid.NewGuid():N}.wav");

        try
        {
            using (stream)
            using (FileStream file = File.Create(path))
            {
                stream.CopyTo(file);
            }

            Start(path, deleteAfterwards: true);
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Writing a temporary sound file failed.");
            TryDelete(path);
        }
    }

    private static void Start(string filePath, bool deleteAfterwards)
    {
        string? executable = player.Value;

        if (executable == null)
        {
            ExceptionLogger?.Invoke(new FileNotFoundException("No audio player was found."),
                $"Cannot play \"{filePath}\": install pipewire-pulse, pulseaudio-utils or alsa-utils.");

            if (deleteAfterwards) TryDelete(filePath);
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new(executable) { UseShellExecute = false, RedirectStandardError = true };

            // ffplay is a video player first; make it behave like a sound effect player.
            if (Path.GetFileName(executable) == "ffplay")
            {
                startInfo.ArgumentList.Add("-nodisp");
                startInfo.ArgumentList.Add("-autoexit");
                startInfo.ArgumentList.Add("-loglevel");
                startInfo.ArgumentList.Add("quiet");
            }

            startInfo.ArgumentList.Add(filePath);

            Process? process = Process.Start(startInfo);

            if (process == null)
            {
                if (deleteAfterwards) TryDelete(filePath);
                return;
            }

            if (deleteAfterwards)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    TryDelete(filePath);
                    process.Dispose();
                };
            }
            else
            {
                process.Dispose();
            }
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Playing \"{filePath}\" failed.");
            if (deleteAfterwards) TryDelete(filePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A sound file left behind in the temp directory is not worth surfacing.
        }
    }
}

/// <summary>Facts about the running distribution, for the About window and debug logs.</summary>
public static class SystemInformation
{
    private static readonly Lazy<string> productName = new(ReadProductName);

    /// <summary>The distribution's own name, e.g. "Fedora Linux 42 (Workstation Edition)".</summary>
    public static string ProductName => productName.Value;

    public static string KernelVersion => Environment.OSVersion.VersionString;

    public static string SessionDescription =>
        $"{(LinuxEnvironment.IsWayland ? "Wayland" : "non-Wayland")} session" +
        (string.IsNullOrEmpty(LinuxEnvironment.DesktopName) ? "" : $" ({LinuxEnvironment.DesktopName})");

    private static string ReadProductName()
    {
        foreach (string path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                if (!File.Exists(path)) continue;

                foreach (string line in File.ReadLines(path))
                {
                    if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;

                    return line["PRETTY_NAME=".Length..].Trim().Trim('"');
                }
            }
            catch
            {
                // Fall through to the generic description below.
            }
        }

        return "Linux";
    }
}
