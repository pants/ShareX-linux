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

using Tmds.DBus.Protocol;

namespace ShareX.Linux.Portals;

/// <summary>A colour returned by the portal colour picker, in the 0-1 range the portal uses.</summary>
public readonly record struct PortalColor(double Red, double Green, double Blue)
{
    public byte R => ToByte(Red);
    public byte G => ToByte(Green);
    public byte B => ToByte(Blue);

    private static byte ToByte(double component) => (byte)Math.Clamp(Math.Round(component * 255.0), 0, 255);
}

/// <summary>
/// org.freedesktop.portal.Screenshot. The compositor owns the pixels on Wayland, so this is the
/// only sanctioned way to grab the screen; the portal writes a PNG and hands back its URI.
/// </summary>
public sealed class ScreenshotPortal
{
    public const string InterfaceName = "org.freedesktop.portal.Screenshot";

    private readonly PortalConnection connection;

    public ScreenshotPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    public Task<uint> GetVersionAsync(CancellationToken cancellationToken = default) =>
        connection.GetVersionAsync(InterfaceName, cancellationToken);

    /// <summary>
    /// Takes a screenshot and returns the path of the PNG the portal produced. With
    /// <paramref name="interactive"/> the portal shows its own picker (area, window or screen);
    /// otherwise the whole desktop is captured, subject to the user's standing permission.
    /// </summary>
    public async Task<string> CaptureAsync(bool interactive = false, bool modal = true, string parentWindow = "",
        CancellationToken cancellationToken = default)
    {
        PortalResponse response = await connection.CallRequestAsync(InterfaceName, "Screenshot", "sa{sv}",
            (ref MessageWriter writer) => writer.WriteString(parentWindow),
            (ref MessageWriter writer) =>
            {
                writer.AddBool("interactive", interactive);
                writer.AddBool("modal", modal);
            },
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled) throw new PortalCancelledException("The screenshot was cancelled.");
        if (!response.Success) throw new PortalException($"The screenshot portal returned {response.Code}.");

        string? uri = response.GetString("uri");

        if (string.IsNullOrEmpty(uri)) throw new PortalException("The screenshot portal did not return an image.");

        return UriToPath(uri);
    }

    /// <summary>Asks the compositor to let the user pick a colour from anywhere on screen.</summary>
    public async Task<PortalColor> PickColorAsync(string parentWindow = "", CancellationToken cancellationToken = default)
    {
        PortalResponse response = await connection.CallRequestAsync(InterfaceName, "PickColor", "sa{sv}",
            (ref MessageWriter writer) => writer.WriteString(parentWindow), null,
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled) throw new PortalCancelledException("Colour picking was cancelled.");
        if (!response.Success) throw new PortalException($"The colour picker returned {response.Code}.");

        VariantValue? color = response.Get("color");

        if (color is not { } value || value.Count != 3)
        {
            throw new PortalException("The colour picker did not return a colour.");
        }

        return new PortalColor(value.GetItem(0).GetDouble(), value.GetItem(1).GetDouble(), value.GetItem(2).GetDouble());
    }

    internal static string UriToPath(string uri) =>
        uri.StartsWith("file://", StringComparison.Ordinal) ? new Uri(uri).LocalPath : uri;
}
