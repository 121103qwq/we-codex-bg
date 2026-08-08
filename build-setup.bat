@echo off
rem ============================================================================
rem  build-setup.bat  ->  dist\we-codex-bg-setup.exe  +  dist\we-codex-bg-portable.zip
rem
rem  Builds the helper and the UI first, then embeds them into a single-file
rem  installer.  Still nothing but the stock csc.exe - no Inno Setup, no NSIS.
rem ============================================================================
setlocal
set "ROOT=%~dp0"
set "BIN=%ROOT%bin"
set "DIST=%ROOT%dist"
set "ICON=%ROOT%assets\we-codex-bg.ico"

if not exist "%ICON%" (
  echo [setup] missing application icon: %ICON%
  exit /b 1
)

call "%ROOT%build.bat" || exit /b 1
call "%ROOT%build-ui.bat" || exit /b 1

if not exist "%DIST%" mkdir "%DIST%"

for %%F in ("%BIN%\we-codex-bg.exe" "%BIN%\we-codex-bg-ui.exe" "%ROOT%README.md") do (
  if not exist "%%~F" (
    echo [setup] missing payload: %%~F
    exit /b 1
  )
)

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%FW%\csc.exe"
set "WPF=%FW%\WPF"

echo [setup] compiling installer
"%CSC%" /nologo /optimize+ /platform:x64 /target:winexe /win32icon:"%ICON%" /out:"%DIST%\we-codex-bg-setup.exe" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  /reference:"%FW%\System.Xaml.dll" ^
  /resource:"%BIN%\we-codex-bg.exe",we-codex-bg.exe ^
  /resource:"%BIN%\we-codex-bg-ui.exe",we-codex-bg-ui.exe ^
  /resource:"%ROOT%README.md",README.md ^
  "%ROOT%src\Setup.cs"
if errorlevel 1 (
  echo [setup] FAILED
  exit /b 1
)

echo [setup] building portable zip
set "STAGE=%TEMP%\wecodexbg-portable"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
copy /y "%BIN%\we-codex-bg.exe"    "%STAGE%\" >nul
copy /y "%BIN%\we-codex-bg-ui.exe" "%STAGE%\" >nul
copy /y "%ROOT%README.md"          "%STAGE%\" >nul
if exist "%DIST%\we-codex-bg-portable.zip" del /f /q "%DIST%\we-codex-bg-portable.zip"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%DIST%\we-codex-bg-portable.zip' -Force"
rmdir /s /q "%STAGE%"

echo [setup] OK
echo         %DIST%\we-codex-bg-setup.exe
echo         %DIST%\we-codex-bg-portable.zip
exit /b 0
