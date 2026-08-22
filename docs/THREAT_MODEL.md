# Threat model

## Assets and trust boundaries

- The user's physical heater and battery are safety-critical assets.
- Bluetooth pairing state and device identifiers are private local data.
- The Windows host, Puffco firmware, and public protocol research are separate
  trust boundaries. Device firmware remains authoritative for heater control.

## Addressed risks

| Risk | Mitigation |
| --- | --- |
| Unknown firmware receives a write | Empty verified-firmware allowlist plus policy tests |
| A UI change bypasses safety | Controls call only typed operations; policy is below the UI |
| Lost BLE reply duplicates a command | State-changing commands are at-most-once and never retried |
| Concurrent reads and writes interleave | One session command gate and one transport command gate |
| Malicious or accidental path reaches firmware/files | Exact write-path allowlist and restricted opcode enum |
| Invalid heat/profile values are encoded | Device-limit sanity checks plus conservative absolute caps |
| Malformed or oversized profile lighting reaches the UI/device | 512-byte CBOR cap, bounded parser depth/collections, one-to-four RGB policy, exact path allowlist, and decoded read-back |
| Malformed or hostile local profile JSON affects the app/device | Separate 16 KiB-capped documents, schema and value validation, traversal-safe filenames, reparse-point rejection, atomic saves, malformed-file isolation, and a separate safety-gated device-write action |
| Repeated boosts run without bound | Device limit capped again at four boosts per session |
| User-configured quick hit exceeds safe limits | Save-time validation plus per-command amount, cumulative temperature, and cumulative duration checks |
| Personal device data is collected | No cloud, analytics, account, serial-number read, or address log |
| Handoff targets the wrong Bluetooth product | Heating-state gate, Peak-name candidate filter, normal pairing/authentication, and post-connect Peak Pro identity check |
| Handoff interrupts another controller | No forced takeover; Windows Bluetooth exclusivity is respected and the connection fails closed |
| Dependency or CI action drifts | Central versions, lock files, NuGet audit, and SHA-pinned actions |

## Residual risks

The Lorax protocol has no public manufacturer specification, Windows Bluetooth
drivers vary, and physical device behavior has not yet been validated. Therefore
version 0.1 makes no real-device write available. Future signed releases will
also require a protected code-signing process; current local development builds
are unsigned and Windows may show a reputation warning.

No software can guarantee that third-party hardware will never fail. The goal is
to minimize reachable behavior, fail closed, keep the physical stop control
authoritative, and require evidence before expanding compatibility.
