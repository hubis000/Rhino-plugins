@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo ==========================================================
echo  MejzlikEdgeCut - Rhino 7 plug-in build
echo ==========================================================
echo.

set "PROJ=MejzlikEdgeCut.csproj"
set "CONFIG=Release"
set "OUTDIR=bin\%CONFIG%"
set "RHP=%OUTDIR%\MejzlikEdgeCut.rhp"

if not exist "%PROJ%" (
    echo ERROR: %PROJ% not found.
    echo Put this .cmd file in the same folder as the .csproj.
    goto :fail
)

REM ----------------------------------------------------------------
REM  Replace the placeholder GUID on first build.
REM  Two plug-ins sharing a GUID collide in Rhino, so this must not ship.
REM ----------------------------------------------------------------
set "PLACEHOLDER=6f2c1d84-7a13-4c9e-9b21-3f5d8e0a4b77"
findstr /C:"%PLACEHOLDER%" MejzlikEdgeCutPlugIn.cs >nul 2>&1
if not errorlevel 1 (
    echo Placeholder plug-in GUID found. Generating a unique one...
    where powershell >nul 2>&1
    if errorlevel 1 (
        echo   WARNING: powershell not found. GUID left as the placeholder.
        echo   Edit MejzlikEdgeCutPlugIn.cs by hand before distributing.
    ) else (
        powershell -NoProfile -ExecutionPolicy Bypass -Command ^
          "$g=[guid]::NewGuid().ToString(); $f='MejzlikEdgeCutPlugIn.cs'; $t=[IO.File]::ReadAllText($f); $t=$t.Replace('%PLACEHOLDER%',$g); [IO.File]::WriteAllText($f,$t); Write-Host ('   New GUID: '+$g)"
        if errorlevel 1 echo   WARNING: GUID replacement failed. Check the file by hand.
    )
    echo.
)

REM ----------------------------------------------------------------
REM  Find a build tool. dotnet CLI first, MSBuild from Visual Studio second.
REM ----------------------------------------------------------------
set "BUILDER="
where dotnet >nul 2>&1
if not errorlevel 1 set "BUILDER=dotnet"

if not defined BUILDER (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "!VSWHERE!" (
        for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
            set "MSBUILD=%%i"
        )
    )
    if defined MSBUILD set "BUILDER=msbuild"
)

if not defined BUILDER (
    echo ERROR: no build tool found.
    echo.
    echo Install one of:
    echo   - .NET SDK 6 or newer      https://dotnet.microsoft.com/download
    echo   - Visual Studio 2019/2022 with the .NET desktop workload
    echo.
    echo Either way you also need the .NET Framework 4.8 targeting pack,
    echo which the Visual Studio installer offers under Individual Components.
    goto :fail
)

REM ----------------------------------------------------------------
REM  Build
REM ----------------------------------------------------------------
if "%BUILDER%"=="dotnet" (
    echo Using dotnet CLI.
    echo.
    echo --- restore ---
    dotnet restore "%PROJ%"
    if errorlevel 1 goto :buildfail
    echo.
    echo --- build ---
    dotnet build "%PROJ%" -c %CONFIG% --nologo
    if errorlevel 1 goto :buildfail
) else (
    echo Using MSBuild: !MSBUILD!
    echo.
    echo --- restore ---
    "!MSBUILD!" "%PROJ%" /t:Restore /v:minimal /nologo
    if errorlevel 1 goto :buildfail
    echo.
    echo --- build ---
    "!MSBUILD!" "%PROJ%" /p:Configuration=%CONFIG% /v:minimal /nologo
    if errorlevel 1 goto :buildfail
)

echo.
if not exist "%RHP%" (
    echo Build reported success but %RHP% is missing.
    echo Check that the csproj still sets ^<TargetExt^>.rhp^</TargetExt^>,
    echo and look in %OUTDIR% for a .dll instead.
    goto :fail
)

echo ==========================================================
echo  BUILD OK
echo ==========================================================
echo.
echo   %CD%\%RHP%
echo.
echo To install: drag that file onto an open Rhino 7 window,
echo or run _PlugInManager in Rhino and use Install.
echo.
echo Rhino locks the .rhp while it is loaded. If a rebuild fails
echo with "file in use", close Rhino first.
echo.
echo Then type MejzlikEdgeCut at the Rhino command line.
echo.

explorer /select,"%CD%\%RHP%"
goto :done

:buildfail
echo.
echo ==========================================================
echo  BUILD FAILED
echo ==========================================================
echo.
echo Common causes, in the order worth checking:
echo.
echo  1. RhinoCommon package version. The csproj pins an example
echo     7.x version that was never verified. Check nuget.org for
echo     the newest 7.* release and edit the PackageReference.
echo     Do not use 8.x - it will not load in Rhino 7.
echo.
echo  2. Missing .NET Framework 4.8 targeting pack.
echo.
echo  3. Compiler errors in the source. This code has never been
echo     compiled. RhinoCommon overload signatures are the likely
echo     culprits, especially the GetNumber option calls in
echo     MejzlikEdgeCutCommand.cs and Curve.Split in EdgeCutCore.cs.
echo.
goto :fail

:fail
echo.
pause
exit /b 1

:done
pause
exit /b 0
