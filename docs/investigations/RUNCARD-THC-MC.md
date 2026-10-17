# THC mc-start diagnostic — Andrew runs hardware, never unattended

This mode can start heat. It does not implement saved-profile sessions. Use only
the characterised device, idle and cool, with the phone app closed and exactly
one scan candidate. Andrew's press-and-hold on the device is the physical escape;
do not rely on Bluetooth to stop heat. Stay with the device throughout the run.

## Build and transfer first

Desktop, repo root:

```powershell
$dotnet = (Resolve-Path .\.tools\dotnet\dotnet.exe).Path

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet build   .\desk_Puff.slnx -c Release --no-restore
& $dotnet test    .\desk_Puff.slnx -c Release --no-build
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64

Copy-Item .\native\desk-puff-ble\target\release\desk-puff-ble.exe `
          .\artifacts\publish\win-x64\ble\ -Force
```

Expect **251 tests passing** — that number is how you know the package contains
this mode. NU1900 warnings are fine; NU1900 as an *error* means stop.

**Verify the helper reads 671,744 bytes — 656 KB in Explorer.** 644 KB is the
destroyed 2026-08-26 build. Take it from `native\...\release\`, never from
`artifacts`. This has been wrong twice.

Move the whole `win-x64` folder to the laptop. `desk_Puff.exe` must be the new
build or the flag will not exist.

## Watch the device, not the screen

The device should display **450 °F while still showing profile 3** if the write
survived the start. That is the finding visible without reading anything, and it
is the only evidence the log cannot contain. Note what it showed.

A dab is optional — survival gives you a 450 hit, reversion a 480 one, so nothing
is wasted either way.

From a newly built complete package (including its `ble` helper), first inspect:

```powershell
.\desk_Puff.exe --probe-thc-mc --trace-writes
```

Trace mode constructs/logs/suppresses only the downward THC frame. Without a
verified write it cannot pass the gate to heat, so it exits without constructing
`mc`. This is not a simulation of successful hardware verification.

The live experiment, only when ready for the app to start the cycle:

```powershell
.\desk_Puff.exe --probe-thc-mc
```

Do not press the device button to start this experiment. The probe requires idle,
zero `tmpo`/`timo`, and a target 16.666667 °C (30 °F) below the original THC value
but not below the characterised minimum. It verifies the write, re-reads THC,
refreshes telemetry and applies the existing start policy before sending `mc=07`.
It does not write a slot or `htr/tcmd`.

Read three independent findings in the log beside the executable:

- `survivedMcStart`: the **first observed state 7** THC sample only. `survived`
  establishes survival of an mc start, not why physical-button start reloads it.
  `reverted-to-slot` disproves preservation in this run. A missing/incomplete
  first sample is unobserved/inconclusive, not replaced by a later sample.
- `drive=observed`: THC and `htr/tcmd` equal the target, `tmpo=0`, and the slot
  independently still holds its original, distinct temperature during heating.
- `clearing`: `cleared-to-slot` requires the target on the last observed heating
  sample, no observed departure during heating, and the unchanged slot value
  after the cycle. Departure during heating means `unobserved` for end clearing.
  `persists-at-session-end` describes that observation only, not eternal persistence.

Timing evidence must cite the logged `stat/elap` value, never convert tick counts
to seconds. Reads are sequential and sampled; gaps or non-finite values limit
what can be concluded. The September 12 physical-button run reverted at ignition,
so its post-cycle slot value does **not** establish clearing at session end.

Post-cycle THC and slot reads (phase 7) MUST complete before hcs restore (phase 8).
Normal completion has three writes: THC down, mc start, hcs restore. High tcmd
above the original THC triggers one stop attempt (`mc=08`) and ends without hcs.
Each write site is visited at most once; no mc retry. The upper budget is four,
but abort and normal restore are mutually exclusive in this sequence.

If an abort errors or loses its reply, its outcome is **unknown**: it may have
executed. Do not retry. Press-and-hold the device to abort physically. The log
includes this instruction on the unknown-outcome line. A stop reply alone is
not a guarantee of physical safety; use the physical escape if heat continues.
For any error, timeout, powered-off state, or link loss: stop the experiment,
check the device physically, and do not treat disconnection as clearing. No
cleanup hcs write occurs without completed post-cycle evidence. A timeout without
state 7/8 says only `no cycle observed`, not that the target failed to drive.

Bring back the complete log and the observed device behaviour. No firmware
conclusion is established by the fake-wire tests.
