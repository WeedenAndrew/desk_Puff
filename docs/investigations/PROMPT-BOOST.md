# Implement independent boost

Single-run prompt. Paste everything below the line into Codex, in the `desk_Puff`
repository root.

Five stages, five gates. **Stop at each gate and paste the output.**

---

## This one writes to a heater

Every prior prompt in this project was read-only. This is not.

`docs/SAFETY.md` sets out an eight-stage ladder. On 2026-08-28 two stages were
skipped because the earlier ones had gone well, and read-only evidence was used
to justify write authorisation. The gate that held was the one demanding a fact
rather than a permission. Do not skip a stage here because the specification
looks complete.

**No stage before Gate 3 transmits anything.** Stage 2 runs under
`--trace-writes`, which constructs and logs the frame and then discards it
before it reaches the radio.

## Standing constraints

- **No git commits, no pushes.** Andrew commits.
- Never claim something works without running it. Paste real output.
- No attribution trailers.
- `dotnet build`, `test`, and `format --verify-no-changes` stay green.
  `TreatWarningsAsErrors` is set, so analyzer output is load-bearing.
- Build serially with `-m:1` if parallel Avalonia projects collide on
  `buildtasks.log`.

## What the device does, measured 2026-09-11

A real session with three boosts applied from the device itself:

```
tmpo     timo      tcmd        state
0.000    0.000   248.889       8      base target, 480 F
2.778   10.000   251.667       8      boost 1
5.556   20.000   254.444       8      boost 2
8.333   30.000   257.222       8      boost 3, ceiling
```

| | |
|---|---|
| paths | `/p/app/tmpo` (Celsius), `/p/app/timo` (seconds) |
| format | f32 little-endian — `LoraxValueCodec.WriteSingle` |
| semantics | **cumulative total, not increment** |
| increment | the active profile's `btmp` / `btim`; 2.778 C and 10 s on profile 0 |
| valid state | `DeviceOperatingState.Active` (8) only |
| ceiling | 8.333 C and 30 s, reached at three boosts |
| side effect | the device recomputes `tcmd` itself — **never write `tcmd`** |

Both paths are already in `LoraxPaths.ExactWritePaths`. Both answer reads with
`STATUS=0x00`, so unlike `ModeCommand` and `LanternMode` these writes **can** be
verified by read-back. Do not add them to `PathsWithoutReadBackVerification`.

---

## Stage 0 — read-only, and I already know the answers

Write nothing. Report:

1. The two lines in `LoraxDeviceClient` that make boost fail today, quoted.
2. What `BoostTemperatureAsync` passes as `boostsAlreadyApplied`, and where that
   value comes from.
3. In `ValidateTemperatureBoost`, whether the ceiling is checked against the
   **increment** or against the **resulting total**. Quote the comparison.
4. `StartSessionAsync`'s write-then-refresh shape, and whether `WriteAsync`
   verifies by read-back for a path not in `PathsWithoutReadBackVerification`.

Then stop.

---

## Stage 1 — implement, nothing transmitted

**Deliverable:** both boost methods writing the cumulative total.

Shape:

1. `EnsureAllowed` with the existing validator.
2. **Read `/p/app/tmpo`** to get the boost already applied.
3. Add the requested increment.
4. Validate the **total** against `MaximumBoostTemperatureCelsius`.
5. Write the total as f32 LE **via `WriteAndVerifyAsync`, not `WriteAsync`**.
6. Refresh on success.

**Use `WriteAndVerifyAsync`.** The prompt previously said to follow
`StartSessionAsync`; that was wrong. `StartSessionAsync` calls `WriteAsync`,
which transmits, requires an empty response, and never reads the path back —
acceptable for `ModeCommand`, which is exempt from verification anyway. `tmpo`
and `timo` answer reads cleanly, so a boost write can be confirmed byte-for-byte
and should be.

**Two defects to fix, and the first is the reason this stage exists.**

`boostsAlreadyApplied` is the literal `0` at both call sites, so the policy's
four-boost cap has never counted anything. `SessionController` does maintain a
real counter, but it increments only after a successful client call and cannot
see a boost applied from the device's own button — which is exactly how the
2026-09-11 capture was produced. With `tmpo` readable, the true count is
`round(tmpo / increment)` and is authoritative for both paths. Supply it.

**The boost ceiling is checked against the increment, not the total.** Gate 0
confirmed the comparison is `temperatureCelsius > limits.MaximumBoostTemperatureCelsius`
where `temperatureCelsius` is the increment, so every individual 2.778 C boost
passes an 8.333 C limit indefinitely.

Be precise about how bad this is, because an earlier draft of this prompt
overstated it. The *absolute* target check that follows —
`snapshot.TargetTemperatureCelsius + temperatureCelsius <= maximumTemperature` —
reads `TargetTemperatureCelsius` from `tcmd`, which already carries accumulated
boost. That check is therefore effectively cumulative and does bound the total
at the verified maximum. The device also enforces its own ceiling; hardware
stopped at three boosts unaided.

So this is not unbounded heating. It is the policy permitting substantially more
boost than the device does, while claiming to enforce a boost limit it does not.
Fix it because a safety gate that does not gate is worse than an absent one —
it invites reliance.

Clamp at the ceiling or refuse — either is defensible. State which and why in a
comment.

Unit tests: cumulative accumulation, the count derived from `tmpo`, refusal at
the ceiling, refusal outside `Active`, and the f32 encoding. No hardware.

**Gate 1:** paste the diff, the test output, and the full build and format
results.

---

## Stage 2 — inspect the frames, transmit nothing

**Andrew runs this.** `--trace-writes` constructs the frame, logs it, and
discards it before the radio.

Prepare the exact command and say what a correct frame looks like — opcode,
path bytes, offset, and the four payload bytes for the first boost from a base
of zero. `2.778` as f32 LE is a specific byte sequence; state it so a wrong
frame is obvious rather than plausible.

**Gate 2:** the command, the expected bytes, and what would indicate a fault.

Andrew returns the log. Confirm the frames match before Stage 3.

---

## Stage 3 — the first real write

**Andrew runs this, after Gate 2 passes.**

One boost, on a live session, watched on the device rather than the screen.
Expected: `tmpo` becomes the increment, `tcmd` rises by the same amount, the
device shows it.

The abort path must be stated before this runs: how to stop the session from the
device if anything is wrong.

**Gate 3:** the log, the device's behaviour, and `tmpo` / `tcmd` read back.

---

## Stage 4 — the rest, then document

Three boosts to the ceiling, a refused fourth, and a session where boost is
attempted outside `Active` and correctly denied.

Then update:

- `CompatibilityCatalog.cs:41`. `MaximumBoostTemperatureCelsius` and
  `MaximumBoostDuration` are described as owner-stated from the phone app. They
  are now **observed on hardware** — 8.333 C and 30 s, reached and held. Say so.
  Do not change the values.
- The README's account of what works.

**Gate 4:** the diff and the final test run.

## Out of scope

Do not write `/p/app/htr/tcmd`. The device derives it, and writing a target
directly bypasses the boost ceiling entirely.

Do not touch the firmware allowlist or the limits values. Provenance improves;
numbers do not move without their own safety argument.
