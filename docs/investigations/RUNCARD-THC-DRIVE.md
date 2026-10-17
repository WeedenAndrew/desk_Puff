# Run card — does a modified `thc/temp` drive a cycle

One cycle, run **30 °F cooler than normal**. The app writes once, then only reads.
**You start the cycle from the device's button** — the probe never sends `mc`.

Two independent questions:

1. Does a written `thc/temp` actually **drive** the heat cycle?
2. Does it **clear** when the session ends, or persist until a slot selection?

---

## ABORT

**Press and hold the device's button until it powers off.**

Note what it costs here: powering off logs `outcome=powered-off
clearing=unobserved` and you lose the second finding. Use it if anything is
wrong, obviously — but a cycle that ends on its own is the one that answers both
questions.

---

## 1. Build — desktop

```powershell
$dotnet = (Resolve-Path .\.tools\dotnet\dotnet.exe).Path

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet build   .\desk_Puff.slnx -c Release --no-restore
& $dotnet test    .\desk_Puff.slnx -c Release --no-build
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj -c Release --no-restore -p:PublishProfile=Windows-x64
```

Expect **222 tests passing**. NU1900 warnings are fine if they reappear — the
audit service comes and goes. NU1900 as an *error* means stop.

## 2. Assemble — desktop

```powershell
Copy-Item .\native\desk-puff-ble\target\release\desk-puff-ble.exe `
          .\artifacts\publish\win-x64\ble\ -Force
```

**Verify 671,744 bytes — 656 KB in Explorer.** 644 KB is the destroyed stale
build. Take it from `native\...\release\`, never from `artifacts`.

## 3. Transfer

Move the whole `win-x64` folder. `desk_Puff.exe` must be the **new** build — the
drive probe lives in it. No repo, no scripts, nothing else.

## 4. Preconditions

- **Puffco phone app closed.** One BLE central at a time.
- Device **idle and cool**. The probe refuses unless `stat/id` == `5`.
- `tmpo` and `timo` must both be **0** — a fresh session. The probe refuses
  otherwise and writes nothing.
- **Exactly one scan candidate.**
- Load a dab if you want the subjective check. 450 °F versus 480 °F is a
  difference you will notice, and it is independent of anything on screen.

## 5. Optional — frame-only check

```powershell
.\desk_Puff.exe --probe-thc-drive --trace-writes
```

Suppresses the write and exits without waiting. Confirms the frame and nothing
else. It makes no hardware claim and will say so.

## 6. The run

```powershell
.\desk_Puff.exe --probe-thc-drive
```

### Wait for this line before touching the device

```
THC DRIVE phase=4 waiting for Andrew to start the cycle physically
```

**Starting before it appears means the cycle runs at 480 °F and the run is
wasted.** After it appears you have roughly six minutes — 300 ticks at about a
second each — which is unhurried but not indefinite.

### Then

1. Start the cycle **from the device's button.**
2. **Look at the device.** It should show **450 °F** while still displaying
   profile 3. That is the whole experiment in one glance.
3. Let it run to the end on its own.

The probe writes `thc/temp` to about **232.222 °C**. The exact bytes are in the
log; they may differ from slot 0's stored `E4386843` by one least-significant bit,
which is expected and harmless — the probe compares against its own computed
value.

## 7. The line that decides it

**The first tick after `stat/id` becomes `7`.**

| `thc/temp` on that tick | meaning |
|---|---|
| **232.222** | the write survived into the cycle. Look for `finding=drive-observed`. |
| **248.889** | pressing the button reloaded the profile into `thc`. |

The second case is **not a failure.** It means the write has to land *during* the
cycle rather than before it, which is a real answer about how the feature would
have to work — and an awkward one, since the app would have a narrow window
between start and preheat.

### Read the ticks, not just the summary

`finding=drive-observed` requires `thc` and `tcmd` to equal the target
**exactly**. That is justified — `tcmd = thc/temp + tmpo` held exactly across 80
samples — but if the firmware rounds or ramps `tcmd` at all you will get
`drive=inconclusive` on a run that worked.

Every tick's raw `tcmd` is logged. If the summary says inconclusive, check
whether `tcmd` was simply close rather than equal. Don't let the summary line
talk you out of the evidence underneath it.

## 8. Outcomes

| log line | what it means |
|---|---|
| `finding=drive-observed` | `thc/temp` drove the cycle, with slot 3 still at 248.889 |
| `clearing=cleared-to-slot` | the last observed heating sample held the target, no departure was observed during heating, and the post-cycle sample holds the unchanged slot value |
| `clearing=persists-at-session-end` | it held the target at the post-cycle observation; this does not establish how long it persists |
| `clearing=unobserved reason=thc-left-target-during-cycle` | it had already left the target before cycle end; the final slot value cannot establish end clearing |
| `outcome=timeout no cycle observed` | the cycle never started. Nothing about drive either way. |
| `outcome=powered-off` | aborted. Clearing unobserved. |
| `outcome=disconnected` | link lost. **A disconnect is not evidence of clearing.** |

Both findings are reported separately and either can be inconclusive without
affecting the other.

Correction for `desk_Puff-20260912-193539.log`: THC reverted on the first `07`
sample, with `stat/elap=0.21 s`. The former `cleared-to-slot` summary was not
evidence of clearing at session end; under the corrected rule it is `unobserved`.
Use `stat/elap` for timing claims, not tick counts. Sequential samples bound a
transition window; they do not locate its exact instant.

After the readings are taken, the probe restores by selecting slot 3 again —
**after** the evidence is recorded, never before.

## 9. Bring back

The log, beside the executable as `desk_Puff-<yyyyMMdd-HHmmss>.log`. No serial
number is in it.

Worth adding from your own senses: whether the device displayed 450, and whether
the dab tasted like a 450 °F dab. That is the one piece of evidence the log
cannot contain.
