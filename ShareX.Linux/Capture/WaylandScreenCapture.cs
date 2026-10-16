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
using ShareX.Linux.Portals;
using SkiaSharp;

namespace ShareX.Linux.Capture;

/// <summary>
/// Screen capture for Wayland sessions.
/// </summary>
/// <remarks>
/// A Wayland client cannot read the framebuffer, so every grab goes through
/// org.freedesktop.portal.Screenshot: the compositor renders the image, writes it to a file and
/// hands back the URI. The user grants permission once per application. Everything ShareX offers -
/// a monitor, a saved region, the picker overlay - is a crop of that full-desktop image.
/// </remarks>
public static class WaylandScreenCapture
{
    private static readonly SemaphoreSlim captureLock = new(1, 1);

    /// <summary>Set by the host application so capture errors reach the normal ShareX log.</summary>
    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>Captures the whole desktop across every output.</summary>
    public static SKBitmap? CaptureDesktop() => Run(CaptureDesktopAsync());

    /// <summary>
    /// Captures through the compositor's own picker, letting the user point at a window or an area.
    /// This is the only way to capture a specific window on Wayland: clients cannot enumerate or
    /// address other applications' surfaces.
    /// </summary>
    public static SKBitmap? CaptureInteractive() => Run(CaptureInteractiveAsync());

    /// <summary>Captures the desktop and returns the part of it covered by <paramref name="bounds"/>.</summary>
    public static SKBitmap? CaptureRectangle(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        using SKBitmap? desktop = CaptureDesktop();

        return desktop == null ? null : Crop(desktop, bounds, GetDesktopOrigin());
    }

    public static async Task<SKBitmap?> CaptureDesktopAsync(CancellationToken cancellationToken = default)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string path = await new ScreenshotPortal().CaptureAsync(interactive: false, modal: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return LoadAndDelete(path);
        }
        catch (PortalCancelledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Capturing the desktop through the screenshot portal failed.");
            return null;
        }
        finally
        {
            captureLock.Release();
        }
    }

    public static async Task<SKBitmap?> CaptureInteractiveAsync(CancellationToken cancellationToken = default)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string path = await new ScreenshotPortal().CaptureAsync(interactive: true, modal: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return LoadAndDelete(path);
        }
        catch (PortalCancelledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "The interactive screenshot portal request failed.");
            return null;
        }
        finally
        {
            captureLock.Release();
        }
    }

    /// <summary>Asks the compositor for a colour anywhere on screen. Returns null when cancelled.</summary>
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
    /// Crops <paramml name="source"/> to <paramref name="bounds"/>, where bounds are in desktop
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
    /// lay outputs out starting at the origin, so this is 0,0; it exists so cropping stays correct
    /// if a compositor ever reports otherwise.
    /// </summary>
    public static Point GetDesktopOrigin() => Point.Empty;

    private static SKBitmap? LoadAndDelete(string path)
    {
        try
        {
            using SKCodec codec = SKCodec.Create(path);

            if (codec == null) return null;

            // Decode into a layout the rest of ShareX can hand straight to Skia and Avalonia.
            SKImageInfo info = codec.Info
                .WithColorType(SKColorType.Bgra8888)
                .WithAlphaType(SKAlphaType.Premul);

            SKBitmap bitmap = new(info);

            return codec.GetPixels(info, bitmap.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput
                ? bitmap
                : DisposeAndReturnNull(bitmap);
        }
        finally
        {
            DeleteIfTemporary(path);
        }
    }

    /// <summary>
    /// Removes the file the portal produced. The portal hands the image to us and expects the
    /// application to clean it up, but a user who pointed the portal at their Pictures folder
    /// wants to keep it, so only transient locations are touched.
    /// </summary>
    private static void DeleteIfTemporary(string path)
    {
        string[] transientRoots =
        [
            Path.GetTempPath(),
            LinuxEnvironment.RuntimeDir,
            LinuxEnvironment.CacheHome,
            "/tmp"
        ];

        try
        {
            string full = Path.GetFullPath(path);

            foreach (string root in transientRoots)
            {
                if (!string.IsNullOrEmpty(root) && full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
                {
                    File.Delete(full);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Removing the temporary screenshot \"{path}\" failed.");
        }
    }

    private static SKBitmap? DisposeAndReturnNull(SKBitmap bitmap)
    {
        bitmap.Dispose();
        return null;
    }

    /// <summary>
    /// Blocks on a portal call. Portal work never continues on the UI thread, so this cannot
    /// deadlock, but it does hold the calling thread while the compositor's dialog is up.
    /// </summary>
    internal static T? Run<T>(Task<T?> task) => task.GetAwaiter().GetResult();
}
