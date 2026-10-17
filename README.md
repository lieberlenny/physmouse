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

1. Download `physmouse.exe` from the [latest release](../../releases/latest) and run it.
2. Click **Install**. physmouse installs for your user account only, into `%LOCALAPPDATA%\Programs\physmouse`, without needing administrator rights. You can change the folder and choose whether it starts with Windows and gets a desktop shortcut.
3. Open physmouse. The layout editor shows your screens placed as Windows has them.
4. Drag them to match your desk and click **Apply**.

physmouse runs in the notification area. Click its icon, or open it again from Start, to bring back the window.

To update, download the new `physmouse.exe` and run it. It offers to replace the installed version and keeps your settings and layout.

To uninstall, use Settings > Apps > Installed apps, or **Uninstall** on physmouse's About page. You can choose to keep your settings for a later reinstall.

If you'd rather not install anything, choose **Run portable** instead. physmouse then keeps its settings next to the exe and doesn't touch the Start menu or startup entries until you turn them on.

It needs Windows 10 or 11 and the .NET Framework 4.8, which is part of Windows.

## Settings file

Everything is stored in `%APPDATA%\physmouse\settings.ini`, a plain-text file that's easy to back up or copy to another PC. For a portable setup, put an empty `settings.ini` next to `physmouse.exe` and physmouse will use that instead.

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
