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
using System.Text;

namespace ShareX.Linux.Desktop;

/// <summary>
/// The Wayland clipboard, through wl-copy and wl-paste.
/// </summary>
/// <remarks>
/// A clipboard owner has to stay alive to serve the data it offered, and it needs a surface to
/// own the selection in the first place. ShareX spends most of its life as a tray icon with no
/// window at all, which leaves a toolkit clipboard with nothing to own the selection with: the
/// copy reports success and the data never arrives. wl-copy sidesteps both problems by forking
/// a tiny process that holds the offer, which is what the other Wayland capture tools do, and
/// it keeps working after ShareX exits.
///
/// This also covers running under XWayland, where wl-copy's offer is bridged back to X11
/// clients, so it is preferred whenever a Wayland display exists regardless of the toolkit's
/// own windowing backend.
/// </remarks>
public static class WaylandClipboard
{
    public const string TextType = "text/plain;charset=utf-8";
    public const string ImageType = "image/png";
    public const string UriListType = "text/uri-list";
    public const string HtmlType = "text/html";

    private static readonly Lazy<string?> copyPath = new(() => LinuxEnvironment.FindExecutable("wl-copy"));
    private static readonly Lazy<string?> pastePath = new(() => LinuxEnvironment.FindExecutable("wl-paste"));

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>True when a Wayland display is present and the wl-clipboard tools are installed.</summary>
    public static bool IsAvailable =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        copyPath.Value != null && pastePath.Value != null;

    /// <summary>Explains what to install when the clipboard cannot be used.</summary>
    public static string UnavailableReason =>
        "Copying needs the wl-clipboard package (wl-copy and wl-paste).";

    public static bool SetText(string text) => Copy(TextType, Encoding.UTF8.GetBytes(text ?? ""));

    public static bool SetImage(byte[] png) => Copy(ImageType, png);

    public static bool SetHtml(string html) => Copy(HtmlType, Encoding.UTF8.GetBytes(html ?? ""));

    /// <summary>Puts file paths on the clipboard in the form file managers expect.</summary>
    public static bool SetFiles(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return false;

        StringBuilder uriList = new();

        foreach (string path in paths)
        {
            uriList.Append(new Uri(Path.GetFullPath(path)).AbsoluteUri).Append('\n');
        }

        return Copy(UriListType, Encoding.UTF8.GetBytes(uriList.ToString()));
    }

    public static bool Clear()
    {
        string? executable = copyPath.Value;

        if (executable == null) return false;

        try
        {
            ProcessStartInfo startInfo = new(executable) { UseShellExecute = false, RedirectStandardError = true };
            startInfo.ArgumentList.Add("--clear");

            using Process? process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Clearing the clipboard failed.");
            return false;
        }
    }

    public static string? GetText() =>
        Paste(TextType) is { } data ? Encoding.UTF8.GetString(data).TrimEnd('\n') : null;

    public static byte[]? GetImage() => Paste(ImageType);

    public static IReadOnlyList<string> GetFiles()
    {
        byte[]? data = Paste(UriListType);

        if (data == null) return [];

        List<string> paths = [];

        foreach (string line in Encoding.UTF8.GetString(data)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;

            try
            {
                Uri uri = new(line);
                if (uri.IsFile) paths.Add(uri.LocalPath);
            }
            catch (UriFormatException)
            {
                // Not every line of a uri-list is a URI; skip what cannot be parsed.
            }
        }

        return paths;
    }

    /// <summary>The MIME types currently on offer, which is how the formats are tested.</summary>
    public static IReadOnlyList<string> GetTypes()
    {
        string? output = RunPaste(["--list-types"], out _);

        return string.IsNullOrWhiteSpace(output)
            ? []
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static bool HasType(string mimeType) =>
        GetTypes().Any(type => type.StartsWith(mimeType, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the clipboard holds something that decodes as an image.</summary>
    public static bool HasImage() =>
        GetTypes().Any(type => type.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    private static bool Copy(string mimeType, byte[] data)
    {
        string? executable = copyPath.Value;

        if (executable == null || data == null) return false;

        try
        {
            ProcessStartInfo startInfo = new(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add(mimeType);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Starting wl-copy failed.");

            using (Stream input = process.StandardInput.BaseStream)
            {
                input.Write(data, 0, data.Length);
            }

            // wl-copy reads the data, forks a process to hold the offer and then returns, so
            // this waits only for the hand-off and not for the lifetime of the clipboard.
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("wl-copy did not finish in time.");
            }

            if (process.ExitCode == 0) return true;

            throw new InvalidOperationException(
                $"wl-copy exited with code {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Copying {mimeType} to the clipboard failed.");
            return false;
        }
    }

    private static byte[]? Paste(string mimeType)
    {
        string? executable = pastePath.Value;

        if (executable == null) return null;

        try
        {
            ProcessStartInfo startInfo = new(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("--no-newline");
            startInfo.ArgumentList.Add("--type");
            startInfo.ArgumentList.Add(mimeType);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Starting wl-paste failed.");

            using MemoryStream output = new();
            process.StandardOutput.BaseStream.CopyTo(output);

            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // A non-zero exit just means the clipboard holds nothing of that type.
            return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : null;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Reading {mimeType} from the clipboard failed.");
            return null;
        }
    }

    private static string? RunPaste(IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = -1;
        string? executable = pastePath.Value;

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

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Starting wl-paste failed.");

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            exitCode = process.ExitCode;

            return output;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Listing the clipboard's types failed.");
            return null;
        }
    }
}
