# Boost trace run — walkthrough

Gate 2 is done. This is the dry run: the app builds the boost frame, writes it
to a log, and **discards it before it reaches the radio**. Nothing changes on the
device.

---

## 1. Build — on the desktop

PowerShell, in the repo root.

```powershell
$dotnet = (Resolve-Path .\.tools\dotnet\dotnet.exe).Path

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64
```

Two separate commands, and `--no-restore` stays on the publish. Combining them
makes the SDK write `Microsoft.NET.ILLink.Tasks` into `packages.lock.json` and
CI fails later with NU1004.

Output lands in `artifacts\publish\win-x64\`.

## 2. Assemble the package — on the desktop

The publish does not include the Bluetooth helper. Copy it in:

```powershell
Copy-Item .\native\desk-puff-ble\target\release\desk-puff-ble.exe `
          .\artifacts\publish\win-x64\ble\ -Force

Copy-Item .\tools\capture\Capture-DeviceNoise.ps1 `
          .\artifacts\publish\win-x64\ -Force
```

Take the helper from `native\...\target\release\`. The older copy already inside
`artifacts` predates the write-path work.

`artifacts\publish\win-x64\` should now hold:

```
desk_Puff.exe
Capture-DeviceNoise.ps1
ble\desk-puff-ble.exe
```

## 3. Copy to the laptop

Move the whole `win-x64` folder. Anywhere writable — the app writes its log
beside itself.

## 4. Trace run — on the laptop

Close the Puffco phone app. A BLE peripheral takes one connection at a time.

```powershell
.\desk_Puff.exe --trace-writes
```

Check the log opens with `demoMode=False traceWrites=True`. If it says
`traceWrites=False`, stop — the flag did not take.

Then:

1. Start a heat cycle **on the device**.
2. Wait until the app shows Active.
3. Request **one** temperature boost in the app.
4. Close the app.

## 5. What the log must show

```
TRACE-WRITE SUPPRESSED path="/p/app/tmpo" offset=0 value=1CC73140 ...
WRITE VERIFICATION path="/p/app/tmpo" result=skipped reason=trace-write-suppressed
```

`1C C7 31 40` is 2.777778 °C — exactly +5 °F, which is what all four profiles
hold in `btmp`. `C1 CA 31 40` (2.778000) is also correct if the request came
through as a rounded value. **Both are fine.**

**And the device must not boost.** Watch the device, not the screen. If its
temperature or the boost indicator moves, suppression is broken and everything
stops there.

Stop and report if you see: a path other than `/p/app/tmpo`, a nonzero offset, a
payload that is not four bytes, a value near 250 instead of near 2.8, any write
to `tcmd`, or a missing suppression line.

## 6. Watch capture — second run

App fully closed first.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Capture-DeviceNoise.ps1 -WatchSession
```

Wait for `WATCH BEGIN`, start a cycle, boost from the device, let it run out.
This one now records `profile_C` — `/p/app/thc/temp` — so we can confirm it
stays at the profile setting while `tcmd` climbs.

## 7. Bring back

- `desk_Puff-<stamp>.log` from the trace run
- `survey-<stamp>.log` and `frames-<stamp>.jsonl` from the watch run

Nothing has been written to the device at this point. The first real write is
Gate 3, after these logs are checked.
