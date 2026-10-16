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
using Tmds.DBus.Protocol;

namespace ShareX.Linux.Portals;

/// <summary>Result of an xdg-desktop-portal request.</summary>
public readonly struct PortalResponse
{
    /// <summary>0 = success, 1 = cancelled by the user, 2 = ended some other way.</summary>
    public uint Code { get; init; }

    public Dictionary<string, VariantValue> Results { get; init; }

    public bool Success => Code == 0;
    public bool Cancelled => Code == 1;

    public string? GetString(string key) =>
        Results.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.String ? value.GetString() : null;

    public uint? GetUInt32(string key) =>
        Results.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.UInt32 ? value.GetUInt32() : null;

    public VariantValue? Get(string key) => Results.TryGetValue(key, out VariantValue value) ? value : (VariantValue?)null;
}

/// <summary>
/// Shared plumbing for the xdg-desktop-portal D-Bus API: the session bus connection and the
/// Request pattern (call a method, then wait for the Response signal on the returned handle).
/// </summary>
public sealed class PortalConnection : IDisposable
{
    public const string BusName = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string RequestInterface = "org.freedesktop.portal.Request";
    public const string SessionInterface = "org.freedesktop.portal.Session";

    private static readonly Lazy<PortalConnection> instance = new(() => new PortalConnection(), isThreadSafe: true);

    public static PortalConnection Instance => instance.Value;

    private readonly SemaphoreSlim connectLock = new(1, 1);
    private DBusConnection? connection;
    private string? uniqueNameToken;
    private int tokenCounter;
    private bool disposed;

    /// <summary>True when a session bus is reachable at all. Portals are unusable without one.</summary>
    public static bool IsAvailable => !string.IsNullOrEmpty(DBusAddress.Session);

    public async Task<DBusConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (connection != null) return connection;

        await connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (connection == null)
            {
                string? address = DBusAddress.Session;

                if (string.IsNullOrEmpty(address))
                {
                    throw new PortalException("No D-Bus session bus is available (DBUS_SESSION_BUS_ADDRESS is unset).");
                }

                DBusConnection created = new(address);
                await created.ConnectAsync().ConfigureAwait(false);

                // Request object paths embed our bus name with ':' dropped and '.' replaced by '_'.
                uniqueNameToken = created.UniqueName!.TrimStart(':').Replace('.', '_');
                connection = created;
            }
        }
        finally
        {
            connectLock.Release();
        }

        return connection;
    }

    /// <summary>Returns a handle token unique to this process, used to predict the Request object path.</summary>
    public string CreateToken(string prefix) =>
        $"sharex_{prefix}_{Environment.ProcessId}_{Interlocked.Increment(ref tokenCounter)}";

    public string GetRequestPath(string token) => $"{ObjectPath}/request/{uniqueNameToken}/{token}";

    /// <summary>
    /// Invokes a portal method that follows the Request pattern and resolves once the portal
    /// emits its Response signal. <paramref name="writeArguments"/> must write every argument
    /// except the trailing options dictionary, which <paramref name="writeOptions"/> fills in.
    /// </summary>
    public async Task<PortalResponse> CallRequestAsync(string portalInterface, string member, string signature,
        PortalArgumentsCallback? writeArguments, PortalOptionsCallback? writeOptions,
        CancellationToken cancellationToken = default)
    {
        DBusConnection bus = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        string token = CreateToken(member.ToLowerInvariant());
        string requestPath = GetRequestPath(token);

        TaskCompletionSource<PortalResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before calling: the portal may answer before the method reply arrives.
        using IDisposable subscription = await WatchResponseAsync(bus, requestPath, completion).ConfigureAwait(false);

        MessageBuffer message = BuildMessage(bus, portalInterface, member, signature, token, writeArguments, writeOptions);

        // The reply carries the real handle; it matches our prediction on every current portal
        // implementation, but honour it anyway so older backends keep working.
        string actualPath = await bus.CallMethodAsync(message,
            static (Message reply, object? state) => reply.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);

        if (string.Equals(actualPath, requestPath, StringComparison.Ordinal))
        {
            return await WaitAsync(completion.Task, cancellationToken).ConfigureAwait(false);
        }

        using IDisposable fallback = await WatchResponseAsync(bus, actualPath, completion).ConfigureAwait(false);
        return await WaitAsync(completion.Task, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask<IDisposable> WatchResponseAsync(DBusConnection bus, string requestPath,
        TaskCompletionSource<PortalResponse> completion) =>
        bus.WatchSignalAsync(BusName, requestPath, RequestInterface, "Response",
            static (Message message, object? state) => ReadResponse(message),
            (Notification<PortalResponse> notification) =>
            {
                // Exception is only readable once the observer has completed; reading it on a
                // value notification throws, and an exception escaping here closes the bus.
                if (notification.IsCompletion)
                {
                    if (notification.Exception != null) completion.TrySetException(notification.Exception);
                }
                else if (notification.HasValue)
                {
                    completion.TrySetResult(notification.Value);
                }
            },
            ObserverFlags.None, false, null);

    private static MessageBuffer BuildMessage(DBusConnection bus, string portalInterface, string member, string signature,
        string? handleToken, PortalArgumentsCallback? writeArguments, PortalOptionsCallback? writeOptions)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(BusName, ObjectPath, portalInterface, member, signature, MessageFlags.None);
            writeArguments?.Invoke(ref writer);

            ArrayStart options = writer.WriteDictionaryStart();
            if (handleToken != null) writer.AddString("handle_token", handleToken);
            writeOptions?.Invoke(ref writer);
            writer.WriteDictionaryEnd(options);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Invokes a plain portal method with no Request handle and discards the reply.</summary>
    public async Task CallAsync(string portalInterface, string member, string signature,
        PortalArgumentsCallback? writeArguments, CancellationToken cancellationToken = default)
    {
        DBusConnection bus = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        await bus.CallMethodAsync(BuildPlainMessage(bus, portalInterface, member, signature, writeArguments)).ConfigureAwait(false);
    }

    private static MessageBuffer BuildPlainMessage(DBusConnection bus, string portalInterface, string member,
        string? signature, PortalArgumentsCallback? writeArguments)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(BusName, ObjectPath, portalInterface, member, signature, MessageFlags.None);
            writeArguments?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Reads a property from one of the portal interfaces.</summary>
    public async Task<VariantValue> GetPropertyAsync(string portalInterface, string property,
        CancellationToken cancellationToken = default)
    {
        DBusConnection bus = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        MessageBuffer message = BuildPlainMessage(bus, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter writer) =>
        {
            writer.WriteString(portalInterface);
            writer.WriteString(property);
        });

        return await bus.CallMethodAsync(message,
            static (Message reply, object? state) => reply.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
    }

    /// <summary>Reports the portal's version for an interface, or 0 when the interface is missing.</summary>
    public async Task<uint> GetVersionAsync(string portalInterface, CancellationToken cancellationToken = default)
    {
        try
        {
            VariantValue value = await GetPropertyAsync(portalInterface, "version", cancellationToken).ConfigureAwait(false);
            return value.Type == VariantValueType.UInt32 ? value.GetUInt32() : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task CloseSessionAsync(string sessionHandle, CancellationToken cancellationToken = default)
    {
        try
        {
            DBusConnection bus = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

            await bus.CallMethodAsync(BuildSessionCloseMessage(bus, sessionHandle)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Closing portal session failed: {exception.Message}");
        }
    }

    private static MessageBuffer BuildSessionCloseMessage(DBusConnection bus, string sessionHandle)
    {
        using MessageWriter writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, sessionHandle, SessionInterface, "Close", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    private static PortalResponse ReadResponse(Message message)
    {
        Reader reader = message.GetBodyReader();
        uint code = reader.ReadUInt32();
        return new PortalResponse { Code = code, Results = reader.ReadDictionaryOfStringToVariantValue() };
    }

    private static async Task<PortalResponse> WaitAsync(Task<PortalResponse> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return await task.ConfigureAwait(false);

        TaskCompletionSource cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancellation))
        {
            if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        connection?.Dispose();
        connection = null;
        connectLock.Dispose();
    }
}

/// <summary>Writes the fixed arguments of a portal method call.</summary>
public delegate void PortalArgumentsCallback(ref MessageWriter writer);

/// <summary>Adds entries to the trailing a{sv} options dictionary of a portal method call.</summary>
public delegate void PortalOptionsCallback(ref MessageWriter writer);

/// <summary>
/// Helpers for the a{sv} options dictionary every portal method takes. The dictionary is already
/// open when a <see cref="PortalOptionsCallback"/> runs, so callbacks only add entries.
/// </summary>
public static class PortalOptions
{
    public static void AddString(this ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    public static void AddBool(this ref MessageWriter writer, string key, bool value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantBool(value);
    }

    public static void AddUInt32(this ref MessageWriter writer, string key, uint value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantUInt32(value);
    }

    public static void AddStringArray(this ref MessageWriter writer, string key, IReadOnlyList<string> values)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(VariantValue.Array(values is string[] array ? array : values.ToArray()));
    }

    public static void AddVariant(this ref MessageWriter writer, string key, VariantValue value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(value);
    }
}

public class PortalException : Exception
{
    public PortalException(string message) : base(message) { }
    public PortalException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when the user dismisses a portal dialog.</summary>
public sealed class PortalCancelledException : PortalException
{
    public PortalCancelledException(string message = "The request was cancelled.") : base(message) { }
}
