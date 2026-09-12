# Boost — the first real write

Single-run prompt. Paste below the line into Codex, in the `desk_Puff` root.

This is **`docs/SAFETY.md` hardware verification stage 7, "Boost validation."**
Stages 1–6 passed on 2026-08-31: allowlisted setting writes with read-back, the
stop command, and the start command at a factory profile. Stage 7 is the next
rung, not an analogy to one.

---

## What has already been established

Gate 2, on hardware, 2026-09-11.

**The trace frame is byte-correct and nothing transmitted.**

```
seq 05 02 │ 11 │ 00 00 │ 00 │ /p/app/tmpo │ 00 │ 55 55 05 41
                                                  = 8.333333 C
```

Zero transmissions in the whole log. A second suppression — an abort to
`/p/app/mc` — also held, so suppression is not path-specific.

**The clamp engaged on live data before any write.** `tmpo` already held
2.777778 (+5 F from a device-side boost), the app requested +10 F, and the
result landed on `8.333333` exactly — the ceiling, not past it.

**`tcmd = thc/temp + tmpo`, exactly, in all 80 watch samples.** `thc/temp` held
248.889 throughout and never moved. That relationship is measured, not inferred,
and it is why `snapshot.TargetTemperatureCelsius` — which reads `thc/temp` —
cannot see accumulated boost.

## The increment is +10 F, not the device's +5 F

`UserPreferencesStore.BoostTemperatureCelsius` supplies 5.555556 C. The
device's own `btmp` is 2.777778 C. **Both are correct**; a user preference may
differ from the profile's configured step. Do not "fix" this.

Expected from a fresh session with `tmpo = 0`:

| boost | tmpo | bytes | tcmd | |
|---|---|---|---|---|
| 1 | 0 → 5.555556 | `1CC7B140` | 254.445 | |
| 2 | 5.555556 → 8.333333 | `55550541` | 257.222 | **clamped** |
| 3 | refused | — | — | no representable increase remains |

`1CC73140` is the *device's* +5 F step and is **not** what the app sends. The
two differ in one nibble and decode to values that display alike.

---

## Stage 0 — read-only, and I already know the answers

Write nothing. Report:

1. The abort path. How does a session stop **from the device itself**, without
   the app? Quote whatever the repository says; if it says nothing, say so.
2. What `WriteAndVerifyAsync` does when read-back disagrees — the exact
   exception and whether it retries.
3. Whether anything in the boost path writes `/p/app/htr/tcmd`. Quote the
   search you ran.

Then stop.

---

## Stage 1 — the run card

**Deliverable:** a single page Andrew follows at the device. Not code.

He will be holding a heater. The card must be readable without scrolling back
through this prompt, and must state the abort **before** the first action.

It needs:

- **Abort first.** What to press on the device to stop a cycle, stated before
  any step. If the repository does not document this, say so plainly rather
  than inventing a gesture.
- Preconditions: phone app closed, fresh session so `tmpo = 0`, confirmed by
  reading it before boosting.
- The exact sequence: start a cycle on the device, wait for Active, one boost,
  observe, second boost, observe, third attempt, observe the refusal.
- After each step: what `tmpo`, `tcmd` and the device display should show.
- **Stop conditions.** Any of these ends the run immediately: `tcmd` not equal
  to `248.889 + tmpo`; `tmpo` above 8.333333; a write-verification failure; the
  device's displayed temperature disagreeing with `tcmd`; any behaviour not on
  the card.

**Watch the device, not the screen.** The screen reports what the app believes.

**Gate 1:** paste the card.

---

## Stage 2 — the run happened; one result needs explaining

`desk_Puff-20260911-202528.log`, 2026-09-11 20:25 MDT. Four writes, all
verified, all correct:

```
20:30:14  read tmpo=0         → write 1CC7B140 (5.555555 C)  verified
20:30:23  read timo=0         → write 00002041 (10.0 s)      verified
20:30:27  read tmpo=5.555555  → write 55550541 (8.333333 C)  verified, clamped
20:30:34  read timo=10.0      → write 0000A041 (20.0 s)      verified
20:30:36  BoostTime        BLOCKED  "safe per-session boost limit has been reached"
20:30:52  BoostTemperature BLOCKED  same reason
```

Read-modify-write confirmed on each. Stop, profile switch and start also worked.

### The thing to explain first

**`timo` stopped at 20 s against a documented 30 s ceiling.** One more 10 s boost
would have been exactly 30, which `MaximumBoostDuration` permits.

Both refusals came from `ValidateBoost`'s **count** check, not from either
ceiling. `SessionController.cs:14` holds a single `boostsApplied` counter, used
for both boost types at lines 97 and 105 and incremented at 162 after either
succeeds. Two temperature boosts plus two time boosts is four, and `4 < 4` is
false.

So the temperature refusal was correct **by accident** — `tmpo` was genuinely
sitting on 8.333333 and the cumulative check would have caught it — while the
time refusal cost a third of its range for an unrelated reason.

**This is a decision, not a defect, and it is Andrew's to make.** Report it; do
not change it. `MaximumConsecutiveBoosts` is a single hardcoded number and
nothing establishes whether the device means four boosts total or four of each.
The evidence says the device enforces the temperature ceiling itself and did not
object to the time boosts, but that is not the same as knowing its rule.

State plainly:

- Which gate fired, at which line, for each refusal.
- That the current behaviour is **safe but conservative** — it stops early, and
  it stops earlier the more of the other boost type you have used.
- That `ApplyBoostAsync` derives a second count from `tmpo` which never took
  effect, because `SessionController`'s gate runs first. Two counters exist for
  one limit.

**Gate 2:** the analysis and the explanation of the count gate. Stop there — the
write-up now depends on Stage 2b.

---

## Stage 2b — three of each, and there are two counters to fix

Andrew's decision, 2026-09-11. **Report the Stage 2 diagnosis at Gate 2 first;
this is what to do about it.**

### Correction to Stage 2

Stage 2 says `ApplyBoostAsync`'s derived count "never took effect." That was
true **only because the `SessionController` gate fired first at 4.** Raise that
gate and the second one becomes live — and it has the same defect.

`LoraxDeviceClient.cs:323-325`:

```csharp
double countIncrement = temperature ? increment : await ReadBoostValueAsync(
    LoraxPaths.ProfileBoostTemperature(live.ActiveProfileIndex), cancellationToken)...;
double count = Math.Round(temperatureApplied / countIncrement, ...);
```

For a **time** boost this divides `tmpo` by the profile's `btmp`. So after two
temperature boosts `tmpo` is 8.333333, `btmp` is 2.777778, and the derived count
for a *time* boost is 3. With the limit at 3, that refuses the third time boost
for a reason that has nothing to do with time.

**Splitting only the `SessionController` counter would not have reached 30 s.**
It would have worked if Andrew happened to boost time before temperature and
failed if he did the reverse, which is not a fix.

### What to change

**1. `SessionController` — two counters, limit 3 each.**

Replace `boostsApplied` (line 14). Reset **both** wherever it resets today,
lines 76 and 86; missing one leaves a counter that never clears. Update the
call sites at 97 and 105 and the increment at 162 so each type carries its own.

`DeviceSafetyPolicy.ValidateBoost` — `const int applicationLimit = 4` becomes 3.
`Math.Min(deviceLimit, applicationLimit)` **stays**, and
`CompatibilityCatalog`'s `MaximumConsecutiveBoosts: 4` does **not** change: that
is what the device is believed to allow, and `Math.Min` is what keeps the app
the more conservative of the two.

**2. `ApplyBoostAsync` — derive the time count from time.**

`LoraxPaths.ProfileBoostTime(index)` (`/u/app/hc/{i}/btim`) already exists and
is already read by `GetProfilesAsync` at line 148. Use `timo / btim` for a time
boost, mirroring `tmpo / btmp` for temperature.

The comment at lines 321-322 forbids deriving the time count "by dividing tmpo
by the requested time increment." That warning is about the *requested*
increment, not the profile's configured step, so `timo / btim` is consistent
with it — but the comment now reads as forbidding the fix. Rewrite it.

**Read `btim` on all four profiles and report the values before relying on
this.** `GetProfilesAsync` passes it through `SecondsOrZero` at line 194, so
**zero is a value the device can legitimately produce** and no fixture exercises
it. At zero the division makes `count` infinite and the guard at 326 throws,
breaking the time boost on a profile where it works today. State what happens
and guard it explicitly. Also move that guard **above** the division at 325; it
currently validates `countIncrement` after dividing by it and catches the result
only because `double.IsFinite` rejects Infinity.

### The unit of the derived count is device steps, not app boosts

`count = tmpo / btmp` counts the device's own increments. That is why two app
temperature boosts at +10 F consume **three** steps of 2.777778, and it is
correct: `MaximumConsecutiveBoosts` is the device's count of button presses, so
device steps is the right unit to compare against it.

The consequence is that whether three time boosts fit depends on hardware
`btim`, which the fixtures assert at 10 but no hardware read confirms:

| hardware `btim` | count before time boost 3 | limit 3 | limit 4 |
|---|---|---|---|
| 10 s | 20 / 10 = 2 | passes, reaches 30 s | passes, reaches 30 s |
| 5 s | 20 / 5 = 4 | refused | refused |

**If `btim` is not 10 on the active profile, recompute the run-card table before
Andrew touches the device.** A prediction built on the fixture value is not a
prediction.

**3. Report, do not change:** `SessionController` calls
`ValidateTemperatureBoost`/`ValidateTimeBoost` with three arguments, so
`appliedTemperatureCelsius` and `appliedDurationSeconds` take their default of
**0** — the cumulative ceiling comparisons at `DeviceSafetyPolicy.cs:273` and
`:305` are no-ops from the controller. They do real work only when
`ApplyBoostAsync` calls the same methods with live values at lines 349-350. That
is defensible, since the controller cannot read the device, but the code reads
as though the controller enforces a ceiling it does not. Add a comment saying
which layer owns that check. **No behaviour change here.**

### Why three

| | increment | ceiling | boosts to reach it |
|---|---|---|---|
| temperature | +10 F (5.555556 C) | +15 F (8.333333 C) | 2 — clamped on the second |
| time | 10 s | 30 s | 3 — exactly |

Three per type gives time exactly what it needs and temperature one slot it
cannot use.

**State the loosening in the commit message and in the code.** Total permitted
boosts go from 4 to 6. The argument is that both ceilings bind at or before
three increments, so the count is a backstop rather than the operative limit.
That argument holds **only while that remains true** — a smaller configured
increment makes the count bind again. Say so in the comment rather than leaving
the next reader to rediscover it.

### Tests

- Three time boosts reaching 30 s; a fourth refused.
- Two temperature boosts reaching 8.333333; a third refused **by the cumulative
  ceiling**, not by a count.
- **Two temperature boosts followed by three time boosts.** This is the case
  that fails today and the reason for change 2.
- The two counters proven independent, and `btim = 0` handled.

**Gate 2b:** the diff, the `btim` values, the test output, and the full build,
test and format results.

---

## Stage 2c — re-run on hardware

**Andrew runs this.** The Stage 1 run card applies. The sequence is now **two
temperature boosts, then three time boosts** — temperature first deliberately,
because that ordering is what the second counter breaks.

Expected, with `timo` starting at 0:

| step | write | | refused by |
|---|---|---|---|
| temp 1 | `1CC7B140` | 5.555555 C, tcmd 254.445, **490 F** | |
| temp 2 | `55550541` | 8.333333 C, tcmd 257.222, **495 F**, clamped | |
| temp 3 | — | refused | **ceiling** — "The cumulative boost ceiling has been reached." |
| time 1 | `00002041` | 10.0 s | |
| time 2 | `0000A041` | 20.0 s | |
| time 3 | `0000F041` | **30.0 s** | |
| time 4 | — | refused | **count** — "The safe per-session boost limit has been reached." |

The two refusals carry **different messages, and that is the check.** Time's
fourth is refused by the count because the count and the ceiling coincide at
exactly three — which is the whole argument for choosing 3. Temperature's third
is refused by the ceiling, and that refusal is the evidence that the ceiling
gate works on hardware rather than being masked.

**If temp 3 says "the safe per-session boost limit has been reached," stop.**
The split did not take.

**If any time boost is refused before reaching 30 s, stop** and record which
message it gave. That means the second counter is still reading `tmpo`.

Abort is press-and-hold until the device powers off.

**Gate 2c:** the log.

---

## Stage 2d — the write-up

Record in `docs/compatibility/`, following `VALIDATION_TEMPLATE.md`. `SAFETY.md`
requires results there **without serial numbers or Bluetooth addresses** —
check the log before quoting from it.

Record both runs: the 2026-09-11 20:25 run that stopped at 20 s on the shared
counter, and the re-run that reaches 30 s. The first is not a failed test; it is
how the shared counter was found, and a validation document that omits it loses
the reason the limit changed.

**Gate 2d:** the compatibility document.

---

## Stage 3 — the notes that are now wrong

Only after Stage 2 passes.

- `CompatibilityCatalog.cs:41` describes `MaximumBoostTemperatureCelsius` and
  `MaximumBoostDuration` as owner-stated from the phone app. Both are now
  observed on hardware — 8.333333 C and 30 s, reached and held. Say so. **Do not
  change the values.**
- The same comment says the `GetLimits` values' "wire location remains unknown."
  125 is now the measured maximum read payload: 126 rejected with status `0x7A`,
  125 and 124 accepted. 40 and 8 remain unknown, and 40 is ruled out as a path
  limit.
- The README's account of what works.

**Gate 3:** the diff.

## Out of scope

Do not write `/p/app/htr/tcmd`. The device derives it, and writing it directly
bypasses the ceiling entirely.

Do not change limit values or the firmware allowlist. Provenance improves;
numbers do not move without their own safety argument.

Do not commit. Andrew commits.
