# Official-app app-only sessions: 2026-10-17

## New evidence and limits

The user ran an official-app profile reported to exist only in the app, named
New Profile, with displayed temperature 460 (unit not explicitly confirmed)
and duration 1:30. They reported starting Extreme, stopping it, then starting
a separate Intense session, applying two temperature boosts, and returning to
idle before reporting. They saw no replacement-slot prompt. This was an
exploratory mixed-action trace, not a controlled one-variable experiment.

**Direct runtime vapor writes are now observed.** Both sessions wrote
`/p/app/thc/temp`, `/p/app/thc/time`, and `/p/app/thc/intn`, with immediate
successful matching reads, followed by runtime color-path operations, a
successful `/p/app/hcs` write/read of byte `FF`, then `/p/app/mc` byte `07`.
The intensity values were Extreme 1.5 and Intense 1.0, consistent with the
[earlier persistent-slot mapping](VAPOR-FOUR-MODE-CAPTURE-2026-10-17.md).
This supersedes earlier absence-of-runtime-write findings for these two
observed values on this captured device. It does not admit firmware, implement
session vapor, establish every runtime mode, or prove restoration behavior.

All 15,420 target command values, including unmatched requests, were checked:
no `/u/app/hc/...` request of any observed opcode occurred, and no relevant
request lacked a paired reply. Thus no persistent-slot copy/write was observed.
Byte `FF` is the actual hcs value in this app-only context; a general sentinel
contract or its lifecycle must not be assumed from its numeric value alone.

## Scalar and command evidence

All listed writes used Lorax opcode `0x11`, offset 0, flags `0x04`, received
status 0 and an empty reply. Listed reads used opcode `0x10`, offset 0,
requested up to 125 bytes, received status 0, and returned exactly the written
bytes. Intensity, time, temperature and boost payloads are little-endian IEEE
754 binary32; hcs/mc payloads are one byte.

Indices are zero-based HCI record indices in this export's **rotated** trace.
Sequences are Lorax uint16 values. Read rows identify immediate readback,
not a later post-completion verification.

| Path, chronological order | Value; payload hex | Write sequence; request / reply | Read sequence; request / reply |
| --- | --- | --- | --- |
| `/p/app/thc/temp` | 237.77777099609375; `1CC76D43` | 31633; 49268 / 49274 | 31635; 49276 / 49287 |
| `/p/app/thc/time` | 90.0; `0000B442` | 31637; 49288 / 49294 | 31639; 49295 / 49301 |
| `/p/app/thc/intn` | 1.5; `0000C03F` | 31641; 49302 / 49308 | 31643; 49310 / 49316 |
| `/p/app/hcs` | FF; `FF` | 31679; 49461 / 49467 | 31681; 49469 / 49476 |
| `/p/app/mc` | 07; `07` | 31683; 49477 / 49482 | Not observed |
| `/p/app/mc` | 08; `08` | 31729; 49719 / 49724 | Not observed |
| `/p/app/mc` | 02; `02` | 31735; 49744 / 49749 | Not observed |
| `/p/app/thc/temp` | 237.77777099609375; `1CC76D43` | 32503; 53418 / 53423 | 32505; 53435 / 53439 |
| `/p/app/thc/time` | 90.0; `0000B442` | 32507; 53441 / 53446 | 32509; 53447 / 53454 |
| `/p/app/thc/intn` | 1.0; `0000803F` | 32511; 53456 / 53461 | 32513; 53462 / 53470 |
| `/p/app/hcs` | FF; `FF` | 32549; 53613 / 53618 | 32551; 53620 / 53627 |
| `/p/app/mc` | 07; `07` | 32553; 53628 / 53634 | Not observed |
| `/p/app/tmpo` | 2.777777671813965; `1CC73140` | 33329; 57392 / 57396 | 33331; 57402 / 57406 |
| `/p/app/tmpo` | 5.55555534362793; `1CC7B140` | 33361; 57552 / 57556 | 33363; 57559 / 57564 |

The initial temperature payload decodes as 237.77777099609375, consistent
with approximately 460°F when interpreted as Celsius by the current client.
That conversion does not independently establish the user's display unit.
Time decodes as 90.0. Two later `tmpo` writes/readbacks decode as cumulative
2.777777671813965 and 5.55555534362793, corresponding to approximately +5°F
and +10°F when interpreted as Celsius deltas. No `timo` writes were observed.

The first `mc=07` was followed 2.783609 seconds later by `mc=08`, then
0.235546 seconds later by `mc=02`. The repository names 07 StartHeatCycle and
08 AbortHeatCycle; the meaning of 02 was not established here and must not be
copied into a new implementation as a guessed reset operation. The second
setup/start followed later and included the two temperature boosts.

Runtime `/p/app/thc/colr` operations with opcodes 0x12, 0x20, and 0x10 also
occurred before each hcs/start. Their complete transfer semantics are outside
this scalar analysis; do not claim they are optional or safely reproducible
from this inventory alone. Private names/colors are retained as lengths/hashes
in the sanitized scalar report rather than republished as raw values.

## Lifetime and identity remain separate questions

There was no later literal hcs/runtime-parameter read or explicit restoration
write, and no second `mc=08`. However, unsolicited composite telemetry does
establish subsequent state and selection changes; literal requests alone do
not describe this lifecycle.

The decoder linked successful opcode `0x20` subscriptions to notification
entries on the same connection. The earlier comparison rotated records 21725
and 21776 register `/p/app/hcs` as subscription 9 and `/p/app/stat/id` as 12.
Observed frames comprise a uint16 event sequence, byte 00, then repeated
uint16 subscription ID / uint8 length / value entries. Across the deduplicated
retained trace union, all 39,723 candidate frames consumed their exact lengths,
with zero candidate framing failures. There were 112,359 entries in this
retained mapping and 1,559 entries outside its retained safe-field mapping,
deliberately not interpreted or published. Those 1,559 entries use already
observed IDs 8/16/17/18 (counts 2/1032/513/12),
log-related fields; they are not missing subscription registrations. Their
payloads were not inspected for this revision. Source lookup retains original
file record indices; unrelated event values are not published.

| Source trace / record | Decoded path/value | Repository interpretation |
| --- | --- | --- |
| Rotated / 49468 | hcs FF | Observed app-only selection context |
| Rotated / 49483 | stat/id 7 | Preheating |
| Rotated / 49725 | stat/id 5 | Idle after first abort |
| Rotated / 53635 | stat/id 7 | Preheating after second start |
| Rotated / 55237 | stat/id 8 | Active |
| Rotated / 63658 | stat/id 5 | Idle |
| Current / 7510, one composite frame | hcs 03 and stat/id 6 | Slot 3 selection; SelectingTemperature |

The second Active-to-Idle interval was 90.000321 seconds, directly confirming
a completed active interval in the stream. A later unsolicited selection/state
transition occurred 90.914637 seconds after that Idle event, only 0.569440
seconds before trace cutoff. Its cause is unknown: there is no literal hcs
restore write to attribute it to, and the event alone does not establish
automatic cleanup or a user/device action. **The last observed state is 6,
not 5**, and the last observed selection is 3 rather than FF. This qualifies
the user's earlier idle report without claiming it was false at its report time.
No post-completion runtime intensity value was observed; hcs returning to 3
does not prove that original runtime temperature/time/intensity bytes returned.

A later reported run cannot be correlated to the captured 03/06 event.
The first capture supplies no later runtime intensity readback, so restoration
of original runtime values remains unproven.

Earlier packet reads established model 13 / API 39 / chamber 2. The user
reports official app 3.13.31 and device firmware AW-1d90ae1. Those version
strings remain user-reported; no independently captured `/p/sys/fw/ver`
response is supplied here. API 39 is not a firmware identity. Firmware
admission remains a separate software boundary.

## Implementation reference

Current transaction, recovery and admission behavior is described in the
[runtime implementation](VAPOR-RUNTIME-IMPLEMENTATION-2026-10-20.md). This capture
establishes wire observations, not a general cleanup contract or broader
firmware admission.

## Integrity and retained private evidence

Only the two recognized Bluetooth log entries were extracted; unrelated
Android diagnostics were not read. No addresses, credentials, or unrelated
history are included. The previous four-mode capture current file is an exact
byte prefix of this rotated file. These exports overlap and must not be summed
as unique traffic.

| Retained relative file | Bytes | SHA-256 |
| --- | ---: | --- |
| `bluetooth-previous/bluetooth-trace.bin` | 5939173 | `b4a2255119c083829964b0fddadb32eeec7f2d4e58106fa6153f3bf1a2642621` |
| `bluetooth-current/bluetooth-trace.bin` | 639584 | `0641fe04eb5bb72614580da70fbe074f9782386755fb06b47cb2b9bcb0c85dd2` |

This export contains 73,102 records, 57,462 complete ACL records, and 4,405,702
ACL payload bytes. No reported drops, shortened/truncated records, ACL length
mismatches, or incomplete L2CAP messages were found. All 46,086 target ATT
packets use fixed CID4; dynamic traffic is on other connections. Literal paths
and connection continuity identify the target; cached UUID discovery was not
independently observed. Encoded clock times remain six hours behind the
independent phone/host clock; the correct BTSnoop epoch is used, without an
asserted absolute correction. Order/intervals support this analysis.

