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
using System.Drawing;
using System.Globalization;
using SkiaSharp;

namespace ShareX.Linux.Capture;

/// <summary>
/// Screen capture through wlr-screencopy, using the grim command-line tool.
/// </summary>
/// <remarks>
/// wlroots-based compositors - Hyprland, sway, river, Wayfire and friends - expose
/// zwlr_screencopy_manager_v1, which lets a client grab the screen without going through a
/// portal. There is no permission dialog because the compositor already decided that clients on
/// its own socket may do this. It is also considerably faster than the portal round trip, and it
/// can grab a sub-rectangle directly instead of capturing everything and cropping.
///
/// GNOME and KDE do not implement the protocol, so grim fails there and
/// <see cref="WaylandScreenCapture"/> falls back to the portal.
/// </remarks>
public sealed class GrimScreenCaptureBackend : IScreenCaptureBackend
{
    private const string Executable = "grim";

    /// <summary>PNG compression costs more time than the capture itself, so ask for none.</summary>
    private const string CompressionLevel = "0";

    private static readonly Lazy<string?> executablePath = new(() => LinuxEnvironment.FindExecutable(Executable));

    /// <summary>Set once grim has actually failed, so a compositor without the protocol is only tried once.</summary>
    private bool unsupported;

    public string Name => "wlr-screencopy (grim)";

    public bool IsAvailable => !unsupported && executablePath.Value != null && LinuxEnvironment.IsWayland;

    /// <summary>True when the compositor is one that is known to implement wlr-screencopy.</summary>
    public static bool IsLikelySupported =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")) ||
        LinuxEnvironment.CompositorName is "hyprland" or "sway" or "river" or "wayfire" or "wlroots" or "mango";

    public SKBitmap? CaptureDesktop() => Run(null);

    public SKBitmap? CaptureRectangle(Rectangle bounds) =>
        bounds.Width <= 0 || bounds.Height <= 0
            ? null
            : Run(FormatGeometry(bounds));

    /// <summary>
    /// grim has no picker of its own; ShareX draws its own region overlay, and window picking
    /// goes through the compositor instead.
    /// </summary>
    public SKBitmap? CaptureInteractive() => null;

    /// <summary>The geometry syntax grim expects: "x,y widthxheight".</summary>
    private static string FormatGeometry(Rectangle bounds) =>
        string.Format(CultureInfo.InvariantCulture, "{0},{1} {2}x{3}",
            bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private SKBitmap? Run(string? geometry)
    {
        string? executable = executablePath.Value;

        if (executable == null) return null;

        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (geometry != null)
        {
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(geometry);
        }

        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("png");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(CompressionLevel);
        startInfo.ArgumentList.Add("-"); // write the image to standard output

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Starting grim failed.");

            using MemoryStream output = new();

            // Read the image while grim is still running so a large capture cannot fill the pipe.
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(output);

            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("grim did not finish in time.");
            }

            string error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 || output.Length == 0)
            {
                // A compositor without wlr-screencopy fails every time; stop trying it.
                unsupported = true;
                throw new InvalidOperationException(
                    $"grim exited with code {process.ExitCode}: {error.Trim()}");
            }

            return WaylandScreenCapture.DecodeImage(output.GetBuffer().AsSpan(0, (int)output.Length));
        }
        catch (Exception exception)
        {
            WaylandScreenCapture.ExceptionLogger?.Invoke(exception,
                $"Capturing with grim failed{(unsupported ? "; this compositor does not support wlr-screencopy, falling back to the portal" : "")}.");
            return null;
        }
    }
}
