# Run card — boost to both ceilings

One page. Everything you need at the device is on it.

---

## ABORT

**Press and hold the device's button until it powers off.**

That is the whole escape hatch. There is no gesture that cancels a heat cycle
while leaving the device on. It drops the Bluetooth link too, so a connection
error in the app afterwards is expected, not a fault.

It works whether or not anything on the computer is responding. **Watch the
device, not the screen** — the screen reports what the app believes.

---

## Before you touch the device

**Desktop — build.** Repo root, PowerShell. Two separate commands; `--no-restore`
stays on the publish, or CI fails later with NU1004.

```powershell
$dotnet = (Resolve-Path .\.tools\dotnet\dotnet.exe).Path

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet build   .\desk_Puff.slnx -c Release --no-restore
& $dotnet test    .\desk_Puff.slnx -c Release --no-build
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64
```

The build must pass **without** `NuGetAudit=false`. If NU1900 still appears it
should now be a warning, not an error.

**Desktop — assemble.** The publish does not include the Bluetooth helper.

```powershell
Copy-Item .\native\desk-puff-ble\target\release\desk-puff-ble.exe `
          .\artifacts\publish\win-x64\ble\ -Force

Copy-Item .\tools\capture\Capture-DeviceNoise.ps1 `
          .\artifacts\publish\win-x64\ -Force
```

Take the helper from `native\...\target\release\`. The copy already sitting in
`artifacts` predates the write-path work.

`artifacts\publish\win-x64\` should hold `desk_Puff.exe`,
`Capture-DeviceNoise.ps1`, and `ble\desk-puff-ble.exe`. Move the whole folder to
the laptop, anywhere writable — the app writes its log beside itself.

**Close the Puffco phone app.** A BLE peripheral takes one connection at a time.

Device cool, charged, chamber attached, idle.

---

## Step 0 — the number that decides whether this run is worth doing

Read the active profile's **`btim`** (its configured time-boost step, shown as
the profile's boost duration).

| `btim` | do |
|---|---|
| **9 s or more** | continue |
| **8 s or less** | **stop.** The derived count will refuse the third time boost and you will not reach 30 s. |

Why: the device-step count is `round(timo / btim)` and must stay below 3. At
`btim = 8`, two boosts put `timo` at 20 and the count rounds to 3.

Then read **`tmpo`** and **`timo`**. Both must be **0**. If either is nonzero the
session is not fresh — stop the cycle, start a new one, read again.

Read **`thc/temp`** and write the number down. Every temperature check below is
against *that* value, not a remembered one.

---

## The sequence

Start a cycle **on the device**. Wait until the app shows Active.

Then, promptly — the session has to still be running at the end:

### Temperature first (this ordering is the point)

| # | expect written | `tmpo` | `tcmd` |
|---|---|---|---|
| 1 | `1CC7B140` | 5.555555 | `thc/temp` + 5.556 |
| 2 | `55550541` | 8.333333 — **clamped** | `thc/temp` + 8.333 |
| 3 | *nothing* | unchanged | unchanged |

Request 3 must be **refused, citing the ceiling**:

> the cumulative boost ceiling has been reached

### Then time

| # | expect written | `timo` |
|---|---|---|
| 1 | `00002041` | 10.0 s |
| 2 | `0000A041` | 20.0 s |
| 3 | `0000F041` | **30.0 s** |
| 4 | *nothing* | unchanged |

Request 4 must be **refused, citing the count**:

> the safe per-session boost limit has been reached

**The two refusals carry different messages, and that is the check.** Temperature
is stopped by the ceiling; time is stopped by the count at exactly the point the
ceiling is also reached.

---

## Stop the run immediately if

- **Temperature request 3 says "the safe per-session boost limit has been
  reached."** The counters did not split.
- **Any time request before the third is refused.** Record which message. It
  means the derived count is still binding.
- **A time request says "would exceed the verified session limit."** That is a
  third gate — the session duration, not the boost ceiling. Note the elapsed
  time and total when it happened.
- `tcmd` is not equal to `thc/temp` + `tmpo`.
- `tmpo` goes above 8.333333, or `timo` above 30.0.
- Any write logs a result other than `verified`.
- The device's displayed temperature disagrees with `tcmd`.
- Anything happens that is not on this card.

---

## Bring back

`desk_Puff-<stamp>.log` from beside the executable. No serial numbers are in it.

Nothing gets committed until that log is read.
