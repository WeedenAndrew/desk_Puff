# Four-mode vapor mapping and attempted idle handoff: 2026-10-17

## Persistent slot mapping

After the [first two-mode capture](VAPOR-CAPTURE-2026-10-17.md), the user reported
saving profile P (wire name `p`, slot 3) as Smooth, then Intense, then restoring
Extreme, with pauses and no heating. The chronological captured writes and
immediate successful reads establish the additional values below; they are
not inferred from enum order or label order alone.

| Official-app label | float32 LE | Four payload bytes | Evidence |
| --- | ---: | --- | --- |
| Smooth | 0.0 | `00 00 00 00` | New write/readback |
| Bold | 0.5 | `00 00 00 3F` | Earlier opposite-change capture |
| Intense | 1.0 | `00 00 80 3F` | New write/readback |
| Extreme | 1.5 | `00 00 C0 3F` | Earlier capture and new restoration |

All three new writes used `/u/app/hc/3/intn`, Lorax opcode `0x11`, offset `0`,
flags `0x04`, and four-byte little-endian IEEE 754 binary32 values. Each received
status `0`, empty reply payload, and an immediate opcode `0x10` read at offset
`0` requesting up to 125 bytes returned the same four bytes with status `0`.

Indices below are zero-based HCI record indices in this capture's retained
rotated Bluetooth trace; sequence numbers are Lorax uint16 values.

| User-reported action | Write sequence; request/reply records | Read sequence; request/reply records |
| --- | --- | --- |
| Save Smooth, 0.0 | 19707; 58280 / 58286 | 19709; 58287 / 58294 |
| Save Intense, 1.0 | 20028; 59693 / 59699 | 20031; 59702 / 59709 |
| Restore Extreme, 1.5 | 20361; 61178 / 61185 | 20363; 61186 / 61192 |

The writes are separated by 14.807087 and 15.551650 seconds. Exactly three
`/intn` writes were observed, all to this persistent slot. The app also rewrote
slot name, temperature, and duration: each retained identical bytes across
all three writes and their readbacks. Color and boost equality were not
established by this analysis. The user reported remaining idle throughout.

## Idle handoff remains inconclusive

The user reported selecting Rosin, waiting, and selecting P without heating.
However, all 13,990 target command values, including unmatched requests,
contain no literal `/p/app/hcs` request and no `/p/app/thc/intn` request or
write. Continued `/p/app/led/pclr` traffic after the final restoration shows
that the trace retained subsequent app/device activity, but does not prove an
active-profile handoff or an intensity reload. This is not evidence that
selection failed, nor evidence that firmware reloaded vapor.

The earlier captures read active slot 3 and runtime intensity 1.5. Across all
retained target streams no runtime intensity write has been observed. These
captures therefore establish **persistent profile values only**, not temporary
JSON session vapor, runtime write permission, restoration semantics, or
physical heating behavior. Existing production guards remain unchanged.

## Identity sources and current client boundary

Earlier baseline packet reads established model 13, API 39, and chamber 2.
The user now reports official app version **3.13.31** and Puffco firmware
**AW-1d90ae1** from the official app's information screen. Those two version
strings are user-reported, not independently captured firmware responses.
No `/p/sys/fw/ver` request was observed in these retained target streams;
API 39 must not be substituted for that firmware value.

The current client reads `/p/sys/fw/ver` with size 12. Empty bytes become
`Unknown`; otherwise it converts only the first byte through
`RevisionNumberToString`. It does not fall back to the API field. The current
catalog admits only Peak Pro model 13 / firmware `AN` and supplies verified
limits only for that tuple. Its safety policy denies non-read actions when
firmware is unverified. The user-reported AW version does not establish which
byte the client would receive and does not admit a new tuple. Read-only
identity confirmation is a separate prerequisite before any reviewed probe;
this document changes no catalog, limits, write allowlist, or capability flag.

Source locations at this investigation: `LoraxDeviceClient.cs` initial identity
reader, `Compatibility/CompatibilityCatalog.cs`,
`Protocol/LoraxValueCodec.cs`, and `DeviceSafetyPolicy.Evaluate`.

## Capture integrity and private retention

Only the two Bluetooth log entries were extracted from the Android export.
Unrelated diagnostic contents were not inspected. Raw files remain outside
Git; no device addresses, credentials, or unrelated history are reproduced.

| Retained relative file | Bytes | SHA-256 |
| --- | ---: | --- |
| `bluetooth-previous/bluetooth-trace.bin` | 5636577 | `b33b9f2351136118357e0732ac0342cb2016c8a5b2854a6b418da089ef896524` |
| `bluetooth-current/bluetooth-trace.bin` | 465523 | `7b22c81bd2c049da2d32bab804e2d361abff30a6039ee222d2410b5e8e9c3d3a` |

The earlier comparison current file is an exact byte prefix of this rotated
file, establishing capture continuity. Overlapping exports must not be added
as unique traffic. This export contains 70,610 records, 51,215 complete ACL
records, and 3,766,858 ACL payload bytes. No reported drops, shortened or
truncated records, ACL length mismatches, or incomplete L2CAP messages were
found. The target's 41,677 ATT packets are all fixed CID 4; dynamic traffic
belongs to other connections. Literal Lorax paths and connection continuity
identify the target; cached GATT UUID discovery was not independently observed.

Encoded timestamps still appear six hours behind the independent phone/host
clock despite the correct BTSnoop epoch. No absolute correction is asserted.
Order and elapsed intervals are used; experiment receipt markers are not exact
action timestamps.

## Scope

The four persistent-slot mappings are observed on this device. The attempted
idle selection remains inconclusive. Later direct runtime-session evidence is
recorded [separately](VAPOR-APP-ONLY-SESSION-2026-10-17.md); capture findings do
not by themselves broaden production admission.
