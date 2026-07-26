@echo off
rem ============================================================================
rem  run.bat "<path to project.json or mp4>" [extra options]
rem
rem  Examples:
rem    run.bat "C:\Program Files (x86)\Steam\steamapps\workshop\content\431960\1234567890\project.json"
rem    run.bat "D:\wall\clouds.mp4" --mode alpha --alpha 190
rem    run.bat ""                     (attach to a wallpaper window already open)
rem
rem  Ctrl+C in this console stops the helper and restores the Codex window.
rem ============================================================================
setlocal
set "EXE=%~dp0bin\we-codex-bg.exe"
if not exist "%EXE%" (
  echo [run] %EXE% missing - run build.bat first.
  exit /b 1
)

if "%~1"=="" (
  echo [run] no wallpaper given - attaching to an existing Wallpaper Engine window.
  "%EXE%" -v %2 %3 %4 %5 %6 %7 %8 %9
  exit /b %errorlevel%
)

"%EXE%" --wallpaper "%~1" -v %2 %3 %4 %5 %6 %7 %8 %9
exit /b %errorlevel%
