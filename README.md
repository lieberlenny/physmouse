# physmouse

Windows lines your screens up by their pixels. If your screens differ in size, resolution or height on the desk, the cursor jumps up or down as it crosses from one to the next, or gets stuck where the pixel edges don't line up.

physmouse fixes that. You tell it where your screens actually sit on your desk, and it moves the cursor between them by real distance, so it leaves one screen and enters the next at the same physical spot.


## Features

- **Layout editor.** Drag screens to where they sit, or type exact positions and sizes in millimetres or inches. Edges snap to each other (hold Alt to place freely). Arrow keys nudge the selected screen by 1 mm, or by 10 mm with Shift. Physical sizes come from each screen's EDID, so you usually only need to arrange them.
- **Live cursor preview.** The editor shows where your cursor is in the layout you're editing, before you apply it.
- **Identify.** Shows a big number on every screen, so you can tell which tile is which.
- **Gaps and offsets.** Screens don't have to touch. Moving across a gap takes the cursor straight to the screen on the other side.
- **Edge behaviour.** Where there's no screen beyond an edge, the cursor either stops there or falls back to Windows' own layout.
- **Edge resistance.** Optionally require a short push past an edge before the cursor crosses, which keeps it from slipping off while you reach for a scroll bar.
- **Bypass key.** Hold Shift, Ctrl or Alt to cross the way Windows normally would.
- **Full-screen lock.** Keeps the cursor on a screen while a game or video fills it. On by default; Alt+Tab or the bypass key gets you out.
- **On/off shortcut.** A global keyboard shortcut that pauses and resumes remapping.
- **Start with Windows.** Optionally with administrator rights, so the cursor also follows your layout while an elevated window such as Task Manager is in front.
- **Remembers every screen.** Layouts are stored per physical screen, so unplugging a screen and plugging it back in keeps its place. When a new screen is connected, physmouse asks you to place it.
- Follows Windows' light or dark mode and your accent colour.

## Install

1. Download `physmouse.exe` from the [latest release](../../releases/latest) and put it somewhere permanent, for example `%LOCALAPPDATA%\Programs\physmouse`.
2. Run it. The layout editor opens with your screens placed as Windows has them.
3. Drag them to match your desk and click **Apply**.
4. Under **General**, turn on **Start with Windows** if you want it.

The first run also adds physmouse to the Start menu.

physmouse runs in the notification area. Click its icon, or open it again from Start, to bring back the window.

It needs Windows 10 or 11 and the .NET Framework 4.8, which is part of Windows. There is no installer. To remove it, set **Start with Windows** to Off, quit from the tray icon, and delete the exe, `%APPDATA%\physmouse` and the Start menu shortcut (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\physmouse.lnk`).

## Settings file

Everything is stored in `%APPDATA%\physmouse\settings.ini`, a plain-text file that's easy to back up or copy to another PC. For a portable setup, put an empty `settings.ini` next to `physmouse.exe` and physmouse will use that instead.

```ini
enabled = true
no-screen-beyond-edge = stop        # stop | windows
edge-resistance = 0                 # pixels to push before crossing
bypass-key = none                   # none | shift | ctrl | alt
lock-to-fullscreen = true
toggle-shortcut = ctrl+alt+f12
tray-icon = true
units = mm                          # mm | in

[layout]
# screen id   x   y   width   height   (mm; x/y is the top-left of the visible image)
LEN68F6\5&15fef2ea&1&UID4353  -97  -385  797  334   # Legion 34W-2c, 3440x1440
```

The screen id is the EDID product code plus the port the screen is connected to. If a screen moves to another port or the graphics driver renumbers it, physmouse still matches it by product code, as long as you don't have two identical screens.

## How it works

physmouse installs a low-level mouse hook on its own high-priority thread. When a mouse movement would take the cursor off its current screen, physmouse converts the exit point to millimetres on the desk, finds the screen across from it, and places the cursor at the matching point there. Movement within a screen passes straight through untouched.

The hook only sees input that Windows lets it see. Windows doesn't pass mouse input to a normal app's hook while an elevated app is in front, so without administrator rights physmouse can't remap the cursor then. That's what the "On, as administrator" startup option is for: it registers a sign-in task in Task Scheduler instead of a normal startup entry, so there's no UAC prompt at every sign-in.

## Building

```bat
build.cmd
```

The build uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so no SDK or Visual Studio is needed. It produces `bin\physmouse.exe` and runs the tests in `tests\Tests.cs`.

## License

[MIT](LICENSE)
