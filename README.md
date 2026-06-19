# Taskbar Opacity Controller

[中文说明](README.zh-CN.md)

Windows 10 taskbar visibility controller.

The current implementation is performance-first: no fade animation, no full window enumeration, no child-window alpha updates, and no forced redraw loops. It only switches the main taskbar alpha when the desired state changes.

## Behavior

| Situation | Taskbar |
| --- | --- |
| Desktop / Win+D / Show Desktop | Transparent |
| Any normal app window visible and not minimized | Visible |
| Mouse in bottom 40px of a screen | Visible |
| Start menu | Visible |
| Search | Visible |
| Task View | Visible |
| Alt+Tab | Visible |

## Usage

Run from the project folder:

```powershell
dotnet run
```

Or run the published `TaskbarOpacityController.exe`.

After launch, the tray icon appears as:

```text
Taskbar Dock Controller
```

## Tray Menu

Right-click the tray icon:

| Item | Action |
| --- | --- |
| Show taskbar now | Immediately restores the taskbar |
| Start with Windows | Toggles startup for the current Windows user |
| Exit | Restores the taskbar and exits |

## Startup

`Start with Windows` writes to:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

It does not require administrator permission.

## Notes

- This version favors low CPU and smooth system performance over animation.
- If the taskbar stays visible, check whether the mouse is inside the bottom 40px hover area.
- The tool changes taskbar alpha only. It does not modify Windows taskbar auto-hide settings.

## Publish

Release builds are configured as self-contained single-file win-x64 executables.

```powershell
dotnet publish -c Release
```

The runnable exe is written under:

```text
bin\Release\net8.0-windows\win-x64\publish\
```
