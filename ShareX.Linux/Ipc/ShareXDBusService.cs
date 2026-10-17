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

namespace ShareX.Linux.Ipc;

/// <summary>What a caller asked the running ShareX to do.</summary>
public sealed record ShareXRequest(string Job, string? FilePath);

/// <summary>
/// Publishes ShareX on the session bus so hotkeys and scripts can drive the running instance
/// instead of starting another one.
/// </summary>
public sealed class ShareXDBusService : IPathMethodHandler, IAsyncDisposable
{
    private readonly Func<ShareXRequest, Task> onTrigger;
    private readonly Func<Task> onOpenMainWindow;
    private readonly string version;
    private DBusConnection? connection;
    private bool disposed;

    public ShareXDBusService(Func<ShareXRequest, Task> onTrigger, Func<Task> onOpenMainWindow, string version)
    {
        this.onTrigger = onTrigger;
        this.onOpenMainWindow = onOpenMainWindow;
        this.version = version;
    }

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    public string Path => ShareXDBus.ObjectPath;

    public bool HandlesChildPaths => false;

    /// <summary>True once the bus name belongs to this process.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>
    /// Claims the bus name and starts answering calls. A second instance will fail to take the
    /// name, which is harmless: it simply means this process is not the one serving requests.
    /// </summary>
    public async Task<bool> PublishAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        try
        {
            string? address = DBusAddress.Session;

            if (string.IsNullOrEmpty(address))
            {
                ExceptionLogger?.Invoke(new PlatformNotSupportedException("No session bus."),
                    "ShareX could not publish its D-Bus service, so hotkeys will start a new process each time.");
                return false;
            }

            connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);

            connection.AddMethodHandler(this);

            // Throws when the name is already taken, which means another ShareX is serving.
            await connection.RequestNameAsync(ShareXDBus.ServiceName, RequestNameOptions.None).ConfigureAwait(false);

            IsPublished = true;

            return true;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Publishing the ShareX D-Bus service failed.");
            return false;
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ShareXDBus.IntrospectionXml], []);
            return default;
        }

        if (!context.Request.InterfaceAsString!.Equals(ShareXDBus.InterfaceName, StringComparison.Ordinal))
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        switch (context.Request.MemberAsString)
        {
            case ShareXDBus.TriggerMember:
                return HandleTrigger(context, withFile: false);

            case ShareXDBus.TriggerFileMember:
                return HandleTrigger(context, withFile: true);

            case ShareXDBus.OpenMainWindowMember:
                ReplyEmpty(context);
                RunDetached(() => onOpenMainWindow());
                return default;

            case ShareXDBus.PingMember:
                ReplyString(context, version);
                return default;

            default:
                context.ReplyUnknownMethodError();
                return default;
        }
    }

    private ValueTask HandleTrigger(MethodContext context, bool withFile)
    {
        Reader reader = context.Request.GetBodyReader();
        string job = reader.ReadString();
        string? path = withFile ? reader.ReadString() : null;

        // Reply before doing the work: a hotkey should not wait for the capture to finish, and
        // a region selection can stay on screen for as long as the user likes.
        ReplyEmpty(context);
        RunDetached(() => onTrigger(new ShareXRequest(job, string.IsNullOrEmpty(path) ? null : path)));

        return default;
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;

        using MessageWriter writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private static void ReplyString(MethodContext context, string value)
    {
        if (context.NoReplyExpected) return;

        MessageWriter writer = context.CreateReplyWriter("s");

        try
        {
            writer.WriteString(value);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Runs the requested work without holding up the bus reader thread.</summary>
    private static void RunDetached(Func<Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "A request received over D-Bus failed.");
            Debug.WriteLine($"D-Bus request failed: {exception}");
        }
    });

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        if (connection != null)
        {
            if (IsPublished)
            {
                try
                {
                    await connection.ReleaseNameAsync(ShareXDBus.ServiceName).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Releasing the ShareX bus name failed: {exception.Message}");
                }
            }

            connection.Dispose();
            connection = null;
        }

        IsPublished = false;
    }
}
