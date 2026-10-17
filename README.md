<h1 align="center">ShareX for Linux</h1>

## Human written summary:
This is a ShareX fork that adds support for Linux. It only works on Wayland, and I specifically targeted Hyprland because that's what I use. I removed the "System" features that are present in upstream ShareX as I personally deem them unnecessary on Linux.

Everthing written below is by AI:

<h3 align="center">Screen capture, file sharing and productivity tool, ported to Wayland</h3>

This is a Linux port of [ShareX](https://github.com/ShareX/ShareX). It targets **Wayland only** —
X11 is not supported and no X11 fallbacks are kept. Everything that needed Win32 has been
replaced with its freedesktop counterpart, and the parts of ShareX that cannot exist on Wayland
have been removed rather than left as buttons that do nothing.

## How capture works here

A Wayland client cannot read the framebuffer, cannot see other applications' windows and cannot
grab a key combination for itself. These are deliberate design decisions in Wayland, not gaps to
work around, so ShareX asks the compositor instead, through
[xdg-desktop-portal](https://flatpak.github.io/xdg-desktop-portal/):

ShareX picks a capture path per compositor, and logs which one it chose at start-up:

| Compositor | Screen capture |
| --- | --- |
| **wlroots** — Hyprland, sway, river, Wayfire | `zwlr_screencopy` through **grim**. No portal, no permission dialog, and a region is grabbed directly instead of cropped out of the whole desktop. |
| GNOME, KDE, anything else | `org.freedesktop.portal.Screenshot` — the compositor renders the image and hands back a file, after asking the user. |

If the preferred path fails the other is tried, so a broken portal configuration does not stop
captures from working.

| What | How |
| --- | --- |
| Window capture | **Hyprland**: geometry and title from its IPC socket, so windows are listed in the menu and `%pn`/`%t` work. Elsewhere: the portal's interactive picker. |
| Screen recording | `org.freedesktop.portal.ScreenCast` publishes a PipeWire stream, GStreamer reads it, ffmpeg encodes it |
| Global hotkeys | `org.freedesktop.portal.GlobalShortcuts` — the compositor owns the bindings |
| Colour picker | `org.freedesktop.portal.Screenshot.PickColor` |
| Notifications | `org.freedesktop.portal.Notification`, falling back to `org.freedesktop.Notifications` |
| Printing | `org.freedesktop.portal.Print`, falling back to CUPS `lp` |
| Clipboard | `wl-copy` and `wl-paste`, which hold the selection in their own process |
| Opening files and folders | `org.freedesktop.FileManager1`, the OpenURI portal, then `xdg-open` |
| Keeping the screen awake while recording | `org.freedesktop.portal.Inhibit` |

The user grants each permission once, in their own compositor's dialog. ShareX never needs
elevated access to the display.

## Requirements

* A Wayland session with a working `xdg-desktop-portal` backend (`-kde`, `-gnome`, `-hyprland`,
  `-wlr`, …)
* .NET 10 runtime, unless you build with `--self-contained`
* `grim` — screen capture on wlroots compositors (Hyprland, sway, river, Wayfire). Without it,
  those compositors fall back to the portal, which often cannot take screenshots there.
* `wl-clipboard` (`wl-copy`, `wl-paste`) — copying images, text and files to the clipboard
* `ffmpeg` — screen recording, video conversion, video previews
* `gst-launch-1.0` with the PipeWire plugin — the screen-recording capture pipeline
  (ships with PipeWire itself)
* Optional: `tesseract` plus a language pack for OCR, `pw-play`/`paplay` for notification
  sounds, `lp` for printing, a colour emoji font such as Noto Color Emoji for the sticker tool

## Building and installing

```sh
# Build and run from the source tree
dotnet build ShareX.slnx
dotnet run --project ShareX

# Install for the current user into ~/.local
packaging/install.sh

# …or for everyone, bundling the .NET runtime
sudo packaging/install.sh --system --self-contained

# Remove it again
packaging/install.sh --uninstall
```

The installer publishes to `$XDG_DATA_HOME/ShareX`, puts a `sharex` launcher on `PATH`, and
installs the desktop entry, hicolor icons and AppStream metadata.

## Setting up hotkeys

ShareX registers its actions with the compositor the first time it runs, and your desktop asks
whether to allow them. **The combination you set inside ShareX is only a request** — the
compositor decides, and the user rebinds ShareX's shortcuts in their own keyboard settings
(System Settings → Shortcuts on KDE, Settings → Keyboard on GNOME).

If your desktop has no GlobalShortcuts portal, bind the shortcuts yourself in your compositor's
configuration and have them run ShareX's command line:

```sh
sharex -RectangleRegion     # capture a region
sharex -PrintScreen         # capture the whole screen
sharex -ActiveWindow        # pick a window to capture
sharex -ScreenRecorder      # start/stop recording
```

## What changed from upstream ShareX

### Removed because you asked

The **System** tool category is gone: borderless window, clipboard viewer, inspect window,
monitor test and network monitor. The in-app update check is gone too — ShareX is updated by
your package manager here, so it no longer prompts on launch.

### Removed because Wayland does not allow it

| Feature | Why |
| --- | --- |
| Scrolling capture | Needs synthetic key and scroll events delivered into another application's window |
| Mouse highlighter | Needs a global pointer hook |
| "Make active window borderless" / "always on top" | Needs to manipulate another application's window |
| Preconfigured window capture by title | A client cannot enumerate or name other windows (on Hyprland the Capture → Window menu does list them) |
| Window title and process name in filenames (`%pn`, `%t`) | Same reason — **except on Hyprland**, where its IPC socket supplies both |
| Hiding desktop icons before a capture | A shell-specific trick with no portable equivalent |
| Taskbar progress | No freedesktop standard; the tray icon still shows progress |
| Windows shell context menus, "Send to", Steam integration | Windows-only concepts |

Region capture keeps its overlay, magnifier and annotation tools. Hovering now snaps to
**monitors** rather than to other applications' windows, and clicking grabs that whole screen.

### Rebuilt on Linux equivalents

| Was | Now |
| --- | --- |
| Media Foundation recorder + D3D11 | ffmpeg fed from the ScreenCast portal over PipeWire |
| Media Foundation video player | An ffmpeg-backed player control drawn by Avalonia |
| DXGI/GDI screen capture | The Screenshot portal |
| Windows.Media.Ocr | Tesseract |
| DirectWrite/Direct2D emoji | Skia with the system colour emoji font |
| ONNX DirectML | ONNX Runtime on CPU, or CUDA/ROCm when installed |
| DPAPI for stored credentials | AES-GCM with a key file only your account can read |
| Registry deployment policy | `/etc/sharex.conf`, overridden by `$XDG_CONFIG_HOME/ShareX/policy.conf` |
| Registry MIME lookups | The freedesktop shared MIME database |
| Shell thumbnails | The freedesktop thumbnail cache |
| System.Drawing printing | Pages rendered to PDF, then the Print portal or `lp` |
| Win32 tray icon | Avalonia's TrayIcon over StatusNotifierItem |
| Win32 cursor handles | Cursors read from the user's Xcursor theme |
| `SystemParametersInfo` wallpaper | The existing Linux wallpaper service |
| Startup folder shortcut | An XDG autostart entry |
| `ShareX.exe` bundled ffmpeg | The system `ffmpeg` on `PATH` |

The webcam picture-in-picture overlay survived the move: it is now an ffmpeg filter graph over a
V4L2 device, with the same position, size, margin and circular-mask options.

## Project layout

`ShareX.Linux` is new: it holds the D-Bus portal clients, the PipeWire recording source and the
desktop services (sound, printing, MIME, thumbnails, secrets). Everything else keeps its
upstream name. `ShareX.ScreenRecordingLib`, `ShareX.Steam` and `ShareX.Setup` were deleted along
with the Windows backends.

## Known rough edges

* The region overlay is a fullscreen window on one output. A Wayland client cannot place a
  surface at a chosen desktop coordinate, so selecting a region spanning several monitors is
  not possible.
* The pointer position is only known while it is over a ShareX window, so "which monitor am I
  on" falls back to the primary screen when ShareX has not seen the pointer.
* The screen-recording controls float where the compositor puts them rather than framing the
  recorded region, because a client cannot place a window at a desktop coordinate.
* On the portal path, whether a screenshot is silent or asks every time is up to your backend:
  the KDE and GNOME backends remember the grant, `xdg-desktop-portal-gtk` asks each time. The
  grim path never asks.

### If screenshots do nothing on a wlroots compositor

Install `grim` — ShareX prefers it and will not touch the portal at all.

The portal path fails silently on Hyprland and sway more often than you would expect, because
`xdg-desktop-portal` picks its backend from `XDG_CURRENT_DESKTOP`. Setting that to `KDE` or
`GNOME` — a common trick to get those file dialogs — means `hyprland.portal`
(`UseIn=wlroots;Hyprland;sway;…`) is never matched, and the GTK backend chosen instead can only
take screenshots on GNOME. ShareX works out the real compositor from
`HYPRLAND_INSTANCE_SIGNATURE`, `SWAYSOCK` and `XDG_SESSION_DESKTOP` rather than trusting
`XDG_CURRENT_DESKTOP`, and records the backend it picked in the start-up log:

```
Compositor: hyprland (XDG_CURRENT_DESKTOP: kde)
Screen capture: wlr-screencopy (grim)
Clipboard: wl-clipboard
```

### Why the clipboard goes through wl-copy

ShareX spends most of its life as a tray icon with no window open. A clipboard owner needs a
surface to own the selection with and has to stay alive to serve the data it offered, so a
toolkit clipboard call from a windowless application reports success and delivers nothing.
`wl-copy` forks a small process that does both, which is also why a copied image survives ShareX
exiting. This applies under XWayland too, where its offer is bridged back to X11 clients.

## Licence

GPL-3.0-or-later, unchanged from upstream. See [LICENSE.txt](LICENSE.txt).
