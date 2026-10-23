@echo off
rem Builds bin\physmouse.exe with the C# compiler that ships with Windows (.NET Framework 4.8),
rem then runs the tests. No SDK or Visual Studio needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
rem The WinRT metadata (Store startup task) ships with Windows 10 and later.
set WINMD=%WINDIR%\System32\WinMetadata
set REFS=/codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.dll /r:"%WINMD%\Windows.ApplicationModel.winmd" /r:"%WINMD%\Windows.Foundation.winmd"
cd /d "%~dp0"
if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /optimize /out:bin\physmouse.exe /win32icon:assets\physmouse.ico /win32manifest:assets\app.manifest %REFS% src\*.cs || exit /b 1
"%CSC%" /nologo /target:exe /main:Tests /out:bin\tests.exe %REFS% src\*.cs tests\Tests.cs || exit /b 1
bin\tests.exe || exit /b 1
echo Built bin\physmouse.exe
