# Microsoft Store (MSIX)

`pack.cmd` builds `bin\physmouse.msix`. It needs the Windows SDK for `makeappx` and `makepri`; the app itself still builds without it. The package is left unsigned because the Store signs it on upload.

`Name`, `Publisher` and `PublisherDisplayName` in `AppxManifest.xml` are the values under Partner Center > physmouse > Product identity (Store ID 9P31NQHKW1NR). Keep `Version` in step with `AssemblyVersion` in `src/Program.cs`. The fourth number must be 0.

When physmouse runs as the package, it:

- skips its own installer, uninstaller and the GitHub update check, because the Store handles these
- starts at sign-in through the package's startup task. The manifest leaves it off; the user turns it on with the toggle under General > Start with Windows (or under Settings > Apps > Startup), and the `DisableAutostart` / `ForceAutostart` policies set it at startup. There is no administrator startup option, so the cursor isn't remapped while an elevated window is in front.
- builds against the WinRT metadata in `%WINDIR%\System32\WinMetadata` for the startup task API, which `pack.cmd` references
- reads an existing `%APPDATA%\physmouse\settings.ini`; once it saves, Windows keeps the file under `%LOCALAPPDATA%\Packages\<family>\LocalCache\Roaming\physmouse`

To try it locally with Developer Mode on, run `Add-AppxPackage -Register bin\msix\AppxManifest.xml`. Close any other running physmouse first, because both use the same single-instance mutex. Remove it again with `Get-AppxPackage LennartBlees.physmouse | Remove-AppxPackage`.
