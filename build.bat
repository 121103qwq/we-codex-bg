@echo off
rem ============================================================================
rem  build.bat [cpp|cs|auto]   ->  bin\we-codex-bg.exe
rem
rem   auto (default) : use a C++ toolchain if one is present, otherwise fall
rem                    back to the csc.exe that ships with .NET Framework 4.x
rem   cpp            : force the C++ build (MSVC / clang-cl / MinGW-w64)
rem   cs             : force the C# build  (no toolchain install needed)
rem ============================================================================
setlocal EnableDelayedExpansion
set "ROOT=%~dp0"
set "OUT=%ROOT%bin"
set "EXE=%OUT%\we-codex-bg.exe"
if not exist "%OUT%" mkdir "%OUT%"

set "WHAT=%~1"
if "%WHAT%"=="" set "WHAT=auto"

if /i "%WHAT%"=="cs" goto build_cs

rem ---------------------------------------------------------------- MSVC on PATH
where cl.exe >nul 2>nul && goto use_cl

rem ---------------------------------------------------------------- MSVC via vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
  if defined VSPATH if exist "!VSPATH!\VC\Auxiliary\Build\vcvars64.bat" (
    echo [build] using MSVC at !VSPATH!
    call "!VSPATH!\VC\Auxiliary\Build\vcvars64.bat" >nul
    goto use_cl
  )
)

rem ---------------------------------------------------------------- clang-cl
where clang-cl.exe >nul 2>nul && goto use_clang

rem ---------------------------------------------------------------- MinGW-w64
where x86_64-w64-mingw32-g++.exe >nul 2>nul && (set "GXX=x86_64-w64-mingw32-g++" & goto use_gxx)
where g++.exe >nul 2>nul && (set "GXX=g++" & goto use_gxx)

if /i "%WHAT%"=="cpp" (
  echo [build] no C++ toolchain found. Install "Desktop development with C++"
  echo         ^(VS Build Tools^) or MinGW-w64, or run: build.bat cs
  exit /b 1
)
echo [build] no C++ toolchain found - falling back to the C# build.
goto build_cs

rem ============================================================================
:use_cl
echo [build] cl.exe
rem /utf-8: the source carries Chinese log strings, so force UTF-8 source+exec charset
cl /nologo /std:c++17 /O2 /EHsc /W3 /utf-8 /DUNICODE /D_UNICODE ^
   "%ROOT%src\we_codex_bg.cpp" /Fo"%OUT%\\" /Fe:"%EXE%" ^
   /link user32.lib gdi32.lib dwmapi.lib shell32.lib advapi32.lib
if errorlevel 1 goto fail
goto done

:use_clang
echo [build] clang-cl.exe
clang-cl /nologo /std:c++17 /O2 /EHsc /utf-8 /DUNICODE /D_UNICODE ^
   "%ROOT%src\we_codex_bg.cpp" /Fo"%OUT%\\" /Fe:"%EXE%" ^
   /link user32.lib gdi32.lib dwmapi.lib shell32.lib advapi32.lib
if errorlevel 1 goto fail
goto done

:use_gxx
echo [build] %GXX%
rem note: no -municode, the program uses main() + GetCommandLineW/CommandLineToArgvW
%GXX% -std=c++17 -O2 -DUNICODE -D_UNICODE ^
   "%ROOT%src\we_codex_bg.cpp" -o "%EXE%" ^
   -luser32 -lgdi32 -ldwmapi -lshell32 -ladvapi32 -static-libgcc -static-libstdc++
if errorlevel 1 goto fail
goto done

:build_cs
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [build] csc.exe not found under %WINDIR%\Microsoft.NET - is .NET Framework 4.x installed?
  exit /b 1
)
echo [build] %CSC%
"%CSC%" /nologo /optimize+ /platform:x64 /target:exe /out:"%EXE%" "%ROOT%src\WeCodexBg.cs"
if errorlevel 1 goto fail
goto done

:fail
echo [build] FAILED
exit /b 1

:done
echo [build] OK -^> %EXE%
exit /b 0
