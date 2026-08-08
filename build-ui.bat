@echo off
rem ============================================================================
rem  build-ui.bat   ->  bin\we-codex-bg-ui.exe
rem
rem  A modern WPF front-end for the we-codex-bg.exe helper.  Pure C#, no XAML, so
rem  it builds with the csc.exe that ships with .NET Framework 4.x - no toolchain
rem  or NuGet package to install.  Build the helper first (build.bat).
rem ============================================================================
setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%bin"
set "EXE=%OUT%\we-codex-bg-ui.exe"
set "ICON=%ROOT%assets\we-codex-bg.ico"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%ICON%" (
  echo [build-ui] missing application icon: %ICON%
  exit /b 1
)

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FW%\csc.exe"
if not exist "%CSC%" (
  echo [build-ui] csc.exe not found under %WINDIR%\Microsoft.NET - is .NET Framework 4.x installed?
  exit /b 1
)

set "WPF=%FW%\WPF"
for %%D in ("%WPF%\WindowsBase.dll" "%WPF%\PresentationCore.dll" "%WPF%\PresentationFramework.dll" "%FW%\System.Xaml.dll") do (
  if not exist "%%~D" (
    echo [build-ui] missing reference: %%~D
    echo            WPF assemblies are part of .NET Framework - install/repair .NET 4.x.
    exit /b 1
  )
)

echo [build-ui] %CSC%
"%CSC%" /nologo /optimize+ /platform:x64 /target:winexe /win32icon:"%ICON%" /out:"%EXE%" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  /reference:"%FW%\System.Xaml.dll" ^
  /reference:"%FW%\System.Windows.Forms.dll" ^
  /reference:"%FW%\System.Drawing.dll" ^
  "%ROOT%src\WeCodexBgUi.cs" "%ROOT%src\WeJson.cs"
if errorlevel 1 (
  echo [build-ui] FAILED
  exit /b 1
)
echo [build-ui] OK -^> %EXE%
exit /b 0
