# Boost validation: Peak Pro, model 13, firmware AN

`docs/SAFETY.md` hardware verification **stage 7, "Boost validation."** Stages
1–6 passed on 2026-08-31 — allowlisted setting writes with read-back, the stop
command, and the start command at a factory profile.

No serial numbers, Bluetooth addresses, pairing identifiers, or account details
appear below. Both source logs were checked before quoting.

## Environment

- Date: 2026-09-11 and 2026-09-12
- desk_Puff revision: **uncommitted working tree** — fill in the commit that
  carries the split boost counters once it exists
- Windows version: Windows 11
- Machine: Dell XPS 15 9510
- Bluetooth adapter: Intel(R) Wireless Bluetooth(R), driver version 24.40.0.3,
  reported as up to date
- Product: Peak Pro
- Model code displayed by desk_Puff: 13
- Firmware displayed by desk_Puff: AN
- Chamber: 3DXL (`/p/htr/chmt` = `02`)
- Active profile during both runs: slot 3, 90 s, `btmp` = +5 °F, `btim` = 10 s
- Battery at the start of the second run: 88.8 %

Two runs, because the first one found a defect and the second one validated the
fix. The first is not a failed test — it is how the defect was found, and the
limit change cannot be justified without it.

The Bluetooth helper was confirmed current: `desk-puff-ble.exe` at **671,744
bytes**, built 2026-08-28, matching `e418d52` "Authorize writes for one verified
device and firmware." A stale 659,456-byte build from 2026-08-26 was found staged
in `artifacts/` and destroyed; it was not what ran. The two differ by 12 KB and
by four commits to `native/`, so the byte size is the discriminator — 671,744 is
current, 659,456 is not.

## Run 1 — 2026-09-11 20:25 MDT

Four writes, every one read back and verified.

| time | path | read | written | value | result |
|---|---|---|---|---|---|
| 20:30:14 | `tmpo` | 0 | `1CC7B140` | 5.555555 °C | verified |
| 20:30:23 | `timo` | 0 | `00002041` | 10.0 s | verified |
| 20:30:27 | `tmpo` | 5.555555 | `55550541` | 8.333333 °C | verified, clamped |
| 20:30:34 | `timo` | 10.0 | `0000A041` | 20.0 s | verified |
| 20:30:36 | — | — | — | BoostTime | **refused** |
| 20:30:52 | — | — | — | BoostTemperature | **refused** |

Read-modify-write confirmed on each. Stop, profile switch and start also worked.

**`timo` stopped at 20 s against a documented 30 s ceiling.** Both refusals cited
*"The safe per-session boost limit has been reached."* — the **count** gate, not
either ceiling.

Cause: `SessionController` held a single `boostsApplied` counter shared by both
boost types, incremented after either one. Two temperature plus two time boosts
is four, against an application limit of four. The count gate is evaluated
before either ceiling check, so it masked them both.

A second count, derived inside `ApplyBoostAsync` as `tmpo / btmp`, was used for
*both* boost types — so temperature progress consumed the time boost's budget
there as well. It had no visible effect only because the first gate refused
earlier.

The temperature refusal was correct by accident: `tmpo` was genuinely at the
ceiling and the cumulative check would have caught it. The time refusal cost a
third of the range for an unrelated reason.

## Run 2 — 2026-09-12 03:10 MDT

Counters split per boost type, application limit 3 each, and the device-step
count derived from the matching override (`tmpo / btmp`, `timo / btim`).

`/u/app/hc/3/btim` read `00002041` = **10.0 s** fresh before each time boost.

| time | path | read | written | value | result |
|---|---|---|---|---|---|
| 03:11:56 | `tmpo` | 0 | `1CC7B140` | 5.555555 °C | verified |
| 03:11:57 | `tmpo` | 5.555555 | `55550541` | 8.333333 °C | verified, clamped |
| 03:12:00 | `tmpo` | 8.333333 | — | BoostTemperature | **refused — ceiling** |
| 03:12:05 | `timo` | 0 | `00002041` | 10.0 s | verified |
| 03:12:07 | `timo` | 10.0 | `0000A041` | 20.0 s | verified |
| 03:12:09 | `timo` | 20.0 | `0000F041` | **30.0 s** | verified |
| 03:12:10 | — | — | — | BoostTime | **refused — count** |

**Both ceilings reached.** `tmpo` at 8.333333 °C (+15 °F) and `timo` at 30.0 s,
each confirmed by read-back rather than by the value the application intended.

The two refusals cite different gates, which is the evidence that the counters
are independent:

- temperature: *"The cumulative boost ceiling has been reached."*
- time: *"The safe per-session boost limit has been reached."*

### Three limits coincide at the same instant

The profile was 90 s, so `/p/app/stat/tott` read `0000B442` = 90.0 before the
time boosts and 120.0 after the third. At that point:

| limit | value | reached |
|---|---|---|
| boost count | 3 per type | 3 time boosts done |
| boost ceiling | `MaximumBoostDuration` 30 s | `timo` = 30.0 |
| session duration | `MaximumDuration` 120 s | `tott` = 120.0 |

All three are satisfied simultaneously, and the count is reported because it is
evaluated first. **On a 120 s profile the session-duration gate refuses the very
first time boost** (`tott` + 10 > 120), so the 30 s ceiling is unreachable there.
The 30 s figure is validated on a 90 s profile and has not been exercised on any
other.

## Results

| Check | Result | Notes |
| --- | --- | --- |
| BLE discovery | Pass | |
| Lorax authentication | Pass | |
| Device identity | Pass | model 13, firmware AN |
| Chamber detection | Pass | 3DXL |
| Temperature boost write | Pass | read-modify-write, read-back verified |
| Temperature cumulative clamp | Pass | second boost cut to land on +15 °F exactly |
| Temperature ceiling refusal | Pass | cites the ceiling |
| Time boost write | Pass | read-modify-write, read-back verified |
| Time boost to full ceiling | Pass | 30.0 s, `0000F041` |
| Count refusal | Pass | cites the count |
| Counters independent per type | Pass | different gates fired for each type |
| `tcmd` = `thc/temp` + `tmpo` | Pass | held across both runs |
| No write to `/p/app/htr/tcmd` | Pass | none in either log |
| Stop, profile switch, start | Pass | |
| Reconnect after an abrupt drop | **Fail** | see below |
| Refusal appears in the diagnostic log | **Partial** | see below |

## Defects found

### The temperature refusal did not log as a refusal

The time refusal wrote both `WRITE BLOCKED action=BoostTime reason="..."` and an
exception line. The temperature refusal wrote **only**
`EXCEPTION context="Display operation error" type=DeviceSafetyException`.

`ApplyBoostAsync` throws `DeviceSafetyException` directly for the cumulative
ceiling, for a non-positive configured step, and for a bad increment, bypassing
the diagnostic write that `EnsureAllowed` performs. Refusals from
`SessionController` log; refusals from the device client do not.

Every hardware claim in this project rests on reading these logs. A safety
refusal that is not recorded as a refusal is only findable by noticing a
safety-typed exception where a block line belongs.

### The link dropped mid-cycle and could not be recovered for 40 seconds

At 03:10:57.130 `/p/app/stat/id` read `07` (Preheating) with `stat/elap` at
about 18.7 s. At 03:10:59.346 — 2.2 seconds later — the GATT session failed with
`HRESULT(0x800704C7)`, whose WinRT text is *"The operation was canceled by the
user."* No user action caused it, and no sidecar restart appears in the log.

Reconnection then failed three times:

| time | error |
|---|---|
| 03:11:14 | `Required characteristic 05434bca-… is missing` |
| 03:11:19 | `The selected e-rig is no longer in the scan results` |
| 03:11:21 | same |
| 03:11:25 | same |

It succeeded at 03:11:39. By 03:11:40 the device reported `05` (Idle) — the
cycle had ended on its own during the outage.

**The application lost observation and control of a running heater for
40 seconds.** It failed closed throughout: no write was attempted, and it
refused to proceed on an incomplete characteristic set rather than half-connect.
The subsequent start at 03:11:44 was correctly allowed against an idle device.
But during the outage desk_Puff could not have stopped the cycle, and the only
remaining control was the device's own button.

The cause is **not established.** Candidates are Windows radio power management,
the Bluetooth driver, the device's own BLE stack, and interference.

Three facts constrain it:

- The second cycle transitioned Preheating → Active without incident, so the
  state transition is not implicated on its own.
- It happened on the **current** helper, which already carries the bus listener
  added in `2da2bbb`. That listener does not prevent it.
- A similar `0x800704C7` at the end of the 2026-09-11 run occurred from state
  `06` (SelectingTemperature), which is consistent with the documented power-off
  abort and so may be a different mechanism entirely. Only the 2026-09-12 drop is
  known to have happened mid-cycle.

The adapter is Intel(R) Wireless Bluetooth(R) on driver 24.40.0.3, reported as up
to date. A current driver does not exonerate it — it only means the behaviour is
not fixed by updating.

**"Allow the computer to turn off this device to save power" was ENABLED on the
adapter when the drop occurred.** It was disabled on 2026-09-12, after the run.

Windows selective suspend on a Bluetooth radio drops active links and surfaces as
`ERROR_CANCELLED`, which is the error observed. So the setting is a plausible
cause and it is now removed — but **this is a candidate eliminated, not a defect
fixed.** Nothing has been retested. The next heat cycle with the app connected is
the first evidence either way, and a single clean cycle will not settle it: the
drop happened once in two runs, so absence in one run proves nothing.

If it recurs with selective suspend disabled, the remaining candidates are the
Intel driver, the device's own BLE stack, and interference.

The first failure mode — a stale cached scan candidate — is fixable: rescan
rather than reuse the candidate after an abrupt drop.

## What the limits are, and where they came from

| limit | value | provenance |
|---|---|---|
| `MaximumBoostTemperatureCelsius` | 8.333333 °C (+15 °F) | owner-stated 2026-08-31; **now reached and held on hardware** |
| `MaximumBoostDuration` | 30 s | owner-stated 2026-08-31; **now reached and held on hardware, on a 90 s profile** |
| application boosts per type | 3 | chosen 2026-09-11; total allowance 4 → 6 |
| `MaximumConsecutiveBoosts` | 4 | owner-stated; unchanged, still capped by `Math.Min` |
| `MaximumTemperatureCelsius` | 600 °F | owner-stated; **disputed — see below** |

+15 °F is a **delta**, not an absolute ceiling. A 480 °F profile boosts to 495 °F
and no further; reaching a higher absolute temperature requires a higher profile
target, not more boost.

**`MaximumTemperatureCelsius` stays at 600 °F.** 580 °F was raised as the
hardware ceiling on 2026-09-12, reviewed, and the 600 °F value retained — owner's
decision. Recorded because the two figures were once in conflict. No profile has
been written above 500 °F on hardware, so nothing between 500 and 600 °F has been
exercised either way.

**Also unresolved:** whether +15 °F is the device's own boost ceiling or only the
phone app's. The firmware clamps `tmpo` itself, so the two have never been
distinguished.

## Protocol notes

- Maximum read payload is **125 bytes**, measured: 126 rejected with status
  `0x7A`, 125 and 124 accepted. `0x7A` is the limit-exceeded status, which
  resolves an earlier ambiguity between a status byte and CBOR content.
- `GetLimits` (0x02) returns 125, 40 and 8 as u16. 125 is now accounted for.
  **40 and 8 remain unidentified.** 40 is ruled out as a path-length limit and is
  hypothesised as a maximum *write* payload, which matters because lighting
  writes are chunked at 80 bytes in `LoraxDeviceClient`.
- `tcmd` = `thc/temp` + `tmpo` exactly, across all samples in both runs.
  `thc/temp` never moves, which is why a snapshot's target temperature cannot
  observe accumulated boost.

## Outcome

- Boost validation result: **Pass**, with two defects recorded above.
- Compatibility-matrix update required: yes — boost ceilings are now
  hardware-observed rather than owner-stated.
- Follow-up work: log device-client refusals as refusals; rescan after an abrupt
  drop; settle 580 vs 600 °F; identify 40 and 8.
- Firmware allowlist changed: **No**
- Limit values changed: **No**
