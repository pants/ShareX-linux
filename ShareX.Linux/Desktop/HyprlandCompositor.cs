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
using System.Text.Json;

namespace ShareX.Linux.Desktop;

/// <summary>A window as Hyprland describes it over its IPC socket.</summary>
public sealed record HyprlandWindow(string Title, string ClassName, Rectangle Bounds);

/// <summary>
/// Talks to Hyprland through hyprctl.
/// </summary>
/// <remarks>
/// Wayland gives clients no way to inspect other applications' windows, but a compositor is
/// free to offer that itself, and Hyprland does over its own IPC socket. Where it is running,
/// ShareX can name the window a capture came from - which restores the %pn and %t filename
/// patterns - and capture a specific window without a picker. Everywhere else these return null
/// and ShareX falls back to the compositor's own picker.
/// </remarks>
public static class HyprlandCompositor
{
    private static readonly Lazy<bool> running = new(() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")) &&
        LinuxEnvironment.HasExecutable("hyprctl"));

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    public static bool IsRunning => running.Value;

    /// <summary>The focused window, or null when nothing is focused or Hyprland is not running.</summary>
    public static HyprlandWindow? GetActiveWindow()
    {
        JsonElement? root = Query("activewindow");

        return root is { } element ? ReadWindow(element) : null;
    }

    /// <summary>Every mapped window, for a capture menu that lists them.</summary>
    public static IReadOnlyList<HyprlandWindow> GetWindows()
    {
        List<HyprlandWindow> windows = [];

        if (Query("clients") is not { ValueKind: JsonValueKind.Array } clients) return windows;

        foreach (JsonElement client in clients.EnumerateArray())
        {
            // Skip windows that are not on screen; they have nothing to capture.
            if (client.TryGetProperty("mapped", out JsonElement mapped) && !mapped.GetBoolean()) continue;
            if (client.TryGetProperty("hidden", out JsonElement hidden) && hidden.GetBoolean()) continue;

            HyprlandWindow? window = ReadWindow(client);

            if (window != null) windows.Add(window);
        }

        return windows;
    }

    private static HyprlandWindow? ReadWindow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        Rectangle bounds = ReadRectangle(element);

        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new HyprlandWindow(
            element.TryGetProperty("title", out JsonElement title) ? title.GetString() ?? "" : "",
            element.TryGetProperty("class", out JsonElement className) ? className.GetString() ?? "" : "",
            bounds);
    }

    /// <summary>Hyprland reports geometry as "at": [x, y] and "size": [width, height].</summary>
    private static Rectangle ReadRectangle(JsonElement element)
    {
        if (!element.TryGetProperty("at", out JsonElement at) || at.GetArrayLength() < 2) return Rectangle.Empty;
        if (!element.TryGetProperty("size", out JsonElement size) || size.GetArrayLength() < 2) return Rectangle.Empty;

        return new Rectangle(at[0].GetInt32(), at[1].GetInt32(), size[0].GetInt32(), size[1].GetInt32());
    }

    private static JsonElement? Query(string command)
    {
        if (!IsRunning) return null;

        try
        {
            string? output = LinuxEnvironment.RunCommand("hyprctl", "-j", command);

            if (string.IsNullOrWhiteSpace(output)) return null;

            using JsonDocument document = JsonDocument.Parse(output);

            // The document is disposed on return, so hand back a detached copy.
            return document.RootElement.Clone();
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Asking Hyprland for \"{command}\" failed.");
            Debug.WriteLine($"hyprctl {command} failed: {exception.Message}");
            return null;
        }
    }
}
