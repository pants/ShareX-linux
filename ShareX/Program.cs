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

using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.Linux.Ipc;
using ShareX.ImageEditor.Integration;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

#if MicrosoftStore
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#endif

namespace ShareX;

internal static class Program
{
    private static Stopwatch _startTimer = null!;
    private static ShareXDBusService? _dbusService;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationDiagnostics.Initialize();
        _startTimer = Stopwatch.StartNew();
        ApplicationCommandLine.Initialize(args);

        // Before anything else is touched: a hotkey asking the running instance to do something
        // should cost a bus round trip, not a second application start.
        if (FastTrigger.TryHandle(args))
        {
            return;
        }

#if STEAM
        if (ApplicationCommandLine.TryHandleUninstall())
        {
            return;
        }
#endif

        SystemOptions.UpdateSystemOptions();
        PersonalPathManager.Initialize();
        DebugHelper.Init(AppPaths.LogsFilePath);

        StartupOptions.IsAdmin = Helpers.IsAdministrator();
        StartupOptions.MultiInstance = ApplicationCommandLine.IsCommandPresent("multi", "m");

        using (SingleInstanceManager singleInstanceManager = new(
            ApplicationInfo.MutexName, ApplicationInfo.PipeName, !StartupOptions.MultiInstance, args))
        {
            if (!singleInstanceManager.IsSingleInstance || singleInstanceManager.IsFirstInstance)
            {
                singleInstanceManager.ArgumentsReceived += SingleInstanceCommandRouter.ArgumentsReceived;
                Run(args);
            }
        }

        ApplicationLifecycle.RestartIfRequested();
        DebugHelper.Flush();
    }

    private static void Run(string[] args)
    {
        DebugHelper.WriteLine("ShareX starting.");
        DebugHelper.WriteLine("Version: " + ApplicationInfo.VersionText);
        DebugHelper.WriteLine("Build: " + ApplicationInfo.Build);
        DebugHelper.WriteLine("Command line: " + Environment.CommandLine);
        DebugHelper.WriteLine("Personal path: " + AppPaths.PersonalFolder);
        if (!string.IsNullOrEmpty(PersonalPathManager.DetectionMethod))
        {
            DebugHelper.WriteLine("Personal path detection method: " + PersonalPathManager.DetectionMethod);
        }
        DebugHelper.WriteLine("Operating system: " + Helpers.GetOperatingSystemProductName(true));
        DebugHelper.WriteLine(".NET version: " + Environment.Version);
        DebugHelper.WriteLine("Running as elevated process: " + StartupOptions.IsAdmin);

        StartupOptions.SilentRun = ApplicationCommandLine.IsCommandPresent("silent", "s");
#if MicrosoftStore
        StartupOptions.SilentRun = StartupOptions.SilentRun || AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
#endif
        StartupOptions.IgnoreHotkeyWarning = ApplicationCommandLine.IsCommandPresent("NoHotkeys");

        PersonalPathManager.CreateApplicationFolders();
        ApplicationDiagnostics.WriteStartupFlags();
        ApplicationDiagnostics.WriteCaptureEnvironment();

        DebugHelper.WriteLine("Avalonia application initializing.");
        AvaloniaBootstrapper.Initialize(args, StartApplicationAsync, ApplicationLifecycle.OnAvaloniaStopped);
        ApplicationDiagnostics.InitializeAvalonia();

        SettingManager.LoadInitialSettings();
        LanguageHelper.ChangeLanguage(ApplicationState.Settings.Language);
        CleanupManager.CleanupAsync();

        DebugHelper.WriteLine("Avalonia application starting.");
        AvaloniaBootstrapper.Run();

        ApplicationLifecycle.CloseSequence();
    }

    /// <summary>
    /// Puts ShareX on the session bus so hotkeys can drive this instance instead of starting
    /// another one. Failing to publish is not fatal; it only means "--trigger" falls back to a
    /// normal start.
    /// </summary>
    private static async Task PublishDBusServiceAsync()
    {
        _dbusService = new ShareXDBusService(
            request => Enum.TryParse(request.Job, ignoreCase: true, out HotkeyType job)
                ? TaskHelpers.ExecuteJob(ApplicationState.DefaultTaskSettings, job, request.FilePath)
                : UnknownJobAsync(request.Job),
            () =>
            {
                MainWindowIntegration.Activate();
                return Task.CompletedTask;
            },
            ApplicationInfo.VersionText);

        if (await _dbusService.PublishAsync())
        {
            DebugHelper.WriteLine("D-Bus service published: " + ShareXDBus.ServiceName);
        }
    }

    private static Task UnknownJobAsync(string job)
    {
        DebugHelper.WriteLine($"D-Bus request named an unknown job: \"{job}\".");
        return Task.CompletedTask;
    }

    private static async Task StartApplicationAsync()
    {
        ImageEditorIntegration.Initialize();

        if (ApplicationState.Settings.ShowStartScreen)
        {
            DebugHelper.WriteLine("Start screen opening.");
            StartScreenWindow startScreen = new();
            await startScreen.ShowAsync();
            DebugHelper.WriteLine("Start screen closed.");
        }

        DebugHelper.WriteLine("Hotkey host init started.");
        MainForm hotkeyHost = new();
        ApplicationLifecycle.AttachHost(hotkeyHost);
        hotkeyHost.Initialize();

        await ApplicationSettingsRuntime.InitializeAsync(hotkeyHost);

        bool showMainWindow = !(StartupOptions.SilentRun || ApplicationState.Settings.SilentRun) ||
            !ApplicationState.Settings.ShowTray;
        MainWindowIntegration.Initialize(hotkeyHost.TrayIconService, showMainWindow);
        SingleInstanceCommandRouter.MarkReady();

        await PublishDBusServiceAsync();


        if (showMainWindow)
        {
            MainWindowIntegration.Activate();
        }

        DebugHelper.WriteLine("Startup time: {0} ms", _startTimer.ElapsedMilliseconds);
        await ApplicationCommandLine.ExecuteInitialAsync();

        if (ApplicationState.Settings.ActionsToolbarRunAtStartup)
        {
            TaskHelpers.OpenActionsToolbar();
        }

        DebugHelper.WriteLine("Hotkey host init finished.");
    }
}
