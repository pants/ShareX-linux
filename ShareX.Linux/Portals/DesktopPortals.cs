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
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace ShareX.Linux.Portals;

/// <summary>org.freedesktop.portal.OpenURI: hands a URI, file or folder to the user's own handler.</summary>
public sealed class OpenUriPortal
{
    public const string InterfaceName = "org.freedesktop.portal.OpenURI";

    private readonly PortalConnection connection;

    public OpenUriPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    public async Task OpenUriAsync(string uri, string parentWindow = "", CancellationToken cancellationToken = default)
    {
        await connection.CallRequestAsync(InterfaceName, "OpenURI", "ssa{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteString(parentWindow);
            writer.WriteString(uri);
        },
        (ref MessageWriter writer) => writer.AddBool("writable", false),
        cancellationToken).ConfigureAwait(false);
    }

    public Task OpenFileAsync(string path, string parentWindow = "", CancellationToken cancellationToken = default) =>
        OpenWithHandleAsync("OpenFile", path, parentWindow, cancellationToken);

    /// <summary>Opens the folder containing <paramref name="path"/>, selecting the item when the file manager can.</summary>
    public Task ShowInFolderAsync(string path, string parentWindow = "", CancellationToken cancellationToken = default) =>
        OpenWithHandleAsync("OpenDirectory", path, parentWindow, cancellationToken);

    private async Task OpenWithHandleAsync(string member, string path, string parentWindow,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        await connection.CallRequestAsync(InterfaceName, member, "sha{sv}", (ref MessageWriter writer) =>
        {
            writer.WriteString(parentWindow);
            writer.WriteHandle(handle);
        }, null, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>org.freedesktop.portal.Settings: the desktop's shared preferences, notably the colour scheme.</summary>
public sealed class SettingsPortal : IDisposable
{
    public const string InterfaceName = "org.freedesktop.portal.Settings";
    public const string AppearanceNamespace = "org.freedesktop.appearance";

    private readonly PortalConnection connection;
    private IDisposable? subscription;

    public SettingsPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    /// <summary>Raised when any watched setting changes. The argument is "namespace/key".</summary>
    public event EventHandler<string>? SettingChanged;

    /// <summary>0 = no preference, 1 = prefer dark, 2 = prefer light.</summary>
    public async Task<uint> GetColorSchemeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            VariantValue value = await ReadOneAsync(AppearanceNamespace, "color-scheme", cancellationToken).ConfigureAwait(false);
            return value.Type == VariantValueType.UInt32 ? value.GetUInt32() : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task<VariantValue> ReadOneAsync(string settingsNamespace, string key,
        CancellationToken cancellationToken = default)
    {
        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        uint version = await connection.GetVersionAsync(InterfaceName, cancellationToken).ConfigureAwait(false);
        string member = version >= 2 ? "ReadOne" : "Read";

        MessageBuffer message = BuildReadMessage(bus, member, settingsNamespace, key);

        VariantValue value = await bus.CallMethodAsync(message,
            static (Message reply, object? state) => reply.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);

        // The v1 "Read" method wraps the value in a second variant.
        return member == "Read" && value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;
    }

    public async Task StartWatchingAsync(CancellationToken cancellationToken = default)
    {
        if (subscription != null) return;

        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        subscription = await bus.WatchSignalAsync(PortalConnection.BusName, PortalConnection.ObjectPath,
            InterfaceName, "SettingChanged",
            static (Message message, object? state) =>
            {
                Reader reader = message.GetBodyReader();
                return $"{reader.ReadString()}/{reader.ReadString()}";
            },
            (Notification<string> notification) =>
            {
                if (!notification.IsCompletion && notification.HasValue)
                {
                    SettingChanged?.Invoke(this, notification.Value);
                }
            },
            ObserverFlags.None, false, null).ConfigureAwait(false);
    }

    private static MessageBuffer BuildReadMessage(DBusConnection bus, string member, string settingsNamespace, string key)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(PortalConnection.BusName, PortalConnection.ObjectPath, InterfaceName,
                member, "ss", MessageFlags.None);
            writer.WriteString(settingsNamespace);
            writer.WriteString(key);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
    }
}

[Flags]
public enum InhibitFlags : uint
{
    Logout = 1,
    UserSwitch = 2,
    Suspend = 4,
    Idle = 8
}

/// <summary>
/// org.freedesktop.portal.Inhibit: keeps the screen awake. ShareX holds one of these for the
/// duration of a screen recording so the lock screen does not cut the capture short.
/// </summary>
public sealed class InhibitPortal : IAsyncDisposable
{
    public const string InterfaceName = "org.freedesktop.portal.Inhibit";

    private readonly PortalConnection connection;
    private string? requestHandle;

    public InhibitPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    public async Task InhibitAsync(string reason, InhibitFlags flags = InhibitFlags.Idle,
        string parentWindow = "", CancellationToken cancellationToken = default)
    {
        if (requestHandle != null) return;

        try
        {
            DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

            string token = connection.CreateToken("inhibit");
            requestHandle = connection.GetRequestPath(token);

            // Inhibit keeps running until its Request handle is closed, so no Response is awaited.
            MessageBuffer message = BuildInhibitMessage(bus, parentWindow, flags, reason, token);
            await bus.CallMethodAsync(message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            requestHandle = null;
            Debug.WriteLine($"Inhibiting idle failed: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (requestHandle == null) return;

        string handle = requestHandle;
        requestHandle = null;

        try
        {
            DBusConnection bus = await connection.GetConnectionAsync().ConfigureAwait(false);
            await bus.CallMethodAsync(BuildCloseMessage(bus, handle)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Releasing the idle inhibitor failed: {exception.Message}");
        }
    }

    private static MessageBuffer BuildInhibitMessage(DBusConnection bus, string parentWindow, InhibitFlags flags,
        string reason, string token)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(PortalConnection.BusName, PortalConnection.ObjectPath, InterfaceName,
                "Inhibit", "sua{sv}", MessageFlags.None);
            writer.WriteString(parentWindow);
            writer.WriteUInt32((uint)flags);

            ArrayStart options = writer.WriteDictionaryStart();
            writer.AddString("handle_token", token);
            writer.AddString("reason", reason);
            writer.WriteDictionaryEnd(options);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static MessageBuffer BuildCloseMessage(DBusConnection bus, string requestPath)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(PortalConnection.BusName, requestPath, PortalConnection.RequestInterface,
                "Close", null, MessageFlags.None);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}

/// <summary>
/// org.freedesktop.portal.Print: hands a rendered document to the desktop's own print dialog,
/// which knows about the user's CUPS queues, paper sizes and duplex settings.
/// </summary>
public sealed class PrintPortal
{
    public const string InterfaceName = "org.freedesktop.portal.Print";

    private readonly PortalConnection connection;

    public PrintPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    public Task<uint> GetVersionAsync(CancellationToken cancellationToken = default) =>
        connection.GetVersionAsync(InterfaceName, cancellationToken);

    /// <summary>Opens the print dialog for a PDF file. Returns false when the user cancels.</summary>
    public async Task<bool> PrintPdfAsync(string pdfPath, string title, string parentWindow = "",
        CancellationToken cancellationToken = default)
    {
        using SafeFileHandle handle = File.OpenHandle(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        PortalResponse response = await connection.CallRequestAsync(InterfaceName, "Print", "ssha{sv}",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(parentWindow);
                writer.WriteString(title);
                writer.WriteHandle(handle);
            },
            (ref MessageWriter writer) => writer.AddBool("modal", true),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled) return false;
        if (!response.Success) throw new PortalException($"The print portal returned {response.Code}.");

        return true;
    }
}
