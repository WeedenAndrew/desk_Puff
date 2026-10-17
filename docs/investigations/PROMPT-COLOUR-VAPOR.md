# Colorway and vapor — finding the session-scoped paths

Two gated prompts, in sequence. Paste each below its rule, into Codex, in the
`desk_Puff` root. Do not start the second until the first has found something.

**Why this order.** No session-scoped path is known for colorway or vapor, and
that is the only thing still blocking a saved profile from running in full.
A path must be found before a write to it can be designed, and **existence is
establishable by reading alone.** Prompt 1 needs no C# and cannot alter the
device. Prompt 2 writes, and only to a path Prompt 1 confirmed.

**Why colour and not vapor.** Colour is cosmetic — a wrong value is a wrong
colour and nothing else. Vapor affects the heating profile, and its value space
is unknown: we do not know which of `Standard` / `High` / `Max` / `XL` maps to
which byte, nor whether a wrong value makes the cycle hotter or more aggressive.
**There is no identified safe direction for vapor.** It stays read-only until the
reads in Prompt 1 let you correlate each slot's stored value with what the phone
app displays for that profile. That correlation is itself read-only, and it is
the prerequisite to a vapor write prompt that does not yet exist.

## The naming evidence both prompts rest on

Three patterns are visible in the paths already known:

1. **The `thc` subtree mirrors a slot.** `/p/app/thc/name`, `/temp`, `/time` are
   the live heat cycle's copies of `/u/app/hc/{i}/name`, `/temp`, `/time`. A
   colour or vapor member there would follow the slot's field name.
2. **Overrides are `<field>o`.** `tmpo` = `tmp` + `o`, `timo` = `tim` + `o`.
   That rule predicts `vapo` and `colo`.
3. **Field names are short lowercase abbreviations** — `mdcd`, `chmt`, `stlm`,
   `btmp`, `btim`, `colr`, `tcmd`, `elap`, `tott`.

---

# Prompt 1 — read-only path discovery

Extend `tools/capture/Capture-DeviceNoise.ps1`. **No C# changes, no allowlist
changes, no writes.** The script never constructs the write opcode, so it cannot
alter or heat the device — that property is the reason this is the first step and
it must survive the change.

## Stage 0 — read only, report, stop

1. Quote `Read-Path` and `Invoke-Lorax`'s status handling. What does the script
   record when a path does not exist, and does a non-zero status abort the run or
   continue to the next path? Continuing is required — a failure is data.
2. Quote `Read-RawPath` and explain its offset parameter.
   `/u/app/hc/0/colr` is read at offset 375 with size 125; state what that
   implies about the colour payload's total size.
3. List every path the script currently reads, grouped as it groups them.
Then stop.

## The status vocabulary — established, do not re-derive

From `survey-20260911-052416.log`, which already ran the script's own
`/p/zzprobe/...` path-length probes against hardware:

| status | meaning | evidence in that log |
|---|---|---|
| `0x00` | success | 52 paths |
| **`0x01`** | **path exists but is not readable** | `/p/app/mc`, `/p/app/ltrn/cmd` — both write-only command paths |
| `0x02` | **path does not exist** | 4× `/p/app/hc/{i}/colr`, 2× `/p/zzprobe/...` |
| `0x7A` | limit exceeded | `/u/app/hc/0/colr` requested at 126 bytes |

**`0x01` is a hit, not a miss.** A write-only command path answers `0x01`, and a
session-scoped *control* is precisely the kind of thing that would be write-only.
Treating `0x01` as failure would discard the most likely form of the answer.
Only `0x02` means the path is not there.

Unknown reads are also now **empirically** harmless rather than harmless by
inference: two nonexistent paths were read, both answered `0x02` with an empty
payload, and the survey continued unaffected.

`/p/app/hc/{0..3}/colr` all returned `0x02`, so the `/p/` prefix does not mirror
`/u/app/hc/` and those four are already ruled out. Do not re-add them.

## Stage 1 — the candidate list

Add a new survey section. Read-only, **continuing past failures**, recording
path, status, payload length and payload hex for every candidate whether it
succeeds or not.

**Group A — `thc` subtree, by slot-field analogy**

```
/p/app/thc/colr    /p/app/thc/vapr    /p/app/thc/btmp
/p/app/thc/vpor    /p/app/thc/vap     /p/app/thc/btim
/p/app/thc/lvl     /p/app/thc/dose    /p/app/thc/idx
```

**Group B — the `<field>o` override pattern**

```
/p/app/vapo    /p/app/colo    /p/app/lvlo
```

**Group C — slot 0 beyond the six known fields**

```
/u/app/hc/0/vapr    /u/app/hc/0/vap     /u/app/hc/0/lvl
/u/app/hc/0/vpor    /u/app/hc/0/dose    /u/app/hc/0/pres
```

**Group D — lantern subtree**

```
/p/app/ltrn/colr    /p/app/ltrn/rgb    /p/app/ltrn/lvl
```

Read each at **4 bytes first.** If a path returns success, re-read it at 32 and
at 125 to find its real length — the slot colour is plainly larger than four
bytes.

**Also read vapor's stored value on all four slots** if Group C finds a vapor
field. Four values, correlated against what the phone app shows for each profile,
is what makes a vapor write designable later. Without it there is nothing to
design against.

## Classify every result against the table above

The script's status summary already groups by status, which is the right output.
In the report, state for each candidate whether it is:

- **`0x00`** — exists and readable. Record the payload and re-read at larger
  sizes to find its length.
- **`0x01`** — **exists, write-only.** This is a find. Name it prominently; it is
  the shape a session-scoped control is most likely to take.
- **`0x02`** — absent. Rule it out and say so, so nobody probes it again.
- anything else — quote it verbatim. A status outside this table is itself a
  finding, and the table is four observations wide, not exhaustive.

## Run it twice — and the second run needs a different order

`Invoke-CandidateSurvey` runs before the mode branch, and under `-WatchSession`
the cycle does not begin until after `WATCH BEGIN`. So both invocations would
probe an **idle** device unless the order changes.

| run | how |
|---|---|
| idle | launch the script with the device idle, as normal |
| mid-cycle | **start the cycle on the device first**, then launch the script |

The script is read-only and connecting mid-cycle is already established practice —
the boost runs did exactly that. No code change is needed for this; only the
order.

This matters because a session-scoped value may be absent or meaningless while
idle, and the `thc` subtree is precisely where that would show. Two identical
idle surveys would read as "these paths do not exist" when the truth would be
"they were never read while one existed."

**Gate 1:** the diff, and the survey output from both runs with the status for
every candidate.

---

# Prompt 2 — colour write attempt

**Only after Prompt 1 confirms a readable colour path.** Do not run this
speculatively, and do not substitute a vapor path into it.

## Stage 0 — read only, report, stop

1. Quote the confirmed colour path, its payload length, and its value as read
   from the device.
2. Quote how `/u/app/hc/{i}/colr` is encoded today — `ProfileLightingCodec` and
   `UpdateProfileAsync` write it, so the format is known. Does the candidate
   path's payload have the same shape? If it does not, say what it looks like
   instead rather than assuming it is the same structure.
3. State whether the candidate is in `ExactWritePaths` or reachable by
   `IsWriteAllowed`. It will not be.
4. Quote `LoraxWriteContext` and state the smallest change that admits one more
   exact path under a **dedicated** context, following the `ThcParameters`
   precedent.

Then stop.

## Stage 1 — the probe

A diagnostic mode, same ladder as the THC probes. Admitted under a **new context
value** — not added to `ExactWritePaths`, and **not folded into
`ThcParameters`**, because a colour path is a different permission from the
heater's setpoint base and collapsing them would widen what that context means.

Device **idle** throughout, rechecked before every write. Never construct `mc`.

| step | action |
|---|---|
| 1 | read and log the candidate path, `hcs`, and `/u/app/hc/{hcs}/colr` |
| 2 | write the candidate **the value just read** — identity. Log the status |
| 3 | write a **visibly different** colour, then read it back **and** read the slot's colour |
| 4 | reload the slot with an identity `hcs` write, then re-read the candidate |

Step 3's second read is the finding. If the slot's colour moved, the candidate is
an alias for the slot and is not session-scoped — which is the outcome most worth
catching, and the one an identity-only write would hide. Step 4 tests whether
`hcs` clears it, as it does for `thc/temp`.

Halt on any non-zero status and log it verbatim. Every write at-most-once, never
retried.

## Reporting

**Writable**, **not-a-slot-alias**, and **cleared-by-`hcs`** as three separate
findings. None may be inferred from the others, and any of them may be
inconclusive without affecting the rest.

Do not claim the colour was *applied to a session* — that needs a cycle, and it
is a later question.

**Gate 1:** the diff, the test output, the exact command line, and full
`build` / `test` / `format --verify-no-changes`. No commit.

---

## What makes this run cheap to read

**The device will tell you.** Pick a colour nothing else on the device uses, and
step 3 is visible without opening a log. That is the same trick as the 467 °F
temperature in the phone-capture card, and the same reason the 450 °F drive run
was legible at a glance.

## What stays unanswered either way

Even a clean Prompt 2 leaves vapor untouched and leaves colour unproven *during*
a session. A saved profile still cannot run in full until both are settled, and a
profile differing in vapor must keep being refused until then — quietly
substituting Standard would be worse than refusing.
