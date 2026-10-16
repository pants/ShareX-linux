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

/// <summary>A capture device ShareX can record from, with the identifier ffmpeg expects.</summary>
public sealed record MediaCaptureDevice(string Name, string Identifier)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates the microphones, monitor (loopback) sources and cameras available to the session.
/// Audio comes from PipeWire through its PulseAudio compatibility layer, which is what ffmpeg's
/// "pulse" input talks to; cameras are plain V4L2 nodes.
/// </summary>
public static class MediaCaptureDevices
{
    /// <summary>Audio inputs, including the ".monitor" sources that capture what is playing.</summary>
    public static IReadOnlyList<MediaCaptureDevice> GetAudioSources()
    {
        List<MediaCaptureDevice> devices = [];

        string? output = LinuxEnvironment.RunCommand("pactl", "list", "short", "sources");

        if (string.IsNullOrWhiteSpace(output)) return devices;

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // index <tab> name <tab> driver <tab> sample spec <tab> state
            string[] columns = line.Split('\t', StringSplitOptions.TrimEntries);

            if (columns.Length < 2 || string.IsNullOrEmpty(columns[1])) continue;

            devices.Add(new MediaCaptureDevice(DescribeAudioSource(columns[1]), columns[1]));
        }

        return devices;
    }

    /// <summary>Cameras exposed as V4L2 devices, in the order the kernel numbered them.</summary>
    public static IReadOnlyList<MediaCaptureDevice> GetVideoSources()
    {
        List<MediaCaptureDevice> devices = [];

        if (!Directory.Exists("/dev")) return devices;

        foreach (string path in Directory.GetFiles("/dev", "video*").OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = ReadV4L2Name(path) ?? Path.GetFileName(path);
            devices.Add(new MediaCaptureDevice(name, path));
        }

        return devices;
    }

    /// <summary>The source that records desktop audio, when the session exposes one.</summary>
    public static MediaCaptureDevice? GetDefaultMonitorSource() =>
        GetAudioSources().FirstOrDefault(x => x.Identifier.EndsWith(".monitor", StringComparison.Ordinal));

    private static string DescribeAudioSource(string identifier) =>
        identifier.EndsWith(".monitor", StringComparison.Ordinal)
            ? $"{identifier[..^".monitor".Length]} (desktop audio)"
            : identifier;

    /// <summary>Reads the friendly card name V4L2 publishes in sysfs, e.g. "Integrated Camera".</summary>
    private static string? ReadV4L2Name(string devicePath)
    {
        try
        {
            string node = Path.GetFileName(devicePath);
            string nameFile = $"/sys/class/video4linux/{node}/name";

            if (!File.Exists(nameFile)) return null;

            string name = File.ReadAllText(nameFile).Trim();

            return string.IsNullOrEmpty(name) ? null : $"{name} ({node})";
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Reading the V4L2 name of {devicePath} failed: {exception.Message}");
            return null;
        }
    }
}
