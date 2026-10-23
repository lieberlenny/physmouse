# Changelog

Each version has a git tag `v<version>`. The Microsoft Store package of a version carries the same number with a trailing `.0` (Store `1.2.1.0` is tag `v1.2.1`).

## 1.3.0 (2026-10-23)

### Added
- Admin policy under `HKLM\SOFTWARE\Policies\physmouse`: `DisableUpdateCheck`, `DisableElevatedStartup`, `DisableAutostart`, `ForceAutostart`, `DisableSelfInstaller` and `DefaultSettings`. See README > Security & enterprise.
- A Start with Windows toggle for the Store version under General.
- `--autostart` for silent installs.
- SECURITY.md, this changelog, and a Security & enterprise section in the README.
- Releases are built by GitHub Actions from the tag, with a build provenance attestation and the SHA-256 in the release notes.

### Changed
- Start with Windows is now off by default: in the setup wizard, for silent installs and in the Store package. Upgrades keep the current choice.
- Screens are numbered 1, 2, 3, … instead of using Windows' internal display numbers, which can jump to 11, 12, 13 after a driver update.

### Fixed
- The update check never reached GitHub because the app only offered TLS 1.0. It now uses TLS 1.2.
- After an update from the setup window, physmouse stayed off if the window was closed without clicking **Open physmouse**.
- Portable mode works again: a `settings.ini` next to the exe is read and written there instead of being moved to AppData. Portable copies from 1.2.0 and 1.2.1 pick up their settings from AppData once.

## 1.2.1 (2026-10-22)

### Fixed
- With the full-screen lock off, games that confine the cursor in exclusive full-screen (such as Minecraft) no longer send it into a corner of the screen when it reaches an edge. physmouse now leaves the cursor where the game keeps it.

## 1.2.0 (2026-10-18)

### Added
- Choose which open full-screen apps keep the cursor on their display. No apps are selected by default.
- An app picker with application icons and names.
- A step-by-step tutorial, which can be replayed from About.
- An update notice in About when a newer version is available.

### Changed
- Refined the layout editor, settings and dropdowns. Added keyboard focus indicators and improved scrolling and text readability.
- The main window opens in front of other windows and can be moved while the tutorial is open.
- Closing the main window keeps physmouse running in the notification area; starting with Windows opens it in the background.
- Settings are kept in AppData across updates and reinstalls, with an option to reset them.

## 1.0.2 (2026-10-18)

### Added
- Silent install and uninstall switches (`--install --quiet`, `--uninstall --quiet`) for package managers, and a WinGet manifest.

### Changed
- Silent uninstall never asks for UAC. It reports failure if Windows blocks removal of the elevated startup task.
- Package-manager upgrades and silent uninstalls keep the layout and settings.

## 1.0.1 (2026-10-18)

### Added
- Built-in installer and uninstaller: the downloaded exe offers to install itself per user and registers under Settings > Apps.

## 1.0.0

- First release (commit `bf412a5`, no tag).
