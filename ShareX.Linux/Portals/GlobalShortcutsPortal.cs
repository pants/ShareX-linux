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

/// <summary>A shortcut ShareX asks the compositor to reserve on its behalf.</summary>
public sealed class GlobalShortcutDefinition
{
    public required string Id { get; init; }

    /// <summary>Human readable text the compositor shows in its own shortcut editor.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Preferred accelerator in the freedesktop shortcuts syntax, for example "CTRL+SHIFT+p".
    /// The compositor is free to ignore it; the user's binding always wins.
    /// </summary>
    public string? PreferredTrigger { get; init; }
}

public sealed class GlobalShortcutActivatedEventArgs : EventArgs
{
    public required string ShortcutId { get; init; }
    public ulong Timestamp { get; init; }
}

/// <summary>
/// org.freedesktop.portal.GlobalShortcuts. Wayland has no global hotkey API for clients, so the
/// compositor owns the bindings: ShareX registers the actions it supports and is told when the
/// user triggers one. The user rebinds them in their desktop's own shortcut settings.
/// </summary>
public sealed class GlobalShortcutsPortal : IAsyncDisposable
{
    public const string InterfaceName = "org.freedesktop.portal.GlobalShortcuts";

    private readonly PortalConnection connection;
    private readonly List<IDisposable> subscriptions = [];
    private string? sessionHandle;
    private bool disposed;

    public GlobalShortcutsPortal(PortalConnection? connection = null) =>
        this.connection = connection ?? PortalConnection.Instance;

    /// <summary>Raised on a thread pool thread when the user presses one of the registered shortcuts.</summary>
    public event EventHandler<GlobalShortcutActivatedEventArgs>? Activated;

    /// <summary>Raised when the compositor reports that the bindings changed, e.g. the user rebound one.</summary>
    public event EventHandler? ShortcutsChanged;

    public bool IsActive => sessionHandle != null;

    public Task<uint> GetVersionAsync(CancellationToken cancellationToken = default) =>
        connection.GetVersionAsync(InterfaceName, cancellationToken);

    public async Task<bool> IsSupportedAsync(CancellationToken cancellationToken = default) =>
        await GetVersionAsync(cancellationToken).ConfigureAwait(false) > 0;

    /// <summary>Creates the shortcuts session and starts listening for activations.</summary>
    public async Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (sessionHandle != null) return;

        string token = connection.CreateToken("shortcuts_session");

        PortalResponse created = await connection.CallRequestAsync(InterfaceName, "CreateSession", "a{sv}", null,
            (ref MessageWriter writer) => writer.AddString("session_handle_token", token),
            cancellationToken).ConfigureAwait(false);

        if (!created.Success) throw new PortalException($"Creating the global shortcuts session failed ({created.Code}).");

        string? handle = created.GetString("session_handle");

        if (string.IsNullOrEmpty(handle))
        {
            throw new PortalException("The global shortcuts portal did not return a session handle.");
        }

        sessionHandle = handle;

        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        subscriptions.Add(await bus.WatchSignalAsync(PortalConnection.BusName, PortalConnection.ObjectPath,
            InterfaceName, "Activated",
            static (Message message, object? state) => ReadActivation(message),
            (Notification<(string Session, string Id, ulong Timestamp)> notification) =>
            {
                if (notification.IsCompletion || !notification.HasValue) return;
                if (!string.Equals(notification.Value.Session, sessionHandle, StringComparison.Ordinal)) return;

                Activated?.Invoke(this, new GlobalShortcutActivatedEventArgs
                {
                    ShortcutId = notification.Value.Id,
                    Timestamp = notification.Value.Timestamp
                });
            },
            ObserverFlags.None, false, null).ConfigureAwait(false));

        subscriptions.Add(await bus.WatchSignalAsync(PortalConnection.BusName, PortalConnection.ObjectPath,
            InterfaceName, "ShortcutsChanged",
            static (Message message, object? state) => message.GetBodyReader().ReadObjectPathAsString(),
            (Notification<string> notification) =>
            {
                if (!notification.IsCompletion && notification.HasValue &&
                    string.Equals(notification.Value, sessionHandle, StringComparison.Ordinal))
                {
                    ShortcutsChanged?.Invoke(this, EventArgs.Empty);
                }
            },
            ObserverFlags.None, false, null).ConfigureAwait(false));
    }

    /// <summary>
    /// Registers the given shortcuts with the compositor. On the first call the user is asked to
    /// confirm; afterwards the compositor remembers the grant for this application id.
    /// </summary>
    public async Task BindAsync(IReadOnlyList<GlobalShortcutDefinition> shortcuts, string parentWindow = "",
        CancellationToken cancellationToken = default)
    {
        if (sessionHandle == null) await StartSessionAsync(cancellationToken).ConfigureAwait(false);

        string handle = sessionHandle!;

        PortalResponse response = await connection.CallRequestAsync(InterfaceName, "BindShortcuts", "oa(sa{sv})sa{sv}",
            (ref MessageWriter writer) =>
            {
                writer.WriteObjectPath(handle);
                WriteShortcuts(ref writer, shortcuts);
                writer.WriteString(parentWindow);
            }, null, cancellationToken).ConfigureAwait(false);

        if (response.Cancelled) throw new PortalCancelledException("Binding global shortcuts was cancelled.");
        if (!response.Success) throw new PortalException($"Binding global shortcuts failed ({response.Code}).");
    }

    /// <summary>Returns the shortcut ids the compositor currently has bound, with their triggers.</summary>
    public async Task<IReadOnlyList<(string Id, string? Trigger, string? Description)>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (sessionHandle == null) await StartSessionAsync(cancellationToken).ConfigureAwait(false);

        string handle = sessionHandle!;

        PortalResponse response = await connection.CallRequestAsync(InterfaceName, "ListShortcuts", "oa{sv}",
            (ref MessageWriter writer) => writer.WriteObjectPath(handle), null, cancellationToken).ConfigureAwait(false);

        List<(string, string?, string?)> result = [];

        if (!response.Success || response.Get("shortcuts") is not { } shortcuts) return result;

        for (int i = 0; i < shortcuts.Count; i++)
        {
            VariantValue entry = shortcuts.GetItem(i);
            if (entry.Count < 2) continue;

            Dictionary<string, VariantValue> properties = entry.GetItem(1).GetDictionary<string, VariantValue>();

            result.Add((entry.GetItem(0).GetString(),
                properties.TryGetValue("trigger_description", out VariantValue trigger) ? trigger.GetString() : null,
                properties.TryGetValue("description", out VariantValue description) ? description.GetString() : null));
        }

        return result;
    }

    private static void WriteShortcuts(ref MessageWriter writer, IReadOnlyList<GlobalShortcutDefinition> shortcuts)
    {
        ArrayStart array = writer.WriteArrayStart(DBusType.Struct);

        foreach (GlobalShortcutDefinition shortcut in shortcuts)
        {
            writer.WriteStructureStart();
            writer.WriteString(shortcut.Id);

            ArrayStart properties = writer.WriteDictionaryStart();
            writer.AddString("description", shortcut.Description);
            if (!string.IsNullOrEmpty(shortcut.PreferredTrigger)) writer.AddString("preferred_trigger", shortcut.PreferredTrigger);
            writer.WriteDictionaryEnd(properties);
        }

        writer.WriteArrayEnd(array);
    }

    private static (string, string, ulong) ReadActivation(Message message)
    {
        Reader reader = message.GetBodyReader();
        string session = reader.ReadObjectPathAsString();
        string id = reader.ReadString();
        ulong timestamp = reader.ReadUInt64();
        return (session, id, timestamp);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        foreach (IDisposable subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();

        if (sessionHandle != null)
        {
            string handle = sessionHandle;
            sessionHandle = null;
            await connection.CloseSessionAsync(handle).ConfigureAwait(false);
        }
    }
}
