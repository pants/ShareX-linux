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

using System.Drawing;
using ShareX.Linux.Desktop;
using ShareX.Linux.Portals;
using SkiaSharp;

namespace ShareX.Linux.Capture;

/// <summary>
/// Screen capture for Wayland sessions.
/// </summary>
/// <remarks>
/// Two ways of getting at the screen exist, and which one works depends on the compositor:
///
/// wlroots compositors (Hyprland, sway, river, Wayfire) implement zwlr_screencopy, which a
/// client may use directly. It is fast and asks the user nothing, because the compositor has
/// already decided its own clients may capture. ShareX prefers it.
///
/// GNOME and KDE do not implement that protocol, and expect xdg-desktop-portal.Screenshot
/// instead, where the compositor renders the image after asking the user for permission.
///
/// The backends are tried in order and a failing one is skipped from then on, so a session
/// whose portal configuration is broken still captures through the direct path and vice versa.
/// </remarks>
public static class WaylandScreenCapture
{
    private static readonly SemaphoreSlim captureLock = new(1, 1);
    private static readonly GrimScreenCaptureBackend grim = new();
    private static readonly PortalScreenCaptureBackend portal = new();

    /// <summary>Set by the host application so capture problems reach the normal ShareX log.</summary>
    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>Set by the host application so ShareX can report which capture path is in use.</summary>
    public static Action<string>? DiagnosticLogger { get; set; }

    /// <summary>
    /// The backends in the order they are tried. grim comes first: it needs no permission and
    /// can crop, so it is both faster and quieter when the compositor supports it.
    /// </summary>
    private static IEnumerable<IScreenCaptureBackend> Backends
    {
        get
        {
            if (grim.IsAvailable) yield return grim;
            if (portal.IsAvailable) yield return portal;
        }
    }

    /// <summary>A short description of how ShareX will capture, for the About window and the log.</summary>
    public static string DescribeBackend() =>
        Backends.FirstOrDefault()?.Name ?? "no screen capture backend is available";

    /// <summary>Captures the whole desktop across every output.</summary>
    public static SKBitmap? CaptureDesktop()
    {
        captureLock.Wait();

        try
        {
            foreach (IScreenCaptureBackend backend in Backends)
            {
                SKBitmap? bitmap = backend.CaptureDesktop();

                if (bitmap != null) return bitmap;
            }

            ReportNoBackend();
            return null;
        }
        finally
        {
            captureLock.Release();
        }
    }

    /// <summary>Captures the part of the desktop covered by <paramref name="bounds"/>.</summary>
    public static SKBitmap? CaptureRectangle(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        captureLock.Wait();

        try
        {
            foreach (IScreenCaptureBackend backend in Backends)
            {
                // A backend that can crop for us avoids capturing the whole desktop first.
                SKBitmap? cropped = backend.CaptureRectangle(bounds);

                if (cropped != null) return cropped;

                using SKBitmap? desktop = backend.CaptureDesktop();

                if (desktop != null) return Crop(desktop, bounds, GetDesktopOrigin());
            }

            ReportNoBackend();
            return null;
        }
        finally
        {
            captureLock.Release();
        }
    }

    /// <summary>
    /// Captures a single window by asking the compositor which one the user wants.
    /// </summary>
    /// <remarks>
    /// On Hyprland the window's geometry comes from its IPC socket and the pixels from a plain
    /// region capture, which also recovers the window's title and class for the filename
    /// patterns. Elsewhere the portal's interactive mode shows the compositor's own picker.
    /// </remarks>
    public static SKBitmap? CaptureWindow(out WindowDetails? window)
    {
        window = null;

        if (HyprlandCompositor.IsRunning)
        {
            HyprlandWindow? active = HyprlandCompositor.GetActiveWindow();

            if (active != null)
            {
                window = new WindowDetails(active.Title, active.ClassName, active.Bounds);

                SKBitmap? bitmap = CaptureRectangle(active.Bounds);

                if (bitmap != null) return bitmap;
            }
        }

        captureLock.Wait();

        try
        {
            if (portal.IsAvailable)
            {
                SKBitmap? bitmap = portal.CaptureInteractive();

                if (bitmap != null) return bitmap;
            }
        }
        finally
        {
            captureLock.Release();
        }

        ReportNoBackend();
        return null;
    }

    /// <summary>Asks the compositor to let the user pick a colour. Returns null when cancelled.</summary>
    public static Color? PickColor()
    {
        try
        {
            PortalColor color = new ScreenshotPortal().PickColorAsync().GetAwaiter().GetResult();
            return Color.FromArgb(color.R, color.G, color.B);
        }
        catch (PortalCancelledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "The colour picker portal request failed.");
            return null;
        }
    }

    /// <summary>
    /// Crops <paramref name="source"/> to <paramref name="bounds"/>, where bounds are in desktop
    /// coordinates and the image's top-left corner sits at <paramref name="origin"/>.
    /// </summary>
    public static SKBitmap? Crop(SKBitmap source, Rectangle bounds, Point origin)
    {
        SKRectI region = SKRectI.Create(bounds.X - origin.X, bounds.Y - origin.Y, bounds.Width, bounds.Height);
        region = SKRectI.Intersect(region, SKRectI.Create(0, 0, source.Width, source.Height));

        if (region.Width <= 0 || region.Height <= 0) return null;

        SKBitmap cropped = new(region.Width, region.Height, source.ColorType, source.AlphaType);

        return source.ExtractSubset(cropped, region) ? cropped : DisposeAndReturnNull(cropped);
    }

    /// <summary>
    /// Top-left corner of the desktop in the compositor's coordinate space. Wayland compositors
    /// lay outputs out starting at the origin, so this is 0,0; it exists so cropping stays
    /// correct if a compositor ever reports otherwise.
    /// </summary>
    public static Point GetDesktopOrigin() => Point.Empty;

    /// <summary>Decodes a captured image into the layout Skia and Avalonia both want.</summary>
    internal static SKBitmap? DecodeImage(ReadOnlySpan<byte> encoded)
    {
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec codec = SKCodec.Create(data);

        if (codec == null) return null;

        SKImageInfo info = codec.Info
            .WithColorType(SKColorType.Bgra8888)
            .WithAlphaType(SKAlphaType.Premul);

        SKBitmap bitmap = new(info);

        return codec.GetPixels(info, bitmap.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput
            ? bitmap
            : DisposeAndReturnNull(bitmap);
    }

    private static void ReportNoBackend() =>
        ExceptionLogger?.Invoke(
            new PlatformNotSupportedException("No screen capture backend succeeded."),
            "Screen capture failed. Install grim for a wlroots compositor such as Hyprland or sway, " +
            "or make sure XDG_CURRENT_DESKTOP names your compositor so xdg-desktop-portal picks a " +
            "backend that can take screenshots.");

    private static SKBitmap? DisposeAndReturnNull(SKBitmap bitmap)
    {
        bitmap.Dispose();
        return null;
    }
}

/// <summary>What the compositor was able to tell ShareX about a captured window.</summary>
public sealed record WindowDetails(string Title, string ClassName, Rectangle Bounds);
