# ScreenOnlySleep

English | [日本語](README.ja.md)

ScreenOnlySleep turns your screens black with a hotkey, without putting the PC to sleep. Downloads, renders, streams and automation keep running. Press a key or click a mouse button to bring the screens back.

It is a small tray app for Windows 10 / 11.

## What it does

- Press the hotkey (default **Ctrl+Alt+F12**) or choose **Turn screens off now** from the tray menu. Every monitor is covered by a black window.
- Monitors that support DDC/CI also get their brightness set to 0, which turns the backlight down. The original brightness comes back when you release.
- Any real key press or mouse button press brings the screens back. That press is swallowed, so it doesn't reach the app underneath.
- As a safety net, the screens come back on their own after 12 hours (you can change this).

## Why moving the mouse does not wake the screens

Windows' own "turn off the display" wakes the screens on the slightest mouse movement, a bumped desk or a jittery sensor. ScreenOnlySleep never turns the display off as far as Windows is concerned. It only draws black windows over it and watches the input itself:

- Mouse movement and the scroll wheel are ignored.
- Synthesized input is ignored: anything sent with `SendInput` and similar APIs, which Windows flags as *injected*. This covers automation tools, key remappers and keep-awake utilities.
- F13–F24 and a few virtual-key codes that ordinary keyboards never produce are ignored too.
- After you press the hotkey, nothing counts until every key of the hotkey is up, so key repeat can't undo it right away.

The black windows are click-through, never take focus and are excluded from screen capture. A program that automates the desktop while the screens are dark still sees and clicks the real windows.

## Install

1. Download `ScreenOnlySleep-<version>.zip` from the Releases page and unzip it to a folder you keep, for example `%LOCALAPPDATA%\Programs\ScreenOnlySleep`.
2. Run `ScreenOnlySleep.exe`. A moon icon appears in the notification area.
3. To start it with Windows, open **Settings** from the tray menu and check **Start with Windows**.

It needs the .NET Framework 4.8, which is already part of Windows 10 (May 2019 Update or later) and Windows 11. Nothing else needs to be installed. Excluding the black windows from screen capture needs Windows 10 version 2004 or later.

The exe is not code-signed, so SmartScreen may warn the first time you run it.

## Uninstall

1. Open **Settings** and uncheck **Start with Windows**, then choose **Exit** from the tray menu.
2. Delete the folder that contains `ScreenOnlySleep.exe`.
3. Delete `%LOCALAPPDATA%\ScreenOnlySleep` (settings and log).

If you deleted the exe without unchecking **Start with Windows**, remove the `ScreenOnlySleep` value under both `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` and `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`.

## Settings

Open **Settings** from the tray menu.

| Setting | Default | Notes |
|---|---|---|
| Hotkey | Ctrl+Alt+F12 | Click the box and press the new combination. It needs Ctrl, Alt, Shift or Win plus another key. If another app already uses it, you are told so and can pick another. |
| Start with Windows | off | Adds or removes the `ScreenOnlySleep` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and the matching value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` that marks it as enabled for Task Manager's **Startup apps** (some Windows builds skip a Run entry without it). If you turned the app off in Task Manager, this box shows unchecked; checking it turns it back on. |
| Also set brightness to 0 (DDC/CI) | on | Monitors without DDC/CI are detected automatically and only get the black window. |
| Turn screens back on after | 12 hours | Safety net, 0.25 to 48 hours. |

Settings, the log and the saved brightness values are stored in `%LOCALAPPDATA%\ScreenOnlySleep\`:

- `settings.ini`: the settings above.
- `ScreenOnlySleep.log`: what released the screens, what input was ignored (capped) and foreground window changes. Key presses after the screens come back are not logged.
- `ddc-state.tsv`: the original brightness, written before it is changed. If the app is killed while the screens are dark, the next start puts the brightness back first. It is also restored when Windows shuts down.

If the hotkey cannot be registered at startup because another app already uses it, a notification tells you so. The tray menu still works.

### Command line

| Option | Meaning |
|---|---|
| `--restore-only` | Put back saved brightness values and exit. |
| `--data-dir <folder>` | Use another folder instead of `%LOCALAPPDATA%\ScreenOnlySleep`. |
| `--test-seconds <n>` | Blank once without the tray icon, come back after n seconds, then exit. |

## Known limitations

- The mouse cursor stays visible on top of the black screens.
- Mouse input still reaches the apps underneath while the screens are dark. Moving and scrolling go through, and the click that brings the screens back is swallowed. So be careful where the cursor is.
- Input from tools that inject it does not bring the screens back. This includes G HUB key assignments, AutoHotkey and Deskflow (or other keyboard/mouse sharing software). Use the real keyboard or mouse on that PC.
- Some notifications and other topmost windows can briefly appear in front of the black screens before they are covered again.
- Monitors without DDC/CI support keep their backlight on, since they are only covered by a black window. On those, the screen is black but still lit.
- DDC/CI brightness changes may be slow or ignored on some monitors, docks and adapters.
- ScreenOnlySleep does not keep the PC awake by itself. If your power plan puts the PC to sleep after some idle time, that still happens; change the plan if you need the PC to stay on.
- While a window running as administrator is in front, real key presses may not be seen (the app runs without administrator rights), so the screens may not come back from the keyboard. Use a mouse button, or switch away from that window first. Not confirmed on real hardware.

## Disclaimer

Use at your own risk. The software is provided "as is", without warranty of any kind; see [LICENSE](LICENSE).

## Build

You need the .NET 8 SDK (or later) on Windows. The project targets .NET Framework 4.8 through the reference-assembly packages, so Visual Studio is not required.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This runs the unit tests, builds the Release exe and creates `dist\ScreenOnlySleep-<version>.zip`. To run the tests only:

```powershell
dotnet test
```

Pushing a tag such as `v1.0.0` runs the GitHub Actions workflow, which builds the zip and attaches it with the exe to a new release.

## License

[MIT](LICENSE) © 2026 Nunifuchisaka
