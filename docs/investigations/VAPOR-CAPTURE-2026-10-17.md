# Vapor capture: 2026-10-17

## Finding and scope

A passive Android Bluetooth HCI capture recorded two opposite vapor-only edits
made by the user in the official app: profile **P**, Extreme → Bold → Extreme.
The profile's actual name read from slot 3 was lowercase `p`. The user separately
reported Rosin as Bold; its slot 1 name read `ROSIN`.

The observed **persistent profile slot** contract was:

| Official-app label | Path | Payload type | Payload bytes | Decoded value |
| --- | --- | --- | --- | --- |
| Bold | `/u/app/hc/3/intn` | little-endian IEEE 754 binary32 | `00 00 00 3F` | 0.5 |
| Extreme | `/u/app/hc/3/intn` | little-endian IEEE 754 binary32 | `00 00 C0 3F` | 1.5 |

Each change used Lorax opcode `0x11`, offset `0`, flags `0x04`, and received
reply status `0` with an empty reply payload. An immediate opcode `0x10` read
at offset `0`, requesting up to 125 bytes, returned the same four payload bytes
with status `0`. The two writes were 46.698179 seconds apart.

This establishes only the two observed labels and the persistent slot-3 edit
on this captured device. Other modes, other device/firmware combinations, power
cycle persistence, and physical heating behavior were not tested. The user
reported the device idle throughout; no heating or desk_Puff device commands
were performed for this investigation.

## Identity and baseline observations

Successful opcode `0x10` reads returned:

| Path | Observed representation/value | Baseline rotated request record |
| --- | --- | --- |
| `/p/sys/hw/mdcd` | uint32 LE, 13 | 57162 |
| `/p/sys/fw/api` | uint32 LE, 39 | 57151 |
| `/p/htr/chmt` | one byte, 2 | 57327 |
| `/p/app/stat/id` | one byte, 5 | 57338 and 57365 |

The repository catalog calls model 13 Onyx Peak Pro and maps chamber value 2
to 3DXL. Those names are existing repository interpretations. The actual
firmware version was not established: `/p/sys/fw/ver` was not observed. API 39
must not be relabeled as firmware AN or used to admit a firmware tuple.
Initial state value 5 maps to Idle in the repository; those early reads do not
establish the latest state at either edit. Idle during the experiment is the
user's report.

Baseline profile reads associated slots 0–3 with `DIAMONDS`, `ROSIN`, `RESIN`,
and `p` respectively. Their `/intn` responses were 1.5, 0.5, 1.5, and 1.5,
all four-byte float32 LE with status 0. These reads alone do not map every
profile to a vapor label.

`/p/app/thc/intn` was read successfully as four-byte float32 LE 1.5. **No write
to that runtime path was observed.** A runtime read does not establish its
write permissions or temporary-session semantics. This finding does not unlock
saved JSON session vapor, remove the safety gate, coerce a requested mode, or
authorize a persistent slot mutation as a session workaround.

## Causal packet evidence

Indices below are zero-based HCI record indices in the retained comparison
**rotated** Bluetooth trace, not sequence numbers or wall-clock markers.

| Action | Write request / successful reply | Read request / successful reply | Value |
| --- | --- | --- | --- |
| User P Extreme → Bold | 56364 / 56369 | 56371 / 56377 | 0.5 |
| User P Bold → Extreme | 61387 / 61394 | 61396 / 61402 | 1.5 |

Exactly two `/intn` writes were found in the identified comparison target
command stream, both to `/u/app/hc/3/intn`. The official app also rewrote the
slot's name, temperature, and duration; each retained identical bytes across
its baseline, writes, and successful readbacks. Color and boost equality were
not established by this analysis. User instruction/receipt markers are not
exact action timestamps; causal order uses the recorded commands and replies.

## Capture integrity and retained evidence

Only the Bluetooth trace entries `FS/data/log/bt/btsnoop_hci.log` and
`FS/data/log/bt/btsnoop_hci.log.last` were extracted from each Android export.
Unrelated diagnostic contents were not inspected. Raw traces remain private,
outside the repository; the filenames and hashes below identify the retained
files without publishing their contents.

| Capture / retained relative file | Bytes | SHA-256 |
| --- | ---: | --- |
| Baseline `bluetooth-previous/bluetooth-trace.bin` | 8874010 | `db66956ea9b41127c6d2962d1a16b644c0ae9629cfb7dbe949e3a8c324306ec7` |
| Baseline `bluetooth-current/bluetooth-trace.bin` | 1309067 | `84e72d1bf4e08e25ff4ef1c4b5bddb7d43716b41e94091c9b6add4c028fed0ef` |
| Comparison `bluetooth-previous/bluetooth-trace.bin` | 5314158 | `de7761880c41905eb2d9bd6cf97a50daccec4c7c2c2bc81fc570f5f3051a41b8` |
| Comparison `bluetooth-current/bluetooth-trace.bin` | 216788 | `51985fd3593e100c6c7c9b28a8ea651548c70703257ed9aafa7bec58704e369f` |

The baseline current trace is an exact byte prefix of the comparison rotated
trace. The baseline contains 82,924 records and 37,846 complete ACL records;
the comparison contains 67,954 records and 45,811 complete ACL records. These
are overlapping captures and must not be summed as unique traffic.
No reported drops, shortened capture records, truncated records, ACL length
mismatches, or incomplete L2CAP messages were found in these retained traces.

The identified comparison target stream contains 26,126 ATT packets, all on
fixed L2CAP CID 4. Dynamic-channel traffic belongs to other connections, so
unparsed EATT does not obscure this target's writes. Literal Lorax paths and
connection continuity identify the target; cached GATT discovery means its
service/characteristic UUIDs were not independently observed in this capture.

The correct BTSnoop epoch `0x00DCDDB30F2F8000` was used. Encoded times appear
six hours behind the independent phone/host wall clock; no absolute correction
is asserted. Record ordering and elapsed intervals support the comparison.
## Scope

This record establishes persistent-slot behavior only. Later runtime-session
evidence is recorded [separately](VAPOR-APP-ONLY-SESSION-2026-10-17.md). Neither
record independently establishes firmware identity or broad device support.
