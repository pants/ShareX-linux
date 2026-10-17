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

#nullable enable

using System;
using System.Linq;
using ShareX.Linux.Ipc;

namespace ShareX;

/// <summary>
/// The "--trigger" command line, which hands a job to the running ShareX and exits.
/// </summary>
/// <remarks>
/// Binding a compositor hotkey to the ShareX executable starts a whole process - the .NET
/// runtime, the toolkit, the settings, the capture backends - before the screenshot can even
/// begin. This path runs before any of that: it makes one D-Bus call and returns, so the
/// capture starts in the instance that is already warm.
///
/// When nothing is listening it reports failure and start-up continues normally, so the first
/// use of a hotkey still works and leaves an instance running for the next one.
/// </remarks>
internal static class FastTrigger
{
    private const string FlagLong = "--trigger";
    private const string FlagShort = "-trigger";

    /// <summary>
    /// Handles the flag if present. Returns true when the process should exit immediately.
    /// </summary>
    internal static bool TryHandle(string[] args)
    {
        if (args == null || args.Length == 0) return false;

        int index = Array.FindIndex(args, argument =>
            argument.Equals(FlagLong, StringComparison.OrdinalIgnoreCase) ||
            argument.Equals(FlagShort, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return false;

        string[] rest = args.Skip(index + 1).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        if (rest.Length == 0)
        {
            Console.Error.WriteLine($"Usage: ShareX {FlagLong} <job> [file]");
            Console.Error.WriteLine("Example: ShareX --trigger RectangleRegion");
            Environment.Exit(2);
            return true;
        }

        string job = rest[0];
        string? filePath = rest.Length > 1 ? rest[1] : null;

        if (ShareXDBusClient.TryTrigger(job, filePath))
        {
            // Exit rather than return: the runtime has already started threads of its own, and
            // a hotkey should leave nothing behind.
            Environment.Exit(0);
            return true;
        }

        // Nothing was listening, so fall through and start ShareX. Rewriting the arguments into
        // the form the normal command line understands means the job still runs.
        ApplicationCommandLine.Initialize(BuildStartupArguments(job, filePath));
        return false;
    }

    private static string[] BuildStartupArguments(string job, string? filePath) =>
        filePath == null ? ["-" + job] : ["-" + job, filePath];
}
