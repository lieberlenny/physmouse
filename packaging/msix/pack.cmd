@echo off
rem Builds bin\physmouse.msix for the Microsoft Store. Needs the Windows SDK (makeappx, makepri).
rem The Store signs the package on upload, so it is left unsigned here.
setlocal
cd /d "%~dp0..\.."
call .\build.cmd || exit /b 1

set SDK=
for /d %%d in ("%ProgramFiles(x86)%\Windows Kits\10\bin\10.*") do if exist "%%d\x64\makeappx.exe" set "SDK=%%d\x64"
if not defined SDK echo Windows SDK not found. & exit /b 1

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set LAYOUT=bin\msix
if exist %LAYOUT% rmdir /s /q %LAYOUT%
mkdir %LAYOUT%
"%CSC%" /nologo /target:exe /main:Icons /out:bin\icons.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs packaging\msix\Icons.cs || exit /b 1
bin\icons.exe %LAYOUT%\Assets || exit /b 1
copy /y bin\physmouse.exe %LAYOUT%\ >nul
copy /y packaging\msix\AppxManifest.xml %LAYOUT%\ >nul

"%SDK%\makepri.exe" createconfig /cf bin\priconfig.xml /dq en-US /pv 10.0.0 /o >nul || exit /b 1
"%SDK%\makepri.exe" new /pr %LAYOUT% /cf bin\priconfig.xml /mn %LAYOUT%\AppxManifest.xml /of %LAYOUT%\resources.pri /o >nul || exit /b 1
"%SDK%\makeappx.exe" pack /d %LAYOUT% /p bin\physmouse.msix /o >nul || exit /b 1
echo Built bin\physmouse.msix
