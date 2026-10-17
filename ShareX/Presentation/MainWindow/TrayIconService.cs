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
using System;
using System.IO;

namespace ShareX;

internal interface ITrayIconService : IDisposable
{
    event Action? RightButtonUp;

    bool Visible { get; set; }
    string ToolTipText { get; set; }

    void SetIcon(byte[] iconBytes);

    /// <summary>Rebuilds the context menu from the current settings and task list.</summary>
    void RefreshMenu();
}

/// <summary>
/// Tray adapter over Avalonia's TrayIcon, which Linux desktops serve through StatusNotifierItem.
/// </summary>
/// <remarks>
/// The protocol reports a plain activation rather than individual mouse buttons, so the
/// configurable middle-click and right-click actions have nothing to hang off: a left click
/// activates and a right click is the panel's own job, showing the context menu. The
/// double-click action is still honoured by timing consecutive activations.
/// </remarks>
internal sealed class DesktopTrayIconService : ITrayIconService
{
    private readonly Avalonia.Controls.TrayIcon _avaloniaIcon;
    private readonly IDisposable? _avaloniaIconRegistration;
    private readonly Avalonia.Threading.DispatcherTimer _singleClickTimer;
    private int _leftClickCount;
    private bool _disposed;
    public event Action? RightButtonUp;

    public bool Visible
    {
        get => _avaloniaIcon.IsVisible;
        set => _avaloniaIcon.IsVisible = value;
    }

    public string ToolTipText
    {
        get => _avaloniaIcon.ToolTipText ?? string.Empty;
        set => _avaloniaIcon.ToolTipText = value;
    }

    public DesktopTrayIconService(IHotkeyHost host, byte[] icon, string text, bool visible)
    {
        _singleClickTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = Avalonia.Application.Current?.PlatformSettings?.GetDoubleTapTime(Avalonia.Input.PointerType.Mouse)
                ?? TimeSpan.FromMilliseconds(500)
        };
        _singleClickTimer.Tick += OnSingleClickTimerTick;

        _avaloniaIcon = new Avalonia.Controls.TrayIcon { Menu = new Avalonia.Controls.NativeMenu(), IsVisible = false };
        _avaloniaIcon.Clicked += OnAvaloniaClick;

        // The menu has to exist before the panel asks for it. StatusNotifierItem exports the
        // whole menu over DBusMenu up front and never tells the application that it is about to
        // open - Avalonia's exporter answers AboutToShow with "no update needed" - so building
        // the items lazily would leave the panel showing an empty menu.
        RefreshMenu();

        ToolTipText = text;
        SetIcon(icon);
        _avaloniaIconRegistration = DesktopServices.RegisterTrayIcon(_avaloniaIcon);
        Visible = visible;
    }

    public void SetIcon(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        _avaloniaIcon.Icon = new Avalonia.Controls.WindowIcon(stream);
    }

    public void RefreshMenu()
    {
        if (_disposed) return;

        BuildNativeMenu(_avaloniaIcon.Menu!, new MainMenuBuilder(true).BuildTrayMenu());
    }

    private void OnAvaloniaClick(object? sender, EventArgs e) => OnMouseUp(InputMouseButton.Left);

    private async void OnMouseUp(InputMouseButton button)
    {
        switch (button)
        {
            case InputMouseButton.Left:
                if (ApplicationState.Settings.TrayLeftDoubleClickAction == HotkeyType.None)
                {
                    await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftClickAction);
                }
                else
                {
                    _leftClickCount++;

                    if (_leftClickCount == 1)
                    {
                        _singleClickTimer.Start();
                    }
                    else
                    {
                        _leftClickCount = 0;
                        _singleClickTimer.Stop();
                        await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftDoubleClickAction);
                    }
                }
                break;
            case InputMouseButton.Middle:
                await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayMiddleClickAction);
                break;
            case InputMouseButton.Right:
                RightButtonUp?.Invoke();
                break;
        }
    }

    private async void OnSingleClickTimerTick(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();

        if (_leftClickCount == 1)
        {
            _leftClickCount = 0;
            await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftClickAction);
        }
    }

    /// <summary>
    /// Fills a native menu from ShareX's menu model, submenus and all.
    /// </summary>
    /// <remarks>
    /// Every level is built now rather than when it is opened: DBusMenu asks for the layout to
    /// whatever depth the panel wants and gives no "about to show" callback to fill it in later.
    /// The whole tray menu is cheap to build, and <see cref="RefreshMenu"/> rebuilds it whenever
    /// ShareX refreshes its menus, which is what keeps the recent-task and toggle entries current.
    /// </remarks>
    private void BuildNativeMenu(Avalonia.Controls.NativeMenu menu, System.Collections.Generic.IReadOnlyList<MainMenuEntry> entries)
    {
        menu.Items.Clear();
        foreach (MainMenuEntry entry in entries)
        {
            if (!entry.IsVisible) continue;
            if (entry.IsSeparator) { menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator()); continue; }

            Avalonia.Controls.NativeMenuItem item = CreateItem(entry);

            if (entry.CreateChildren != null)
            {
                item.Menu = new Avalonia.Controls.NativeMenu();

                // A panel does not send a click for an item that opens a submenu, so an entry
                // that both toggles something and has children offers the toggle as its first
                // child instead of silently losing it.
                if (entry.ExecuteAsync != null) item.Menu.Items.Add(CreateItem(entry));

                BuildNativeMenu(item.Menu, SafeInvoke(entry.CreateChildren));
            }
            else if (entry.CreateCategories != null)
            {
                item.Menu = new Avalonia.Controls.NativeMenu();
                foreach (MainMenuCategory category in SafeInvoke(entry.CreateCategories))
                {
                    Avalonia.Controls.NativeMenuItem categoryItem = new(category.Header) { Menu = new Avalonia.Controls.NativeMenu() };
                    BuildNativeMenu(categoryItem.Menu, category.Entries);
                    item.Menu.Items.Add(categoryItem);
                }
            }

            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Builds one native item, carrying over the toggle state so a checkable entry is exported
    /// as a checkbox rather than as a plain label the user gets no feedback from.
    /// </summary>
    private Avalonia.Controls.NativeMenuItem CreateItem(MainMenuEntry entry)
    {
        Avalonia.Controls.NativeMenuItem item = new(entry.Header)
        {
            IsEnabled = entry.IsEnabled,
            IsChecked = entry.IsChecked,
            ToggleType = entry.ToggleType switch
            {
                MainMenuToggleType.CheckBox => Avalonia.Controls.MenuItemToggleType.CheckBox,
                MainMenuToggleType.Radio => Avalonia.Controls.MenuItemToggleType.Radio,
                _ => Avalonia.Controls.MenuItemToggleType.None
            }
        };

        if (entry.ExecuteAsync != null)
        {
            item.Click += async (_, _) =>
            {
                await entry.ExecuteAsync();

                // The exported menu is a snapshot, so the new state has to be published.
                // A checkbox is flipped in place, which reaches the panel as a property update
                // and leaves the open menu alone; a radio item needs its siblings unchecked, so
                // that case rebuilds.
                switch (entry.ToggleType)
                {
                    case MainMenuToggleType.CheckBox:
                        item.IsChecked = !item.IsChecked;
                        break;
                    case MainMenuToggleType.Radio:
                        RefreshMenu();
                        break;
                }
            };
        }

        return item;
    }

    /// <summary>One failing submenu builder should not leave the whole tray menu empty.</summary>
    private static System.Collections.Generic.IReadOnlyList<T> SafeInvoke<T>(
        Func<System.Collections.Generic.IReadOnlyList<T>> build)
    {
        try
        {
            return build();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Building a tray submenu failed.");
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _singleClickTimer.Stop();
        _singleClickTimer.Tick -= OnSingleClickTimerTick;
        _avaloniaIcon.Clicked -= OnAvaloniaClick;
        _avaloniaIconRegistration?.Dispose();
    }
}
