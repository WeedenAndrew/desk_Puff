@echo off
REM ============================================================================
REM CAPTURE.cmd - read everything the connected Puffco will tell us.
REM
REM Wake the device and CLOSE THE PUFFCO PHONE APP first. A BLE peripheral
REM accepts one central at a time; while the phone holds it, this cannot connect.
REM
REM Read only. The frames it sends are 0x00 seed, 0x01 unlock, 0x10 read.
REM No write opcode is ever constructed. Nothing heats.
REM
REM Leaves two files beside this one:
REM   survey-<stamp>.log    decoded, the one to read
REM   frames-<stamp>.jsonl  every frame both ways, raw
REM ============================================================================
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Capture-DeviceNoise.ps1"
echo(
pause
