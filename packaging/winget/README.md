# Winget release checklist

The manifest under manifests/l/LieberLenny/physmouse/<version>/ is submitted to the Microsoft community source (microsoft/winget-pkgs, PR from the lieberlenny fork).

For a new version: move the folder to the new version, update PackageVersion in all three files, and point InstallerUrl and InstallerSha256 at the GitHub Release asset (physmouse-v<version>-installer.exe). Do this in the release commit, so the tag carries the matching manifest. The hash can only be set after CI has built the release (builds aren't byte-identical), so leave the zero placeholder there and fill in the hash from the uploaded asset, not a local build, when submitting to winget-pkgs.

The installer manifest invokes --install --quiet for silent and silent-with-progress installs (start with Windows stays off; --autostart would turn it on), --install for interactive installs, and relies on the registered QuietUninstallString (--uninstall --quiet) for removal. Silent operations never request UAC. Exit code 2 means the legacy elevated startup task remains: a quiet install completed but needs task cleanup, while a quiet uninstall made no changes. Map 2 in the next manifest's `ExpectedReturnCodes` if the deployment should treat this state as successful; otherwise, run `physmouse.exe --remove-legacy-task` as SYSTEM before deployment. That SYSTEM command cannot restore a user's startup entry.

Acceptance by the repository maintainers is required before users can find it in the default WinGet source.
