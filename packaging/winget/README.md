# Winget release checklist

The 1.0.2 manifest is prepared for the Microsoft community source. Its installer URL is the intended GitHub Release asset; it cannot be submitted until that asset exists.

The SHA-256 currently matches the locally built, unsigned bin/physmouse.exe (557F6CE8F122780DC56F83C9D1AA10E1C6BE7975A290EC4E3546B453FFC3CD81). If the release binary is Authenticode-signed, recalculate and replace the hash after signing, then validate the manifests and install/uninstall in a clean Windows Sandbox. The asset may be renamed to physmouse-1.0.2-setup.exe; renaming does not change the hash.

The installer manifest invokes --install --quiet for silent and silent-with-progress installs, --install for interactive installs, and relies on the registered QuietUninstallString (--uninstall --quiet) for removal. Silent removal never requests UAC; it returns failure if Windows refuses removal of an elevated startup task. The current app installs per-user, without elevation.

Submit the three YAML files under manifests/l/LieberLenny/physmouse/1.0.2/ in a fork of microsoft/winget-pkgs, run winget validate and the repository's SandboxTest, then open a PR. Acceptance by the repository maintainers is required before users can find it in the default WinGet source.