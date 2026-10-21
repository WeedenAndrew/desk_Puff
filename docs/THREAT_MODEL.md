# Threat model

## Assets and trust boundaries

- The user's physical heater and battery are safety-critical assets.
- Bluetooth pairing state and device identifiers are private local data.
- The Windows host, Puffco firmware, and public protocol research are separate
  trust boundaries. Device firmware remains authoritative for heater control.
- **The `desk-puff-ble` sidecar is a separate process and a trust boundary.**
  It is the only component that opens Bluetooth. It is launched from a fixed
  path beneath the application directory and spoken to over stdin/stdout.

## Addressed risks

| Risk | Mitigation |
| --- | --- |
| Unknown firmware receives a write | Exact verified model/firmware admission, recorded limits and policy tests |
| A UI change bypasses safety | Controls call only typed operations; policy is below the UI |
| Lost BLE reply duplicates a command | State-changing commands are at-most-once and never retried |
| Concurrent reads and writes interleave | One session command gate and one transport command gate |
| Malicious or accidental path reaches firmware/files | Exact write-path allowlist and restricted opcode enum |
| Invalid heat/profile values are encoded | Device-limit sanity checks plus conservative absolute caps |
| Malformed or oversized profile lighting reaches the UI/device | 4096-byte CBOR cap, bounded parser, separate display/write palette limits, exact path allowlist, byte-preserving edits and whole-document read-back |
| Malformed or hostile local profile JSON affects the app/device | Separate 16 KiB-capped documents, schema and value validation, traversal-safe filenames, reparse-point rejection, atomic saves, malformed-file isolation, and a separate safety-gated device-write action |
| Repeated boosts run without bound | Per-type count, per-hit and cumulative temperature/time ceilings |
| User-configured quick hit exceeds safe limits | Save-time validation plus per-command amount, cumulative temperature, and cumulative duration checks |
| Personal device data is collected | No cloud, analytics, account, serial-number read, or address log. Held by absence of a call: reads are not allowlisted, only writes are |
| A substituted helper binary is launched | Fixed path under the application directory; `PATH`, working directory, and environment are not consulted |
| Handoff targets the wrong Bluetooth product | Heating-state gate, Peak-name candidate filter, normal pairing/authentication, and post-connect Peak Pro identity check |
| Handoff interrupts another controller | No forced takeover; Windows Bluetooth exclusivity is respected and the connection fails closed |
| Dependency or CI action drifts | Central versions, lock files, NuGet audit, and SHA-pinned actions |

## Residual risks

The Lorax protocol has no public manufacturer specification, Windows Bluetooth
drivers vary, and physical validation covers only the narrow recorded tuple and
operations. Unknown firmware stays read-only; vapor capture and software tests do not establish broader certification or
complete recovery validation. Future signed releases will
also require a protected code-signing process; current local development builds
are unsigned and Windows may show a reputation warning.

No software can guarantee that third-party hardware will never fail. The goal is
to minimize reachable behavior, fail closed, keep the physical stop control
authoritative, and require evidence before expanding compatibility.
