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

public enum DesktopNotificationPriority
{
    Low,
    Normal,
    High,
    Urgent
}

/// <summary>A button shown on a desktop notification.</summary>
public sealed record DesktopNotificationButton(string Label, string ActionId);

public sealed class DesktopNotificationActivatedEventArgs : EventArgs
{
    public required string NotificationId { get; init; }
    public required string ActionId { get; init; }
}

/// <summary>
/// org.freedesktop.portal.Notification, with a fallback to the long-standing
/// org.freedesktop.Notifications service when the portal is unavailable.
/// </summary>
public sealed class NotificationPortal : IDisposable
{
    public const string InterfaceName = "org.freedesktop.portal.Notification";

    private const string LegacyBusName = "org.freedesktop.Notifications";
    private const string LegacyObjectPath = "/org/freedesktop/Notifications";

    private readonly PortalConnection connection;
    private readonly List<IDisposable> subscriptions = [];
    private bool? portalAvailable;
    private bool disposed;

    public NotificationPortal(PortalConnection? connection = null) =>
        this.connection = connection ?? PortalConnection.Instance;

    /// <summary>Raised when the user clicks a notification or one of its buttons.</summary>
    public event EventHandler<DesktopNotificationActivatedEventArgs>? ActionInvoked;

    public async Task ShowAsync(string id, string title, string body,
        DesktopNotificationPriority priority = DesktopNotificationPriority.Normal,
        string? defaultAction = null, IReadOnlyList<DesktopNotificationButton>? buttons = null,
        string? iconPath = null, CancellationToken cancellationToken = default)
    {
        portalAvailable ??= await connection.GetVersionAsync(InterfaceName, cancellationToken).ConfigureAwait(false) > 0;

        if (portalAvailable == true)
        {
            // The portal takes the icon from our desktop entry, so iconPath only matters to the fallback.
            await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false);

            await connection.CallAsync(InterfaceName, "AddNotification", "sa{sv}", (ref MessageWriter writer) =>
            {
                writer.WriteString(id);

                ArrayStart notification = writer.WriteDictionaryStart();
                writer.AddString("title", title);
                writer.AddString("body", body);
                writer.AddString("priority", PriorityToString(priority));
                if (!string.IsNullOrEmpty(defaultAction)) writer.AddString("default-action", defaultAction);
                if (buttons is { Count: > 0 }) WriteButtons(ref writer, buttons);
                writer.WriteDictionaryEnd(notification);
            }, cancellationToken).ConfigureAwait(false);

            return;
        }

        await ShowLegacyAsync(title, body, iconPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task WithdrawAsync(string id, CancellationToken cancellationToken = default)
    {
        if (portalAvailable != true) return;

        try
        {
            await connection.CallAsync(InterfaceName, "RemoveNotification", "s",
                (ref MessageWriter writer) => writer.WriteString(id), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Withdrawing a notification the shell already dropped is not worth reporting.
        }
    }

    private async Task EnsureSubscribedAsync(CancellationToken cancellationToken)
    {
        if (subscriptions.Count > 0) return;

        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        subscriptions.Add(await bus.WatchSignalAsync(PortalConnection.BusName, PortalConnection.ObjectPath,
            InterfaceName, "ActionInvoked",
            static (Message message, object? state) =>
            {
                Reader reader = message.GetBodyReader();
                return (reader.ReadString(), reader.ReadString());
            },
            (Notification<(string Id, string Action)> notification) =>
            {
                if (!notification.HasValue) return;

                ActionInvoked?.Invoke(this, new DesktopNotificationActivatedEventArgs
                {
                    NotificationId = notification.Value.Id,
                    ActionId = notification.Value.Action
                });
            },
            ObserverFlags.None, false, null).ConfigureAwait(false));
    }

    private async Task ShowLegacyAsync(string title, string body, string? iconPath, CancellationToken cancellationToken)
    {
        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        await bus.CallMethodAsync(BuildLegacyMessage(bus, title, body, iconPath)).ConfigureAwait(false);
    }

    private static MessageBuffer BuildLegacyMessage(DBusConnection bus, string title, string body, string? iconPath)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(LegacyBusName, LegacyObjectPath, LegacyBusName, "Notify",
                "susssasa{sv}i", MessageFlags.None);
            writer.WriteString("ShareX");
            writer.WriteUInt32(0);
            writer.WriteString(iconPath ?? LinuxEnvironment.ApplicationId);
            writer.WriteString(title);
            writer.WriteString(body);
            writer.WriteArray(System.Array.Empty<string>());

            ArrayStart hints = writer.WriteDictionaryStart();
            writer.AddString("desktop-entry", LinuxEnvironment.ApplicationId);
            writer.WriteDictionaryEnd(hints);

            writer.WriteInt32(-1);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteButtons(ref MessageWriter writer, IReadOnlyList<DesktopNotificationButton> buttons)
    {
        VariantValue[] entries = new VariantValue[buttons.Count];

        for (int i = 0; i < buttons.Count; i++)
        {
            Dict<string, VariantValue> button = new()
            {
                ["label"] = buttons[i].Label,
                ["action"] = buttons[i].ActionId
            };

            entries[i] = button.AsVariantValue();
        }

        writer.AddVariant("buttons", VariantValue.ArrayOfVariant(entries));
    }

    private static string PriorityToString(DesktopNotificationPriority priority) => priority switch
    {
        DesktopNotificationPriority.Low => "low",
        DesktopNotificationPriority.High => "high",
        DesktopNotificationPriority.Urgent => "urgent",
        _ => "normal"
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (IDisposable subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
    }
}
