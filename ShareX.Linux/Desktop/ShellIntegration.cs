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
using ShareX.Linux.Portals;
using Tmds.DBus.Protocol;

namespace ShareX.Linux.Desktop;

/// <summary>
/// Opens files, folders and links with whatever the user has chosen to handle them.
/// </summary>
/// <remarks>
/// Three routes are tried in turn: the freedesktop FileManager1 interface, which every major file
/// manager implements and which can highlight a specific file; the OpenURI portal, which works
/// inside a sandbox; and finally xdg-open.
/// </remarks>
public static class ShellIntegration
{
    private const string FileManagerBusName = "org.freedesktop.FileManager1";
    private const string FileManagerObjectPath = "/org/freedesktop/FileManager1";

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>Opens the containing folder and highlights the file when the file manager can.</summary>
    public static void ShowFileInManager(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        string full = Path.GetFullPath(filePath);

        if (TryShowItemsOverDBus(full)) return;

        try
        {
            new OpenUriPortal().ShowInFolderAsync(full).GetAwaiter().GetResult();
            return;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Opening the containing folder through the portal failed.");
        }

        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory)) OpenFolder(directory);
    }

    public static void OpenFolder(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath)) return;

        OpenUri(new Uri(Path.GetFullPath(directoryPath)).AbsoluteUri);
    }

    public static void OpenFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        OpenUri(new Uri(Path.GetFullPath(filePath)).AbsoluteUri);
    }

    /// <summary>Hands a URI to the user's default handler.</summary>
    public static void OpenUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;

        try
        {
            new OpenUriPortal().OpenUriAsync(uri).GetAwaiter().GetResult();
            return;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Opening \"{uri}\" through the portal failed; trying xdg-open.");
        }

        TryXdgOpen(uri);
    }

    private static bool TryXdgOpen(string target)
    {
        string? xdgOpen = LinuxEnvironment.FindExecutable("xdg-open");

        if (xdgOpen == null) return false;

        try
        {
            ProcessStartInfo startInfo = new(xdgOpen) { UseShellExecute = false };
            startInfo.ArgumentList.Add(target);

            using Process? process = Process.Start(startInfo);
            return process != null;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"xdg-open could not open \"{target}\".");
            return false;
        }
    }

    /// <summary>
    /// Asks the running file manager to reveal the file. Returns false when no file manager
    /// claims the interface, which is normal on a bare window manager.
    /// </summary>
    private static bool TryShowItemsOverDBus(string filePath)
    {
        try
        {
            DBusConnection bus = PortalConnection.Instance.GetConnectionAsync().GetAwaiter().GetResult();

            bus.CallMethodAsync(BuildShowItemsMessage(bus, new Uri(filePath).AbsoluteUri)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"FileManager1.ShowItems failed: {exception.Message}");
            return false;
        }
    }

    private static MessageBuffer BuildShowItemsMessage(DBusConnection bus, string uri)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(FileManagerBusName, FileManagerObjectPath, FileManagerBusName,
                "ShowItems", "ass", MessageFlags.None);
            writer.WriteArray(new[] { uri });
            writer.WriteString(string.Empty); // startup id
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
