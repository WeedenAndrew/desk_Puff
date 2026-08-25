@echo off
REM ============================================================================
REM DEMO.cmd - build the demo and run it.
REM
REM The demo needs no Bluetooth, no hardware, and no Rust helper. It is the only
REM mode that currently runs end to end, so this is the way to poke at the front
REM end without any of that.
REM
REM Publishes a self-contained single-file exe, then launches it with --demo.
REM The exe stays put afterwards, so you can re-run it directly without
REM rebuilding: artifacts\publish\win-x64\desk_Puff.exe --demo
REM ============================================================================
setlocal
cd /d "%~dp0"

REM Prefer the workspace-local SDK, fall back to whatever is on PATH.
set "DOTNET=.tools\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

where %DOTNET% >nul 2>&1
if errorlevel 1 if not exist "%DOTNET%" (
  echo(
  echo    No .NET SDK found.
  echo    Expected .tools\dotnet\dotnet.exe, or "dotnet" on PATH.
  echo    The project targets .NET 10.
  goto :end
)

set "OUT=artifacts\publish\win-x64"
set "EXE=%OUT%\desk_Puff.exe"

echo(
echo ==== 1/2  publishing ====
echo    This is incremental. First run takes a while; later runs are quick.
echo(
%DOTNET% publish .\src\DeskPuff.App\DeskPuff.App.csproj ^
  -c Release -p:PublishProfile=Windows-x64
if errorlevel 1 (
  echo(
  echo    Publish failed. Nothing was launched.
  goto :end
)

if not exist "%EXE%" (
  echo(
  echo    Publish reported success but %EXE% is missing.
  echo    Check PublishDir in src\DeskPuff.App\Properties\PublishProfiles\Windows-x64.pubxml
  goto :end
)

echo(
echo ==== 2/2  launching --demo ====
echo(
echo    exe:  %CD%\%EXE%
echo    mode: demo. No Bluetooth is opened. No hardware is addressed.
echo(
echo    Re-run later without rebuilding:
echo        "%CD%\%EXE%" --demo
echo(

start "" "%EXE%" --demo

echo    Launched. This window can be closed.
echo(

:end
endlocal
pause
