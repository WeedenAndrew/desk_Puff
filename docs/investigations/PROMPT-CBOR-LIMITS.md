# CBOR decoder, GetLimits, and why boost does nothing

Single-run prompt. Paste everything below the line into Codex, in the `desk_Puff`
repository root.

Five stages, five gates. **Stop at each gate and paste the output.**

---

## Standing constraints

- **No git commits, no pushes.** Andrew commits.
- **This work issues no writes to the device.** `GetLimits` (0x02) and path
  reads (0x10) are read-only opcodes. Nothing here constructs opcode 0x11. If a
  stage appears to need a write, stop and say so — `docs/SAFETY.md` has an
  eight-stage ladder and skipping stages on this project has already cost a
  session.
- Never claim something works without running it. Paste real output.
- No attribution trailers anywhere.
- `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes` must
  stay green. `Directory.Build.props` sets `TreatWarningsAsErrors`, so analyzer
  output is load-bearing rather than advisory.

Prefer the workspace SDK if present:

```powershell
$dotnet = if (Test-Path .\.tools\dotnet\dotnet.exe) {
    (Resolve-Path .\.tools\dotnet\dotnet.exe).Path
} else { (Get-Command dotnet -ErrorAction Stop).Source }
```

---

## Stage 0 — read-only, and I already know the answers

Write nothing. Report:

1. Where `GetLimits` is defined, and **every place it is issued**. Quote
   `file:line`.
2. In `DeviceSafetyPolicy.ValidateBoost`, the exact expression that decides
   whether a boost is permitted, and what makes it deny.
3. What `LoraxValueCodec` actually decodes — the encoding, not the name.
4. Which file in `Protocol/` contains real CBOR major-type handling, and which
   major types it implements.

Then stop. A wrong answer means you are summarising rather than reading.

---

## Stage 1 — promote the CBOR reader that already exists

Your Stage 0 answer changes this stage. **Do not write a new decoder.** The
nested `CborReader` in `ProfileLightingCodec` already covers major types 0–7
and the 24/25/26/27 argument forms. It is private, and it discards two things
that matter here.

**Deliverable:** that reader lifted into its own type, with the losses fixed and
a diagnostic dump added.

Three changes, each for a stated reason:

**Materialise floats.** Type 7 with additional info 25, 26 or 27 currently
returns the raw bit pattern, because the lighting decoder never needed the
value. A limit expressed as a float would come back as an enormous integer that
looks like a plausible wrong answer rather than an obvious one. Decode half,
single and double properly.

**Keep the tag.** Type 6 currently returns the tagged value and throws the tag
away. A tag is the source telling you how to read what follows, which is
precisely the information missing about these three numbers.

**Add a dump.** Offset, raw bytes, major type, additional info, and decoded
value, one line per item. A wrong decode has to be visible *as* a wrong decode
rather than as a number.

**Beware `0x7A`.** It is a text-string header with a four-byte length in CBOR,
and it is also a plausible status byte. This project has already misread one as
the other. The dump must show both readings where a byte is ambiguous at the
start of a frame rather than silently choosing.

Keep `ProfileLightingCodec` working — it has tests, and they must stay green.

Unit tests against RFC 8949 Appendix A vectors, including one of each
additional-info form, plus the float and tag cases the old reader dropped.

**Gate 1:** paste the test output, a dump of the RFC vectors, and confirmation
that the existing lighting tests still pass.

---

## Stage 2 — decode what the device actually returned

**Deliverable:** the 125/40/8 response run through the new decoder.

`CompatibilityCatalog.cs:41` records that `GetLimits` returned 125, 40 and 8 as
u16 values and that they "do not describe these thermal limits." That conclusion
was reached by reading them as three u16s. **The decoder's job is to test that
reading**, not inherit it.

**The raw bytes already exist.** Found in `survey-20260828-023254.log`, which
Andrew captured on 2026-08-28:

```
-- opcode 0x02 probes (raw, no interpretation) --
  empty body        <empty body>      STATUS=0x00 SUCCESS  6B  7D 00 28 00 08 00
  read-shaped body  /p/sys/fw/ver     STATUS=0x00 SUCCESS  6B  7D 00 28 00 08 00
```

Three facts fall straight out, and your job is to confirm or break them:

**Both probes return byte-identical responses.** The reply does not depend on
the request body, which rules out the path-echo theory.

**It is not CBOR.** `0x7D` is major type 3 with additional information 29, and
28–30 are reserved in RFC 8949. Your new reader should reject this frame, and
that rejection is the evidence. Six bytes as three little-endian `u16` give
exactly 125, 40 and 8.

**125 is already in the codebase.** `LoraxDeviceClient.cs:596` reads
`const int chunkSize = 125;` with no comment and no provenance — an undocumented
magic number in the read path that the device reports as its first limit.

So the hypothesis to test is that **`GetLimits` returns protocol limits, not
device limits** — which is why they never described thermal ones. Proposed:
125 = maximum read payload per chunk (high confidence, exact match); 40 =
maximum path length, against a longest path in use of 16 characters (moderate);
8 = unknown (low).

Do not accept that because it is written here. Break it if it is wrong.

Questions the dump should answer:

- Is the response CBOR at all, or raw little-endian?
- If CBOR: an array of three, a map with keys, or something else? A map would
  name the fields and the whole question dissolves.
- Are they u16, or a different width read wrongly?
- Is there a header, status byte or length prefix being counted as data?
- Does the read-shaped probe echo the requested path, which would make one of
  the "values" a string length rather than a limit?

If the raw bytes are not recorded anywhere, say so and stop — Stage 4 captures
them on hardware. **Do not reconstruct bytes from the decoded numbers.**

**Gate 2:** the dump, and a plain statement of which readings are consistent
with the bytes and which are ruled out.

---

## Stage 3 — why boost does nothing

**Deliverable:** the chain traced end to end, no device required.

`ValidateBoost` computes `Math.Min(snapshot.Capabilities!.MaximumConsecutiveBoosts, 4)`
and denies when that is zero. Trace it:

1. Where does `snapshot.Capabilities` come from on the **Bluetooth** path —
   `LoraxDeviceClient`, not the demo client?
2. Is `MaximumConsecutiveBoosts` read from the device, defaulted, or left zero?
3. If defaulted: what to, where, and is that default reached on a real connect?
4. `ValidateBoost` also calls `Evaluate(action, snapshot)` first. Under what
   snapshot states does *that* deny, and is an idle device one of them? Boost is
   only meaningful mid-session, so a state gate would be correct behaviour
   rather than a bug.

**Both can be true.** A zero capability and a correct state gate would each
produce a dead button, and fixing one would leave it dead. Report which
condition each is, and whether they are simultaneously in play.

Add a unit test that fails on the current behaviour and names it — a boost
request against a snapshot that *should* allow one, showing the denial reason.

**Gate 3:** the trace with `file:line` at each step, the failing test, and its
output.

---

## Stage 4 — the hardware capture

**Andrew runs this. You prepare it.**

**Deliverable:** an addition to `tools/capture/Capture-DeviceNoise.ps1`, or a
sibling script, that issues `GetLimits` and records the **raw bytes** alongside
the decode.

It must record the complete frame — sequence, status and payload — not just the
payload, since Stage 2 may show the framing is where the misreading happened.

**The `0x02` capture is already done** — `survey-20260828-023254.log` has both
probes and they are byte-identical. Stage 4 is no longer about collecting the
response. It is about testing what the three numbers *mean*, because Gate 2
established that matching a constant does not establish a wire definition.

Two experiments, both read-only, each of which can kill its hypothesis.

**Is 125 the maximum read payload?** Request a single read of **126** bytes from
a path known to hold at least that much, then 125, then 124. If the device caps
its reply at 125 bytes, that is the limit, directly measured. If it returns 126,
then 125 is not the payload ceiling and `chunkSize` at
`LoraxDeviceClient.cs:596` is a coincidence or a copied number.

Note this cannot be run through `ReadAllAsync`, which already chunks at 125 and
would hide the answer. It needs a single raw read at a chosen length.

**Is 40 a path-length limit?** `LoraxProtocol.cs:119` permits 63 characters, so
if the device's limit is 40 the app already accepts paths it cannot send — which
is a bug nobody has hit because the longest path in use is 16.

Test differentially, since a nonexistent path and an over-long path might both
simply fail. Read a **nonexistent path of ~20 characters**, then a **nonexistent
path of ~50 characters**. Identical failures mean 40 is not a path limit.
Different status bytes mean it probably is, and the difference names it.

Record the status byte and full frame for every case, including the failures.
**A failure here is the measurement, not an error to work around.**

Read-only. The script must not construct opcode 0x11. State that in a comment
and make it true.

Also capture whatever populates `Capabilities`, so Stage 3's question is
answered from the device rather than from the code.

**Gate 4:** paste the script and the exact command Andrew runs. Do not run it;
there is no device attached to your environment.

---

## Stage 5 — interpretation, and the boost path

After Andrew returns the captures.

**The limits.** Decode them, state what 125/40/8 are, and say plainly if the
bytes do not settle it. **A wrong answer here is worse than no answer** — these
numbers may end up gating a heater, and the limits table is owner-stated
precisely because nobody could read the device's own.

**The boost semantics.** Gate 3 established that `BoostTemperatureAsync` and
`BoostTimeAsync` validate and then throw; nothing is ever written. The paths
exist and are already on the write allowlist:

```
LoraxPaths.cs:22   TemperatureOverride = "/p/app/tmpo"
LoraxPaths.cs:23   TimeOverride        = "/p/app/timo"
```

Both appear in `ExactWritePaths` and are referenced nowhere else. That gap is
the whole of the dead button.

Before anything is written, one question has to be answered, and it can be
answered by **reading**:

> Does `/p/app/tmpo` hold an **absolute** target, or a **delta**?

"Override" suggests absolute. But the per-profile boost amounts live separately
at `btmp` and `btim`, which suggests `tmpo`/`timo` are the session's *effective*
values rather than the increment. A boost would then be read-add-write, not a
write of `+15`.

Getting this backwards writes 15°F as an absolute target to a device that heats.

Add both paths to the read set in `Capture-DeviceNoise.ps1`, to be captured
**during an active session** — idle will not distinguish the two readings if
both are zero. Compare against the active profile's target read at the same
moment. If `tmpo` equals the profile target, it is absolute. If it is zero or
small while a session runs, it is a delta.

**This is a read. Do not write either path in this stage**, and do not
implement boost. `docs/SAFETY.md` has an eight-stage ladder and this is the
stage that establishes what the value means.

**Gate 5:** the limits decode, the `tmpo`/`timo` readings against the
simultaneous profile target, a plain statement of absolute versus delta, and an
explicit list of what remains unknown.

---

## Stage 6 — watch a whole session instead of sampling one moment

Gate 5's `tmpo` reading was inconclusive and the reason is timing. The capture
takes **3.7 seconds** of reads after an 8-second scan; a session runs **90
seconds**. One sample lands wherever it lands, and `tmpo = 0` is what both
hypotheses predict outside an applied boost.

Extending the *scan* does not help — that is discovery. What is missing is a
read phase that outlasts the session.

**Deliverable:** a polling mode in `Capture-DeviceNoise.ps1` that samples the
session paths repeatedly for longer than a session, then stops.

- Poll every **1.5 s** for **120 s** — a 90 s session plus margin at both ends.
  One pass over six paths measured ~300 ms, so the interval is comfortable.
- Sample, every pass: `/p/app/tmpo`, `/p/app/timo`, `/p/app/htr/tcmd`,
  `/p/app/htr/temp`, `/p/app/stat/elap`, `/p/app/stat/id`.
- One line per pass, timestamped, values on one row. A boost applied mid-session
  must be visible as a step change in a column — that is the entire point, so
  format for scanning down a column rather than reading paragraphs.
- Put it behind a switch, `-WatchSession`, so the existing one-shot survey is
  unchanged and still the default.

Still read-only. No opcode 0x11.

**Two things that may bite, and should be handled rather than discovered:**

The connection has to survive 120 s. Nothing has held this link that long
before — the longest run so far is under 12 seconds. If it drops, record the
timestamp and the last successful read rather than failing the whole capture.

A session ending may change device state or drop the link. Keep polling and
record what happens; the transition out of a session is itself data, and
`stat/id` moving from 7 to something else would name the state machine.

**Gate 6:** paste the script diff and the exact command. Do not run it.

### What Andrew does with it

1. Start the capture with `-WatchSession`.
2. Once polling begins, start a heat cycle on the device.
3. **Apply a boost from the device itself** partway through.
4. Let the session end while polling continues.

Then `tmpo` is decided by inspection: a step to **2.778** is a delta, a step to
**251.667** is absolute, and no step at all means the physical button does not
drive this path.

## Out of scope

Do not change `CompatibilityCatalog`'s limits table. It is owner-stated,
hardware-verified, and gates live writes. If the device turns out to report its
own limits, that is a finding to report — replacing the table is a separate
decision with its own safety argument.
