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

using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.Linux.Recording;
using ShareX.Linux.Desktop;
using ShareX.Linux.Capture;
using ShareX.Linux;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ShareX;

internal static class ApplicationDiagnostics
{
    private static bool _exceptionHandlersEnabled;

    internal static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += ResolveLocalizedAssembly;

        ConnectPlatformLogging();

#if DEBUG
        if (Debugger.IsAttached)
        {
            return;
        }
#endif

        _exceptionHandlersEnabled = true;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    internal static void InitializeAvalonia()
    {
        if (_exceptionHandlersEnabled)
        {
            Dispatcher.UIThread.UnhandledException += OnAvaloniaDispatcherException;
        }
    }

    /// <summary>
    /// Routes the Linux platform layer's failures into the ShareX log. Without this, a portal
    /// or capture problem leaves no trace at all, which is exactly the case that needs one.
    /// </summary>
    private static void ConnectPlatformLogging()
    {
        void Report(Exception exception, string message) => DebugHelper.WriteException(exception, message);

        WaylandScreenCapture.ExceptionLogger = Report;
        WaylandScreenCapture.DiagnosticLogger = DebugHelper.WriteLine;
        WaylandRecordingSource.ExceptionLogger = Report;
        HyprlandCompositor.ExceptionLogger = Report;
        ShellIntegration.ExceptionLogger = Report;
        SoundPlayback.ExceptionLogger = Report;
        PrintingService.ExceptionLogger = Report;
        UpdateSignatureVerifier.ExceptionLogger = Report;
        WaylandClipboard.ExceptionLogger = Report;
    }

    /// <summary>Records how this session will capture the screen, which is the first thing to check.</summary>
    internal static void WriteCaptureEnvironment()
    {
        DebugHelper.WriteLine("Session: {0}", SystemInformation.SessionDescription);
        DebugHelper.WriteLine("Compositor: {0} (XDG_CURRENT_DESKTOP: {1})",
            string.IsNullOrEmpty(LinuxEnvironment.CompositorName) ? "unknown" : LinuxEnvironment.CompositorName,
            string.IsNullOrEmpty(LinuxEnvironment.DesktopName) ? "unset" : LinuxEnvironment.DesktopName);
        DebugHelper.WriteLine("Screen capture: {0}", WaylandScreenCapture.DescribeBackend());
        DebugHelper.WriteLine("Clipboard: {0}",
            WaylandClipboard.IsAvailable ? "wl-clipboard" : "toolkit (" + WaylandClipboard.UnavailableReason + ")");
    }

    internal static void WriteStartupFlags()
    {
        List<string> flags = [];
        if (ApplicationInfo.Dev) flags.Add(nameof(ApplicationInfo.Dev));
        if (StartupOptions.MultiInstance) flags.Add(nameof(StartupOptions.MultiInstance));
        if (StartupOptions.Portable) flags.Add(nameof(StartupOptions.Portable));
        if (StartupOptions.SilentRun) flags.Add(nameof(StartupOptions.SilentRun));
        if (StartupOptions.Sandbox) flags.Add(nameof(StartupOptions.Sandbox));
        if (StartupOptions.IgnoreHotkeyWarning) flags.Add(nameof(StartupOptions.IgnoreHotkeyWarning));
        if (SystemOptions.DisableUpdateCheck) flags.Add(nameof(SystemOptions.DisableUpdateCheck));
        if (SystemOptions.DisableUpload) flags.Add(nameof(SystemOptions.DisableUpload));
        if (SystemOptions.DisableLogging) flags.Add(nameof(SystemOptions.DisableLogging));

        if (flags.Count > 0)
        {
            DebugHelper.WriteLine("Flags: " + string.Join(", ", flags));
        }
    }

    private static Assembly? ResolveLocalizedAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.CultureName) ||
            assemblyName.Name?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Languages", assemblyName.CultureName, assemblyName.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static void OnAvaloniaDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowError(e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception ??
            new Exception($"Unhandled exception object: {e.ExceptionObject}");
        ShowError(exception);
    }

    private static void ShowError(Exception exception)
    {
        ErrorWindowIntegration.Show(exception.Message, $"{exception}\r\n\r\n{ApplicationInfo.Title}",
            AppPaths.LogsFilePath, Links.GitHubIssues);
    }
}
