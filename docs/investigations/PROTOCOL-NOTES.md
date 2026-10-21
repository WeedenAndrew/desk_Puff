# Protocol notes

## Data formats

- Lorax reply status is separate from its payload. Status `0x7A` was observed
  for an oversized read; it is not a CBOR header in that reply.
- GetLimits returned three little-endian uint16 values: 125, 40 and 8. The
  125-byte read payload limit is established; the other fields remain unknown.
- Lighting documents are CBOR. Captured documents ranged from 466 to 799 bytes;
  the client bounds reads at 4,096 bytes and uses 125-byte chunks. Supported
  color edits preserve the original document and verify the full readback.
- Heating scalars and boost offsets are IEEE 754 float32 little-endian.
  `/p/app/tmpo` and `/p/app/timo` are session boost offsets; validated cumulative
  ceilings are recorded in [device evidence](../compatibility/peak-pro-13-AN.md).

## Vapor and temporary sessions

| Official mode | Captured scalar |
|---|---:|
| Smooth | 0.0 |
| Bold | 0.5 |
| Intense | 1.0 |
| Extreme | 1.5 |

All four values have persistent-slot write/readback evidence. Runtime writes
of Extreme and Intense were directly observed at `/p/app/thc/intn`, alongside
`thc/temp` and `thc/time`, with opcode `0x11`, offset 0 and flags `0x04`.
The app-only context used `/p/app/hcs` byte `FF`; it is not a fifth saved slot.
Observed start/abort values were `mc` 07/08. The purpose of `mc` 02 was not
established and is not used by the temporary-session implementation.

The [dated captures](README.md) retain exact bytes, request/reply indices,
readbacks and hashes. They distinguish observed data from restoration and
firmware behavior that remain unverified. Legacy JSON ordinals are not wire
mode mappings; old intent requires an explicit named choice.

## Passive capture requirements

The official app's host must record its own Bluetooth traffic; an unrelated
computer scan does not expose phone writes. Keep desk_Puff disconnected while
collecting official-app traffic. Verify complete ACL/L2CAP/ATT payloads before
comparing actions: event-only or truncated logs cannot establish an encoding.

Record a baseline, one setting change and its reversal while keeping unrelated
settings fixed. Use packet order, successful replies and immediate readbacks;
receipt timestamps alone do not establish causality. Reassemble fragmented
messages and distinguish persistent-slot paths from runtime paths. A capture
without those actions cannot prove their absence from the protocol.

Raw exports can contain addresses, keys and unrelated-device traffic. Keep them
outside Git and retain only sanitized paths, formats, indices and hashes in
evidence. Device actions require separate controlled validation; these notes
do not request a heat cycle. If state is uncertain, hold the physical device
button until it powers off. See [safety](../SAFETY.md).
