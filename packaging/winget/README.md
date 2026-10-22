# Winget release checklist

The manifest under manifests/l/LieberLenny/physmouse/<version>/ is submitted to the Microsoft community source (microsoft/winget-pkgs, PR from the lieberlenny fork).

For a new version: move the folder to the new version, update PackageVersion in all three files, and point InstallerUrl and InstallerSha256 at the GitHub Release asset (physmouse-v<version>-installer.exe). Take the hash from the uploaded asset, not a local build.

The installer manifest invokes --install --quiet for silent and silent-with-progress installs, --install for interactive installs, and relies on the registered QuietUninstallString (--uninstall --quiet) for removal. Silent removal never requests UAC; it returns failure if Windows refuses removal of an elevated startup task. The current app installs per-user, without elevation.

Acceptance by the repository maintainers is required before users can find it in the default WinGet source.
