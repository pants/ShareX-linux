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
using ShareX.Linux.Interop;

namespace ShareX.Linux.Desktop;

/// <summary>Helpers for the local IPC channel ShareX uses to talk to an already-running instance.</summary>
public static class LinuxIpc
{
    private const uint OwnerReadWrite = 0b110_000_000; // 0600

    /// <summary>
    /// Tightens the permissions on the Unix socket behind a .NET named pipe so only the user who
    /// created it can connect.
    /// </summary>
    public static bool RestrictPipeToCurrentUser(string pipeName)
    {
        string path = GetPipeSocketPath(pipeName);

        try
        {
            if (!File.Exists(path)) return false;

            return LibC.Chmod(path, OwnerReadWrite) == 0;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Restricting \"{path}\" failed: {exception.Message}");
            return false;
        }
    }

    /// <summary>The socket path .NET derives from a pipe name on Unix.</summary>
    public static string GetPipeSocketPath(string pipeName) =>
        Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
}
