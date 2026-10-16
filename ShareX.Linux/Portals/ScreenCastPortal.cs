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

[Flags]
public enum ScreenCastSourceTypes : uint
{
    Monitor = 1,
    Window = 2,
    Virtual = 4
}

public enum ScreenCastCursorMode : uint
{
    Hidden = 1,
    Embedded = 2,
    Metadata = 4
}

public enum ScreenCastPersistMode : uint
{
    DoNot = 0,
    Application = 1,
    ExplicitlyRevoked = 2
}

/// <summary>One PipeWire stream handed back by the ScreenCast portal.</summary>
public sealed class ScreenCastStream
{
    public uint NodeId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ScreenCastSourceTypes SourceType { get; init; }

    /// <summary>Stable identifier of the captured output, when the compositor supplies one.</summary>
    public string? MappingId { get; init; }

    public override string ToString() => $"node {NodeId} at {X},{Y} {Width}x{Height} ({SourceType})";
}

/// <summary>
/// A live ScreenCast session. Holding it open keeps the PipeWire nodes alive; disposing it tells
/// the compositor to tear the capture down and drops the recording indicator.
/// </summary>
public sealed class ScreenCastSession : IAsyncDisposable
{
    private readonly PortalConnection connection;
    private int pipeWireFd = -1;
    private bool closed;

    internal ScreenCastSession(PortalConnection connection, string handle)
    {
        this.connection = connection;
        Handle = handle;
    }

    public string Handle { get; }

    public IReadOnlyList<ScreenCastStream> Streams { get; internal set; } = [];

    /// <summary>Token that <see cref="ScreenCastPortal"/> can replay to skip the picker next time.</summary>
    public string? RestoreToken { get; internal set; }

    /// <summary>
    /// A file descriptor for the PipeWire remote the streams live on. The session owns it; it is
    /// opened on first use and closed with the session.
    /// </summary>
    public async Task<int> GetPipeWireFileDescriptorAsync(CancellationToken cancellationToken = default)
    {
        if (pipeWireFd >= 0) return pipeWireFd;

        pipeWireFd = await new ScreenCastPortal(connection).OpenPipeWireRemoteAsync(Handle, cancellationToken).ConfigureAwait(false);
        return pipeWireFd;
    }

    public async ValueTask DisposeAsync()
    {
        if (closed) return;
        closed = true;

        if (pipeWireFd >= 0)
        {
            Interop.LibC.Close(pipeWireFd);
            pipeWireFd = -1;
        }

        await connection.CloseSessionAsync(Handle).ConfigureAwait(false);
    }
}

/// <summary>
/// org.freedesktop.portal.ScreenCast. This is how a Wayland client gets a continuous feed of a
/// monitor or window: the compositor publishes a PipeWire node and we read frames from it.
/// </summary>
public sealed class ScreenCastPortal
{
    public const string InterfaceName = "org.freedesktop.portal.ScreenCast";

    private readonly PortalConnection connection;

    public ScreenCastPortal(PortalConnection? connection = null) => this.connection = connection ?? PortalConnection.Instance;

    public Task<uint> GetVersionAsync(CancellationToken cancellationToken = default) =>
        connection.GetVersionAsync(InterfaceName, cancellationToken);

    /// <summary>Source types this portal backend is willing to offer.</summary>
    public async Task<ScreenCastSourceTypes> GetAvailableSourceTypesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            VariantValue value = await connection.GetPropertyAsync(InterfaceName, "AvailableSourceTypes", cancellationToken)
                .ConfigureAwait(false);
            return (ScreenCastSourceTypes)value.GetUInt32();
        }
        catch
        {
            return ScreenCastSourceTypes.Monitor;
        }
    }

    public async Task<ScreenCastCursorMode> GetAvailableCursorModesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            VariantValue value = await connection.GetPropertyAsync(InterfaceName, "AvailableCursorModes", cancellationToken)
                .ConfigureAwait(false);
            return (ScreenCastCursorMode)value.GetUInt32();
        }
        catch
        {
            return ScreenCastCursorMode.Hidden;
        }
    }

    /// <summary>
    /// Runs the full CreateSession / SelectSources / Start handshake and returns a started session.
    /// Unless a <paramref name="restoreToken"/> from an earlier session is replayed, the compositor
    /// shows its own picker so the user chooses what ShareX may see.
    /// </summary>
    public async Task<ScreenCastSession> StartAsync(
        ScreenCastSourceTypes sourceTypes = ScreenCastSourceTypes.Monitor,
        ScreenCastCursorMode cursorMode = ScreenCastCursorMode.Hidden,
        bool multiple = false,
        string? restoreToken = null,
        ScreenCastPersistMode persistMode = ScreenCastPersistMode.Application,
        string parentWindow = "",
        CancellationToken cancellationToken = default)
    {
        string sessionToken = connection.CreateToken("screencast_session");

        PortalResponse created = await connection.CallRequestAsync(InterfaceName, "CreateSession", "a{sv}", null,
            (ref MessageWriter writer) => writer.AddString("session_handle_token", sessionToken),
            cancellationToken).ConfigureAwait(false);

        if (!created.Success) throw new PortalException($"Creating the screen cast session failed ({created.Code}).");

        string? sessionHandle = created.GetString("session_handle");

        if (string.IsNullOrEmpty(sessionHandle))
        {
            throw new PortalException("The screen cast portal did not return a session handle.");
        }

        ScreenCastSession session = new(connection, sessionHandle);

        try
        {
            PortalResponse selected = await connection.CallRequestAsync(InterfaceName, "SelectSources", "oa{sv}",
                (ref MessageWriter writer) => writer.WriteObjectPath(sessionHandle),
                (ref MessageWriter writer) =>
                {
                    writer.AddUInt32("types", (uint)sourceTypes);
                    writer.AddBool("multiple", multiple);
                    writer.AddUInt32("cursor_mode", (uint)cursorMode);
                    writer.AddUInt32("persist_mode", (uint)persistMode);
                    if (!string.IsNullOrEmpty(restoreToken)) writer.AddString("restore_token", restoreToken);
                },
                cancellationToken).ConfigureAwait(false);

            if (selected.Cancelled) throw new PortalCancelledException("Screen sharing was cancelled.");
            if (!selected.Success) throw new PortalException($"Selecting screen cast sources failed ({selected.Code}).");

            PortalResponse started = await connection.CallRequestAsync(InterfaceName, "Start", "osa{sv}",
                (ref MessageWriter writer) =>
                {
                    writer.WriteObjectPath(sessionHandle);
                    writer.WriteString(parentWindow);
                }, null, cancellationToken).ConfigureAwait(false);

            if (started.Cancelled) throw new PortalCancelledException("Screen sharing was cancelled.");
            if (!started.Success) throw new PortalException($"Starting the screen cast failed ({started.Code}).");

            session.Streams = ReadStreams(started);
            session.RestoreToken = started.GetString("restore_token");

            if (session.Streams.Count == 0)
            {
                throw new PortalException("The screen cast portal did not return any streams.");
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens the PipeWire remote for a session and returns a raw file descriptor the caller owns.</summary>
    public async Task<int> OpenPipeWireRemoteAsync(string sessionHandle, CancellationToken cancellationToken = default)
    {
        DBusConnection bus = await connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        MessageBuffer message = BuildOpenRemoteMessage(bus, sessionHandle);

        return await bus.CallMethodAsync(message,
            static (Message reply, object? state) => (int)reply.GetBodyReader().ReadHandleRaw(), null).ConfigureAwait(false);
    }

    private static MessageBuffer BuildOpenRemoteMessage(DBusConnection bus, string sessionHandle)
    {
        MessageWriter writer = bus.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(PortalConnection.BusName, PortalConnection.ObjectPath, InterfaceName,
                "OpenPipeWireRemote", "oa{sv}", MessageFlags.None);
            writer.WriteObjectPath(sessionHandle);
            ArrayStart options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(options);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static List<ScreenCastStream> ReadStreams(PortalResponse response)
    {
        List<ScreenCastStream> streams = [];

        if (response.Get("streams") is not { } value) return streams;

        for (int i = 0; i < value.Count; i++)
        {
            VariantValue entry = value.GetItem(i);
            if (entry.Count < 2) continue;

            uint nodeId = entry.GetItem(0).GetUInt32();
            Dictionary<string, VariantValue> properties = entry.GetItem(1).GetDictionary<string, VariantValue>();

            (int x, int y) = ReadPoint(properties, "position");
            (int width, int height) = ReadPoint(properties, "size");

            streams.Add(new ScreenCastStream
            {
                NodeId = nodeId,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                SourceType = properties.TryGetValue("source_type", out VariantValue type)
                    ? (ScreenCastSourceTypes)type.GetUInt32()
                    : ScreenCastSourceTypes.Monitor,
                MappingId = properties.TryGetValue("mapping_id", out VariantValue mapping) &&
                            mapping.Type == VariantValueType.String
                    ? mapping.GetString()
                    : null
            });
        }

        return streams;
    }

    private static (int, int) ReadPoint(Dictionary<string, VariantValue> properties, string key)
    {
        if (!properties.TryGetValue(key, out VariantValue value) || value.Count < 2) return (0, 0);

        return (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32());
    }
}
