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

namespace ShareX.Linux.Ipc;

/// <summary>
/// Calls an already-running ShareX over the session bus.
/// </summary>
/// <remarks>
/// This deliberately avoids touching anything else in ShareX. It runs before the settings, the
/// toolkit or the capture stack are loaded, so a hotkey costs a bus round trip rather than a
/// second application start.
/// </remarks>
public static class ShareXDBusClient
{
    /// <summary>
    /// A hotkey should feel immediate or not happen at all; waiting longer than this means
    /// something is wrong and the caller is better off falling back.
    /// </summary>
    private static readonly TimeSpan callTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Asks the running instance to run a job. Returns false when no instance is listening, so
    /// the caller can start one instead.
    /// </summary>
    public static bool TryTrigger(string job, string? filePath = null)
    {
        if (string.IsNullOrEmpty(job)) return false;

        try
        {
            return CallAsync(job, filePath).Wait(callTimeout);
        }
        catch (Exception)
        {
            // No instance is running, or the bus is unreachable; the caller starts ShareX.
            return false;
        }
    }

    /// <summary>Returns the running instance's version, or null when nothing is listening.</summary>
    public static string? TryPing()
    {
        try
        {
            Task<string> call = PingAsync();

            return call.Wait(callTimeout) ? call.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task CallAsync(string job, string? filePath)
    {
        using DBusConnection connection = await ConnectAsync().ConfigureAwait(false);

        bool withFile = !string.IsNullOrEmpty(filePath);

        MessageBuffer message = Build(connection,
            withFile ? ShareXDBus.TriggerFileMember : ShareXDBus.TriggerMember,
            withFile ? "ss" : "s",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(job);
                if (withFile) writer.WriteString(filePath!);
            });

        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }

    private static async Task<string> PingAsync()
    {
        using DBusConnection connection = await ConnectAsync().ConfigureAwait(false);

        MessageBuffer message = Build(connection, ShareXDBus.PingMember, null, null);

        return await connection.CallMethodAsync(message,
            static (Message reply, object? state) => reply.GetBodyReader().ReadString(), null).ConfigureAwait(false);
    }

    private static async Task<DBusConnection> ConnectAsync()
    {
        string? address = DBusAddress.Session
            ?? throw new PlatformNotSupportedException("No D-Bus session bus is available.");

        DBusConnection connection = new(address);

        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static MessageBuffer Build(DBusConnection connection, string member, string? signature,
        WriteArguments? writeArguments)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            // NoAutoStart keeps a stray call from launching ShareX through bus activation when
            // the caller wants to decide that for itself.
            writer.WriteMethodCallHeader(ShareXDBus.ServiceName, ShareXDBus.ObjectPath, ShareXDBus.InterfaceName,
                member, signature, MessageFlags.NoAutoStart);
            writeArguments?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private delegate void WriteArguments(ref MessageWriter writer);
}
