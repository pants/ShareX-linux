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

using System.Text;
using Tmds.DBus.Protocol;

namespace ShareX.Linux.Ipc;

/// <summary>
/// Names shared by the ShareX D-Bus service and the clients that call it.
/// </summary>
/// <remarks>
/// Hotkeys are the reason this exists. A compositor keybinding that runs the ShareX executable
/// pays for a whole process start - the runtime, the toolkit, the settings - before it can take
/// a screenshot. Calling the already-running instance over the session bus skips all of it, and
/// is reachable from any language or from gdbus and busctl in a shell.
/// </remarks>
public static class ShareXDBus
{
    public const string ServiceName = "com.getsharex.ShareX";
    public const string ObjectPath = "/com/getsharex/ShareX";
    public const string InterfaceName = "com.getsharex.ShareX";

    public const string TriggerMember = "Trigger";
    public const string TriggerFileMember = "TriggerWithFile";
    public const string OpenMainWindowMember = "OpenMainWindow";
    public const string PingMember = "Ping";

    /// <summary>Introspection XML, so gdbus and d-feet can discover the interface.</summary>
    public static ReadOnlyMemory<byte> IntrospectionXml { get; } = Encoding.UTF8.GetBytes(
        $"""
          <interface name="{InterfaceName}">
            <method name="{TriggerMember}">
              <arg name="job" type="s" direction="in"/>
            </method>
            <method name="{TriggerFileMember}">
              <arg name="job" type="s" direction="in"/>
              <arg name="path" type="s" direction="in"/>
            </method>
            <method name="{OpenMainWindowMember}"/>
            <method name="{PingMember}">
              <arg name="version" type="s" direction="out"/>
            </method>
          </interface>

        """);
}
