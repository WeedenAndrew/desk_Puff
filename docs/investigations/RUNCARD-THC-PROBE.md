# Run card — THC probe

Two runs: a trace run that transmits nothing, then the real one. Build on the
desktop, run on the laptop.

The question: are `/p/app/thc/temp` and `/p/app/thc/time` writable, and are they
distinct from the profile slot? Nothing here heats the device.

---

## ABORT

**Press and hold the device's button until it powers off.** Unchanged.

The probe never issues `mc`, so there is no heat cycle to stop — but the abort
still ends the Bluetooth link if anything looks wrong.

---

## 1. Build — desktop

PowerShell, repo root. Separate `restore` and `publish`, `--no-restore` stays on
the publish, or CI fails later with NU1004.

```powershell
$dotnet = (Resolve-Path .\.tools\dotnet\dotnet.exe).Path

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet build   .\desk_Puff.slnx -c Release --no-restore
& $dotnet test    .\desk_Puff.slnx -c Release --no-build
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64
```

Expect 208 tests passing and 8 NU1900 warnings. **NU1900 must be a warning, not
an error** — if it errors, stop; the `Directory.Build.props` change has regressed.

## 2. Assemble — desktop

The publish does not stage the Bluetooth helper, and `artifacts\...\ble\` is
empty on purpose since the stale 2026-08-26 copy was destroyed.

```powershell
Copy-Item .\native\desk-puff-ble\target\release\desk-puff-ble.exe `
          .\artifacts\publish\win-x64\ble\ -Force
```

**Verify it reads 671,744 bytes — 656 KB in Explorer.** 659,456 bytes / 644 KB is
the stale build and must not ship.

`Capture-DeviceNoise.ps1` is **not needed** for this run. The probe does its own
logging through the managed write path, which is the whole point.

## 3. What transfers

| from | to | note |
|---|---|---|
| `artifacts\publish\win-x64\desk_Puff.exe` | laptop | **must be the new build** — the probe lives here |
| `artifacts\publish\win-x64\ble\desk-puff-ble.exe` | laptop `ble\` | 671,744 bytes |
| everything else in `win-x64\` | laptop | runtime DLLs, `.pdb`, config |

Simplest: move the whole `win-x64` folder. Anywhere writable — the app writes its
log beside itself.

Nothing else transfers. No repo clone needed on the laptop, no source, no scripts.

## 4. Preconditions

- **Puffco phone app closed.** A BLE peripheral takes one central at a time.
- Device **idle and cool**, chamber attached, charged.
- **Exactly one scan candidate.** The runner refuses if it sees more than one
  device, so no other Puffco base within range.
- Nothing else needs to be running.

## 5. Trace run — transmits nothing

```powershell
.\desk_Puff.exe --probe-thc --trace-writes
```

Only those two flags may be combined; any third argument is refused before
anything happens.

**What the log must show:**

- six `LORAX WRITE FRAME` lines, each followed immediately by
  `TRACE-WRITE SUPPRESSED`
- six `WRITE VERIFICATION ... result=skipped reason=trace-write-suppressed`
- `THC PROBE result=trace-only hardwareEvidence=none`
- `writesAccepted=unknown` or `false` in the summary — **`true` here would be a
  bug**, since nothing transmitted
- **no `mc` frame anywhere**

Expected frame values:

| # | path | value | bytes |
|---|---|---|---|
| 1 | `thc/time` | identity — 90 s or 120 s | `00 00 B4 42` / `00 00 F0 42` |
| 2 | `thc/time` | −10 s → 80 s or 110 s | `00 00 A0 42` / `00 00 DC 42` |
| 3 | `thc/temp` | identity — 248.889 | `8F E3 78 43` |
| 4 | `thc/temp` | −5 °F → 246.111 | read from the log |
| 5 | `hcs` | alternate slot | one byte, not `03` |
| 6 | `hcs` | original slot | `03` |

The probe also logs `THC PROBE VALUE path=... value=` in round-trip decimal, so
every number is readable without decoding hex.

**Stop and report** if a write transmits, a seventh write appears, any path other
than those three shows up, or an `mc` frame exists.

**Send me this log before the real run.**

## 6. Real run

```powershell
.\desk_Puff.exe --probe-thc
```

**Do not touch the device's button while it runs.** The idle check cannot close
the gap between checking `stat/id` and issuing the write. This is the only
hazard left in the design and the mitigation is that you keep your hands off it.

Exit code `0` means the sequence completed; `1` means it refused or halted. Either
is a result.

### What the outcomes mean

| observation | conclusion |
|---|---|
| a write returns a **non-zero status** | `thc` is not writable. Conclusive — the hypothesis is dead and we stop guessing. |
| `alias-detected` in the log | the slot value moved. `thc` is an alias for the slot, **not** the one-off mechanism. Also conclusive, and the outcome most worth catching. |
| all six verified, slots unchanged, `reselectRestored*=true` | the paths are writable and distinct from the slot. **Suggestive, not proof.** |

Step 5 selects the first slot whose parameters sit inside `DeviceLimits`,
probably slot 0 at 450 °F. If it picks slot 2 at 500 °F, `thc/temp` will **rise**
in the log. That is ordinary profile selection through the already-allowlisted
`hcs` path — the same operation validated in stages 1–6 — not a `thc` write, and
the never-increase invariant is untouched.

### What this run cannot establish

Even on a clean result it does not show that a modified `thc/temp` is **applied**
to a session, or that it **clears** when the session ends. Both need a heat cycle
at the lowered value, which is a separate gate. Do not write it up as
"session-scoped."

## 7. Bring back

- the trace log
- the real log
- the exit code

Both logs live beside the executable as `desk_Puff-<yyyyMMdd-HHmmss>.log`. Neither
contains a serial number.
