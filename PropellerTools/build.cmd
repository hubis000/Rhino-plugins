@echo off
setlocal enabledelayedexpansion

rem ---------------------------------------------------------------------------
rem PropellerTools build script - Rhino 7, .NET Framework 4.8, x64
rem
rem Usage:  build.cmd [Release|Debug]
rem
rem Needs ONE of:
rem   * .NET SDK 6 or later   (sufficient on its own - no Visual Studio needed)
rem   * Visual Studio 2019+ or Build Tools, ".NET desktop development" workload
rem
rem NOTE ON STYLE: this script uses goto labels rather than parenthesised
rem if-blocks. cmd.exe pre-parses a whole ( ... ) block before executing it, and
rem %ProgramFiles(x86)% contains a ")" that terminates such a block early. Do
rem not refactor the flow below back into if ( ... ) blocks.
rem ---------------------------------------------------------------------------

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set ROOT=%~dp0
set SLN=%ROOT%PropellerTools.sln
set DIST=%ROOT%dist

echo === PropellerTools build (%CONFIG%, x64) ===

set BUILDER=
set BUILDKIND=

rem --- preference 1: dotnet SDK --------------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 goto try_vswhere
set BUILDER=dotnet
set BUILDKIND=sdk
for /f "usebackq tokens=*" %%v in (`dotnet --version 2^>nul`) do echo Found .NET SDK %%v
goto have_builder

rem --- preference 2: MSBuild via vswhere -----------------------------------
:try_vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" goto run_vswhere
set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" goto run_vswhere
goto try_msbuild_path

:run_vswhere
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "BUILDER=%%i"
if not defined BUILDER goto try_msbuild_path
set BUILDKIND=msbuild
goto have_builder

rem --- preference 3: msbuild already on PATH -------------------------------
:try_msbuild_path
where msbuild >nul 2>&1
if errorlevel 1 goto no_builder
set BUILDER=msbuild
set BUILDKIND=msbuild
goto have_builder

:no_builder
echo.
echo ERROR: no build tool found.
echo.
echo Install the .NET SDK - smaller than Visual Studio and sufficient alone:
echo     winget install Microsoft.DotNet.SDK.8
echo   or https://dotnet.microsoft.com/download  (pick the SDK, not the Runtime)
echo.
echo Company devices may need IT approval - raise a helpdesk ticket.
exit /b 1

:have_builder
echo Builder: !BUILDER!  (!BUILDKIND!)

rem --- build ---------------------------------------------------------------
echo.
echo --- restoring and building ---
if "!BUILDKIND!"=="sdk" goto build_sdk

"!BUILDER!" "%SLN%" -t:Restore,Build -p:Configuration=%CONFIG% -p:Platform=x64 -v:minimal
if errorlevel 1 goto build_failed
goto stage

:build_sdk
dotnet build "%SLN%" -c %CONFIG% -p:Platform=x64 -v minimal
if errorlevel 1 goto build_failed
goto stage

:build_failed
echo.
echo ERROR: build failed. See the output above.
echo   - restore/network failure: check proxy, or ask IT via the helpdesk
echo   - "RhinoCommon 7.13.21348.13001 not found": edit the three .csproj files
echo     and use whatever the current 7.x version is; any 7.x works here
exit /b 1

rem --- stage ---------------------------------------------------------------
:stage
echo.
echo --- staging to dist ---
if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%"

set PLUGOUT=%ROOT%src\PropellerTools.Plugin\bin\x64\%CONFIG%
set CUTOUT=%ROOT%src\PropellerTools.CuttingSurfaces\bin\x64\%CONFIG%

if exist "%PLUGOUT%\PropellerTools.rhp" goto do_copy

rem Output landed somewhere else - find it rather than guessing.
echo WARNING: PropellerTools.rhp not at the expected path:
echo   %PLUGOUT%
echo Searching...
for /f "usebackq delims=" %%f in (`dir /s /b "%ROOT%src\*.rhp" 2^>nul`) do echo   found: %%f
echo.
echo Adjust PLUGOUT and CUTOUT near the bottom of this script to match.
exit /b 1

:do_copy
copy /y "%PLUGOUT%\PropellerTools.rhp" "%DIST%\" >nul
copy /y "%PLUGOUT%\PropellerTools.Core.dll" "%DIST%\" >nul
copy /y "%CUTOUT%\PropellerToolsCuttingSurfaces.rhp" "%DIST%\" >nul

if /i not "%CONFIG%"=="Debug" goto check_version
if exist "%PLUGOUT%\*.pdb" copy /y "%PLUGOUT%\*.pdb" "%DIST%\" >nul
if exist "%CUTOUT%\*.pdb" copy /y "%CUTOUT%\*.pdb" "%DIST%\" >nul

rem --- sanity check: Core must be 1.0.0.0 ----------------------------------
:check_version
powershell -NoProfile -Command "$v=[Reflection.AssemblyName]::GetAssemblyName('%DIST%\PropellerTools.Core.dll').Version; Write-Host ('PropellerTools.Core version: ' + $v); if ($v.ToString() -ne '1.0.0.0') { Write-Host 'WARNING: the plugins bind to Core 1.0.0.0 - this will fail to load.' }"

rem --- clear the zone marker -----------------------------------------------
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%DIST%' -Recurse | Unblock-File"

echo.
echo === done ===
echo Output: %DIST%
dir /b "%DIST%"
echo.
echo Install: copy the ENTIRE dist folder somewhere permanent, then drag the
echo          .rhp files into Rhino 7 once. Do not move them afterwards.

endlocal
