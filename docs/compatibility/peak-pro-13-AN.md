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

## Session-scoped parameters — found

Diagnostic probe, 2026-09-12 18:01 MDT, `desk_Puff-20260912-180059.log`. Device
idle throughout; no heat command issued. A trace run 40 seconds earlier confirmed
all six frames with nothing transmitted.

**`/p/app/thc/temp` and `/p/app/thc/time` are writable, and they are not the
profile slot.**

| write | value | status | read-back | slot 3 after |
|---|---|---|---|---|
| `thc/time` | `0000B442` — 90 s, identity | `0x00` | verified | 90 s — unchanged |
| `thc/time` | `0000A042` — **80 s** | `0x00` | verified, reads 80 | **90 s — unchanged** |
| `thc/temp` | `8EE37843` — 248.88889, identity | `0x00` | verified | 248.88889 — unchanged |
| `thc/temp` | `721C7643` — **246.11111** | `0x00` | verified, reads 246.11111 | **248.88889 — unchanged** |

So a `thc` write moves the live value and leaves `/u/app/hc/3/*` untouched. That
is the behaviour a one-off profile requires, and it rules out the alias case that
would have made the feature unimplementable.

**`hcs` populates `thc` from the selected slot.** Previously inferred from a
single sample; now demonstrated by forcing a transition:

| write | `thc/time` | `thc/temp` | |
|---|---|---|---|
| `hcs` = `00` | 120 | 232.22223 | slot 0's exact values — 450 °F, 120 s |
| `hcs` = `03` | 90 | 248.88889 | slot 3's exact values, restored |

The probe deliberately selects a *different* slot and returns, because an
identity `hcs` write may be a firmware no-op and would have made this step
unreadable either way.

### The button press discards a pre-start write

Drive probe, 2026-09-12 19:35 MDT, `desk_Puff-20260912-193539.log`. `thc/temp`
written down to 232.22221 (450 °F) while idle, then the cycle started **from the
device's button**. The app issued no `mc`.

The write **survived 54 polls and about 70 seconds of idle**, unchanged at
`E3386843` on every tick. Then:

| tick | time | `stat/id` | `thc/temp` |
|---|---|---|---|
| 53 | 19:37:01.461 | `05` idle | `E3386843` = 232.22221 |
| 54 | 19:37:02.765 | `07` preheating | `8EE37843` = **248.88889** |

At tick 54 `stat/elap` read 0.21 s and `htr/temp` 25.38 °C. `htr/tcmd` already
read 248.88889.

Ticks are sequential reads about 1.3 s apart, so they establish an observed
transition within that window, not an instant. The tight bound comes from
`stat/elap` instead — the device's own elapsed counter said the cycle was 0.21 s
old when `thc/temp` already read the slot value. So the reload happened within a
fifth of a second of ignition, on the device's clock rather than ours.

**Pressing the device's button reloads the active profile into `thc`, discarding
the written value, and the cycle runs at the slot temperature.** A pre-start write
is therefore not the mechanism — not because the write fails, but because
starting from the button undoes it.

`tcmd` equalled `thc/temp` exactly with `tmpo` at 0, which reconfirms
`tcmd = thc/temp + tmpo` and means `tcmd` tracks `thc` rather than latching
independently.

### Clearing at session end is still unobserved

The probe's summary reported `clearing=cleared-to-slot`. **That inference is not
supported by this run.** It is derived from the post-cycle read alone, and `thc`
had already reverted at ignition — 114 seconds before the session ended. The
value being at the slot temperature afterwards says nothing about session end,
because it never held the target during the session at all.

The finding logic cannot distinguish "cleared when the session ended" from "was
already cleared long before it ended." It has the data to — every tick was
logged — but it only examines the final value.

### An `mc` start preserves the write — the mechanism works

`--probe-thc-mc`, 2026-09-12 21:31 MDT, `desk_Puff-20260912-213137.log`.
`thc/temp` written down to 232.22221 (450 °F) while idle, re-read to confirm, then
the **app** started the cycle with `/p/app/mc` = 7. No button press.

| finding | result | evidence |
|---|---|---|
| survived the start | **survived** | `thc/temp` = 232.22221 at the first `stat/id` = `7` sample, `stat/elap` = 1.478 s |
| drove the cycle | **observed** | `htr/tcmd` = 232.22221, `tmpo` = 0, slot 3 still 248.88889 |
| cleared at session end | **persists** | `thc/temp` still 232.22221 after the cycle; last heating sample held the target at `stat/elap` = 89.73 s |

**The heater physically tracked the lower command.** `htr/temp` regulated between
229.5 and 232.2 °C across the session — 450 °F, not the profile's 480 °F. That is
independent of anything the app believed.

So the complete mechanism is: **write `thc/temp` while idle, then start with `mc`.**
`StartSessionAsync` writes only `mc` and never `hcs`, which is the difference from
a button press — the button appears to mean *select the current profile and
start*, and the selection is what discards the write.

### Clearing requires an explicit `hcs` write

**The value does not clear itself at session end.** It held 232.22221 through the
whole 90 s session and past the end of it.

What clears it is a `hcs` write, and an **identity write suffices** — phase 8
wrote `hcs` = `03` while `hcs` was already `03`, and `thc/temp` reloaded to
248.88889. An earlier concern that an identity `hcs` write might be a firmware
no-op is therefore disproven. Forcing a real slot transition in the write probe
was still the right call, since it made that result unambiguous either way.

### What this means for the saved-profile feature

`SessionOverride` documented its parameters as "applied when a session starts and
dropped when it ends." **The device does not drop them**, so that summary was
wrong and has been corrected in the source. `ClearSessionOverrideAsync` has to
perform the `hcs` write itself, and the application has to guarantee it runs.

That guarantee is the design problem, not the write. If desk_Puff crashes or the
link drops mid-session, the device keeps the override until something reselects.
One natural safety property helps: a **physical button start always reloads from
the slot**, so a stale override cannot survive the user starting a cycle
themselves. The exposed case is an app-initiated start against a stale override —
which argues for reading `thc` and comparing it to the slot before any `mc`, not
for trusting cleanup to have happened.

### Incidental

- **`/p/app/stat/tott` reads `0000807F` — positive infinity — while idle.**
  An earlier revision of this document claimed `SecondsOrZero` mapping that to
  zero is why the time boost's `SessionTotal + duration <= MaximumDuration` gate
  behaved correctly. **That was wrong.** The sentinel never reaches
  `SessionTotal`: snapshot construction takes `stateTotal` only while Preheating
  or Active and `profileDuration` otherwise, so while idle `SessionTotal` comes
  from `thc/time`. During the boost run the device was Active and `tott` read
  `0000B442` = 90.0, finite. The sentinel and that gate have never met.

  What does follow is that **the device uses sentinel values**, and not every
  consumer expects them. `thc/temp` is not finite-checked before entering
  `Snapshot.TargetTemperatureCelsius`, and the temperature display formats it
  without a check either, so a non-finite read would render as text rather than
  as unavailable.
- `/p/app/thc/name` returned a single byte, `70` (`p`), for a 32-byte request.
- `/p/sys/fw/ver` = `27000000` = 39, which encodes as "AN". Consistent with the
  allowlist entry.
- Slot 0 holds 450 °F and 120 s, corroborating the catalog's recorded survey.

## Colorway and vapor path discovery

Read-only candidate survey, 2026-09-13, `survey-20260913-190638.log` (idle) and
`survey-20260913-190846.log` (`stat/id` = `08`, actively heating). 21 candidate
paths, no writes. **Both runs returned identical results**, so nothing in this
group appears or disappears with session state.

### Status vocabulary, now complete

| status | meaning |
|---|---|
| `0x00` | exists, readable |
| `0x01` | exists, **write-only** — only `/p/app/mc` and `/p/app/ltrn/cmd` |
| `0x02` | does not exist |
| `0x7A` | limit exceeded |

No candidate returned `0x01`, so no new write-only control was found.

### Found

**`/p/app/thc/colr`** — the live heat cycle's lighting program, CBOR:

```
{"lamp": {"name": "migrtn1", "param": {22 keys, incl. "paths": [16 entries]}}}
```

**`/p/app/ltrn/colr`** — the lantern's program, same envelope, different program:

```
{"lamp": {"name": "pikaled2", "param": {11 keys, incl. "anim": 1,
                                        "color": h'75 bytes', "plNum": 0}}}
```

`color` is **25 consecutive RGB triplets** — a blue-to-cyan ramp — so the lantern
exposes directly editable colour data, where `thc/colr` uses a path-based
animation instead. The two are different program *kinds* under one envelope, not
the same structure with different values.

**`/p/app/thc/btmp`** = `1CC73140` = 2.777778 °C and **`/p/app/thc/btim`** =
`00002041` = 10.0 s — the **live session's** boost steps, mirroring the slot's.

### Absent — all `0x02`

Every vapor candidate, under both prefixes and all five guessed names:
`thc/vapr`, `thc/vpor`, `thc/vap`, `thc/lvl`, `thc/dose`, `thc/idx`, `vapo`,
`colo`, `lvlo`, `hc/0/vapr`, `hc/0/vpor`, `hc/0/vap`, `hc/0/lvl`, `hc/0/dose`,
`hc/0/pres`, `ltrn/rgb`, `ltrn/lvl`.

**Vapor has no Lorax path under any name tried.** The `<field>o` override pattern
that predicted `tmpo`/`timo` does not extend to colour or vapor either — `colo`
and `vapo` are both absent, so that rule appears specific to the two numeric
session overrides.

### Slot inventory, with the owner's phone-app vapor levels

| slot | name | blob 2026-08-31 | blob 2026-09-14 | vapor (phone app) |
|---|---|---|---|---|
| 0 | `DIAMONDS` | 517 B | **708 B** | Extreme — top level |
| 1 | `ROSIN` | 466 B | 466 B | **Bold** — one below top |
| 2 | `RESIN` | 517 B | 517 B | Extreme |
| 3 | `p` | 799 B | 799 B | Extreme |

**Blob sizes are not stable.** Slot 0 grew 191 bytes between those dates, so it was
edited in the phone app in between. Three slots matching exactly is what
validates the capture loop; the fourth changing is device history, not a bug. Any
code that hardcodes a blob length is wrong — read until the device returns a
short payload.

`/p/app/thc/colr` read **799 bytes with `hcs` = 3**, exactly slot 3's size, so the
session copy mirrors the active slot's whole lighting document just as
`thc/temp` and `thc/time` mirror its scalars. `/p/app/ltrn/colr` is 469 bytes.

Slot 3's name is a single byte, `0x70`. The device returned one byte for a
32-byte request, so the name is literally `p`.

### Pair by animation program, not by size or vapor alone

The documents decode to **two different animation programs**, and that governs
which comparisons mean anything:

| slot | program | vapor |
|---|---|---|
| 0 `DIAMONDS` | `migrtn1` | Extreme |
| 1 `ROSIN` | `pikaled2` | **Bold** |
| 2 `RESIN` | `pikaled2` | Extreme |
| 3 `p` | `migrtn1` | Extreme |

So the usable pairs are:

- **Test: slot 1 vs slot 2** — both `pikaled2`, differing vapor.
- **Control: slot 0 vs slot 3** — both `migrtn1`, same vapor.

Two earlier revisions of this document got this wrong. The first cited slots 0
and 2 having equal 517-byte lengths, which stopped being true when slot 0 grew to
708. The second kept 0-and-2 as a control on the grounds that both are Extreme —
but 0 is `migrtn1` and 2 is `pikaled2`, so that comparison is cross-program and
produced 110 differing leaf paths, more than the 108 in the supposed test. When a
control is noisier than its test, the design is measuring program type rather
than the variable of interest.

Under the cross-program pairing, zero vapor candidates survived. That result says
nothing about vapor; it says the pairing was wrong.

**Correcting the pairing did not rescue it, and that closes the approach.**
Re-run 2026-09-14 with slot 1 vs slot 2 as the test (both `pikaled2`, differing
vapor) and slot 0 vs slot 3 as the control (both `migrtn1`, same vapor): test 38
differing paths, control **102**. The control is still the noisier of the two.

The reason is structural and no third pairing fixes it: **every slot holds a
different mood.** Slots 0 and 3 are `ARABIAN LIGHTS` and `Ink`, so two `migrtn1`
documents differ across all 16 `paths` arrays regardless of vapor. No pair among
these four documents differs in vapor alone, so correlation cannot isolate it.

### Vapor is not in anything readable — closed

Single-variable experiment, 2026-09-14. Slot 2 `RESIN` changed from Extreme to
Bold **in the phone app**, nothing else touched. Captures `004800` before and
`041018` after.

Slot 2's document is **byte-identical across the two captures except one field**:

```
before   heatProfileDateModified  1A 6A 69 43 A2
after    heatProfileDateModified  1A 6A A7 C7 9F
```

`moodDateModified` is unchanged, as are the colour ramp, `colorLen`, mood name,
every ULID and `userColors`. All four scalars are also unchanged: `temp` 260 °C,
`time` 120 s, `btmp` 2.778, `btim` 10.

So a vapor change moves **one timestamp and nothing else** across every path this
survey reads. Combined with 15 probed path names all returning `0x02`, vapor is
not stored anywhere desk_Puff can currently see.

The timestamp bump does prove the app wrote something, so an unread path exists.
Finding it needs either further name guessing or a capture of the phone app's ACL
traffic — and the Android bugreport route is already known to strip ACL payloads,
leaving only rooting, which is disproportionate.

**This is closed rather than solved.** `ValidateSessionOverride` refuses any
override whose vapor is not `Standard`, which is correct regardless of where the
value lives. Temperature and duration are hardware-proven; vapor is a documented
limitation.

### What the profile documents actually contain

Decoded 2026-09-14 from the complete captures. Two program shapes:

| | `migrtn1` — slots 0, 3, `thc` | `pikaled2` — slots 1, 2, lantern |
|---|---|---|
| `lamp.param` | 22 keys, 16 `paths` | 11 keys |
| colour field | **`colors`** (plural), 12–18 B | **`color`** (singular) + `colorLen`, 60–75 B |
| mood names | `ARABIAN LIGHTS`, `Ink` | `MADE IN HEAVEN`, `DISCo`, `BLUE BOMBER` |

`/p/app/thc/colr` is **byte-identical** to `/u/app/hc/3/colr` at 799 bytes, so the
session copy is an exact mirror of the active slot's document.

No `CborReader` bound fired: the largest map or array was under 32, depth stayed
within 8, and the largest byte string was 75 bytes.

**The original solid-document replacement discarded 30 keys on a `migrtn1`
document and 18 on a `pikaled2` one.** The `meta` losses were the serious ones — `heatProfileUlid`,
`moodUlid`, `originalMoodUlid`, `version`, `moodType`, `tempoFrac`,
`moodDateModified`, `heatProfileDateModified`. Those are identity and provenance,
not decoration, and the phone app presumably uses them to recognise its own
profiles.

Also note the codec reads `lamp.param.color` **singular**, which `migrtn1`
documents do not have — they use `colors`. On those slots the palette is only
recovered because `meta.userColors` exists. The repaired write path no longer
sends an `EncodeSolid` replacement: it preserves unchanged animation documents,
allows only equal-size payload edits to an existing solid palette, and compares
the full readback byte-for-byte. Animation color changes are refused before
profile writes until their generation semantics are established. This repair
has managed fake-wire regression coverage, not new hardware confirmation.

The remaining hypothesis is that vapor is **not a path at all** but a key inside
the profile's CBOR blob. `/u/app/hc/{i}/colr` is 517 bytes of CBOR under the same
`lamp` envelope, and `ProfileLightingCodec` already parses part of it. Whether it
ignores keys it does not model is the next read-only question, and it needs no
device — the bytes are already captured.

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
