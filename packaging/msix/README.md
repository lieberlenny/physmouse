# Microsoft Store (MSIX)

`pack.cmd` builds `bin\physmouse.msix`. It needs the Windows SDK for `makeappx` and `makepri`; the app itself still builds without it. The package is left unsigned because the Store signs it on upload.

`Name`, `Publisher` and `PublisherDisplayName` in `AppxManifest.xml` are the values under Partner Center > physmouse > Product identity (Store ID 9P31NQHKW1NR). Keep `Version` in step with `AssemblyVersion` in `src/Program.cs`. The fourth number must be 0.

When physmouse runs as the package, it:

- skips its own installer, uninstaller and the GitHub update check, because the Store handles these
- leaves start-at-sign-in to the package's startup task, which the user turns off under Settings > Apps > Startup. There is no administrator startup option, so the cursor isn't remapped while an elevated window is in front.
- reads an existing `%APPDATA%\physmouse\settings.ini`; once it saves, Windows keeps the file under `%LOCALAPPDATA%\Packages\<family>\LocalCache\Roaming\physmouse`

To try it locally with Developer Mode on, run `Add-AppxPackage -Register bin\msix\AppxManifest.xml`. Close any other running physmouse first, because both use the same single-instance mutex. Remove it again with `Get-AppxPackage LennartBlees.physmouse | Remove-AppxPackage`.
