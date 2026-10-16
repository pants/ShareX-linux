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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShareX.Linux.Portals;

namespace ShareX.HelpersLib;

/// <summary>
/// Registers ShareX's hotkeys with the compositor through org.freedesktop.portal.GlobalShortcuts.
/// </summary>
/// <remarks>
/// Wayland has no way for a client to grab a key combination for itself, by design: a background
/// application that could claim arbitrary keys would be a keylogger. The compositor owns the
/// bindings instead. ShareX declares each action it supports along with the combination the user
/// configured as a <em>preferred</em> trigger, and the desktop decides what to honour. Users
/// rebind ShareX's shortcuts in their own desktop settings, and the bindings they end up with
/// are read back so the hotkey list can show them.
/// </remarks>
public sealed class PortalHotkeyHost : IHotkeyHost
{
    /// <summary>
    /// Registering is batched: ShareX adds hotkeys one at a time during start-up, but every
    /// BindShortcuts call replaces the whole set and may prompt, so the calls are coalesced.
    /// </summary>
    private static readonly TimeSpan bindDelay = TimeSpan.FromMilliseconds(250);

    private readonly object syncRoot = new();
    private readonly Dictionary<string, HotkeyInfo> hotkeysById = [];
    private readonly GlobalShortcutsPortal portal = new();
    private CancellationTokenSource? pendingBind;
    private bool initialized;

    public event HotkeyEventHandler? HotkeyPress;
    public event EventHandler? Closed;

    public bool IsDisposed { get; private set; }

    public int HotkeyRepeatLimit { get; set; }

    /// <summary>The triggers the compositor reports for each registered action, for display.</summary>
    public IReadOnlyDictionary<string, string> ActiveTriggers { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>Raised after the desktop reports that the bindings changed.</summary>
    public event EventHandler? TriggersChanged;

    public void Initialize()
    {
        if (initialized || IsDisposed) return;

        initialized = true;

        portal.Activated += OnActivated;
        portal.ShortcutsChanged += OnShortcutsChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (!await portal.IsSupportedAsync().ConfigureAwait(false))
            {
                DebugHelper.WriteLine(
                    "Global shortcuts are unavailable: this desktop does not implement the GlobalShortcuts portal. " +
                    "ShareX actions can still be bound to keys in the desktop's own keyboard settings by " +
                    "running \"sharex -<job name>\", for example \"sharex -RectangleRegion\".");
                return;
            }

            await portal.StartSessionAsync().ConfigureAwait(false);
            await RefreshTriggersAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Starting the global shortcuts session failed.");
        }
    }

    public void RegisterHotkey(HotkeyInfo hotkeyInfo)
    {
        if (IsDisposed || hotkeyInfo == null) return;

        lock (syncRoot)
        {
            hotkeysById[GetShortcutId(hotkeyInfo)] = hotkeyInfo;
        }

        // The compositor decides whether the preferred trigger is granted, and says so later.
        hotkeyInfo.Status = HotkeyStatus.Registered;
        ScheduleBind();
    }

    public bool UnregisterHotkey(HotkeyInfo hotkeyInfo)
    {
        if (hotkeyInfo == null) return false;

        bool removed;

        lock (syncRoot)
        {
            removed = hotkeysById.Remove(GetShortcutId(hotkeyInfo));
        }

        hotkeyInfo.Status = HotkeyStatus.NotConfigured;

        if (removed) ScheduleBind();

        return removed;
    }

    public void Close()
    {
        if (IsDisposed) return;

        IsDisposed = true;

        try
        {
            pendingBind?.Cancel();
            portal.Activated -= OnActivated;
            portal.ShortcutsChanged -= OnShortcutsChanged;
            portal.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Closing the global shortcuts session failed.");
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Close();

    /// <summary>Coalesces a burst of registrations into a single BindShortcuts call.</summary>
    private void ScheduleBind()
    {
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;

        lock (syncRoot)
        {
            previous = pendingBind;
            pendingBind = cancellation;
        }

        previous?.Cancel();
        previous?.Dispose();

        _ = BindAfterDelayAsync(cancellation.Token);
    }

    private async Task BindAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(bindDelay, cancellationToken).ConfigureAwait(false);

            List<GlobalShortcutDefinition> definitions;

            lock (syncRoot)
            {
                definitions = hotkeysById
                    .Select(entry => new GlobalShortcutDefinition
                    {
                        Id = entry.Key,
                        Description = entry.Value.ToString(),
                        PreferredTrigger = ToPortalTrigger(entry.Value)
                    })
                    .ToList();
            }

            if (definitions.Count == 0) return;

            await portal.BindAsync(definitions, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RefreshTriggersAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later registration.
        }
        catch (PortalCancelledException)
        {
            DebugHelper.WriteLine("The user declined the global shortcuts request.");
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Binding global shortcuts failed.");
        }
    }

    private async Task RefreshTriggersAsync()
    {
        try
        {
            IReadOnlyList<(string Id, string? Trigger, string? Description)> shortcuts =
                await portal.ListAsync().ConfigureAwait(false);

            ActiveTriggers = shortcuts
                .Where(shortcut => !string.IsNullOrEmpty(shortcut.Trigger))
                .ToDictionary(shortcut => shortcut.Id, shortcut => shortcut.Trigger!, StringComparer.Ordinal);

            TriggersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Reading the bound global shortcuts failed.");
        }
    }

    private void OnActivated(object? sender, GlobalShortcutActivatedEventArgs e)
    {
        HotkeyInfo? hotkeyInfo;

        lock (syncRoot)
        {
            if (!hotkeysById.TryGetValue(e.ShortcutId, out hotkeyInfo)) return;
        }

        HotkeyPress?.Invoke(hotkeyInfo.ID, hotkeyInfo.KeyCode, hotkeyInfo.ModifiersEnum);
    }

    private void OnShortcutsChanged(object? sender, EventArgs e) => _ = RefreshTriggersAsync();

    /// <summary>
    /// A stable identifier for an action. The compositor remembers the user's binding against
    /// this string, so it must not change between runs: the hotkey's numeric id is reused across
    /// sessions by <see cref="HotkeyManager"/>, which makes it a good key.
    /// </summary>
    private static string GetShortcutId(HotkeyInfo hotkeyInfo) =>
        $"sharex.hotkey.{hotkeyInfo.ID}";

    /// <summary>
    /// Converts a hotkey to the freedesktop shortcuts syntax, e.g. "CTRL+SHIFT+p". Returns null
    /// when the combination has no equivalent, leaving the compositor to pick or ask.
    /// </summary>
    internal static string? ToPortalTrigger(HotkeyInfo hotkeyInfo)
    {
        if (!hotkeyInfo.IsValidHotkey) return null;

        StringBuilder trigger = new();

        if (hotkeyInfo.Control) trigger.Append("CTRL+");
        if (hotkeyInfo.Alt) trigger.Append("ALT+");
        if (hotkeyInfo.Shift) trigger.Append("SHIFT+");
        if (hotkeyInfo.Win) trigger.Append("LOGO+");

        string? key = ToPortalKeyName(hotkeyInfo.KeyCode);

        if (key == null) return null;

        trigger.Append(key);

        return trigger.ToString();
    }

    /// <summary>
    /// Maps a key to the name the shortcuts specification uses, which follows the XKB keysym
    /// names rather than the Windows virtual-key names ShareX stores.
    /// </summary>
    private static string? ToPortalKeyName(InputKey key) => key switch
    {
        >= InputKey.A and <= InputKey.Z => key.ToString().ToLowerInvariant(),
        >= InputKey.D0 and <= InputKey.D9 => key.ToString()[1..],
        >= InputKey.F1 and <= InputKey.F24 => key.ToString().ToUpperInvariant(),
        InputKey.PrintScreen => "Print",
        InputKey.Insert => "Insert",
        InputKey.Delete => "Delete",
        InputKey.Home => "Home",
        InputKey.End => "End",
        InputKey.PageUp => "Prior",
        InputKey.PageDown => "Next",
        InputKey.Up => "Up",
        InputKey.Down => "Down",
        InputKey.Left => "Left",
        InputKey.Right => "Right",
        InputKey.Space => "space",
        InputKey.Return => "Return",
        InputKey.Tab => "Tab",
        InputKey.Escape => "Escape",
        InputKey.Back => "BackSpace",
        InputKey.Pause => "Pause",
        _ => null
    };
}
