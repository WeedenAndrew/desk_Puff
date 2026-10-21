# Architecture

```text
src/DeskPuff.App                 Avalonia UI and startup
src/DeskPuff.Core                Models, local JSON, safety, session controller
src/DeskPuff.Bluetooth.Windows   Lorax protocol, recovery, sidecar transport
native/desk-puff-ble             Rust Bluetooth helper
tests/                          Core, managed-wire, UI and render regressions
  DeskPuff.App.Tests/Support     Test-only simulator and explicit test setup
tools/capture                   Opt-in diagnostic tooling
tools/release                   Shared local/CI release assembler
docs/compatibility              Dated device verification
docs/investigations             Retained protocol evidence and investigation history
```

The production layers depend inward: App requests typed operations through
SessionController and IDeviceClient; Core has no platform or Bluetooth dependency.
The Bluetooth client encodes exact allowlisted operations and communicates over
stdin/stdout with a separate Rust helper. The helper alone opens Bluetooth.
It is resolved only at `AppContext.BaseDirectory/ble/desk-puff-ble[.exe]`, never
through PATH or the working directory. CI builds/tests/stages it; local .NET
builds alone do not produce the helper. This process boundary is described in
[THREAT_MODEL.md](THREAT_MODEL.md).

Shipping startup always constructs the real client. There is no demo client,
demo banner, demo storage root or automatic demo connection in production.
Unsupported arguments, including the removed `--demo`, return before UI/client
startup. Test support explicitly injects a simulator/fake and owns its profile
and preferences paths; screenshots render the real window without hardware.

## Commands and recovery

SessionController serializes commands and evaluates policy inside its gate.
State changes are at-most-once: uncertainty prompts verification or bounded
recovery, never a blind retry. Device identity, firmware admission, chamber and
verified limits remain independent gates; UI buttons cannot expand support.

Named temporary vapor sessions additionally require model 13 / AN, API 39, chamber 2
and runtime preflight. Original selection and runtime bytes are retained before
mutation, bound to the same connection candidate and checked during cleanup.
External context changes fail closed; disconnected recovery remains pending.
Stop remains usable when another Start is blocked. FF is a temporary context,
not a fifth device slot. Details and physical-validation limits are in the
[runtime implementation](investigations/VAPOR-RUNTIME-IMPLEMENTATION-2026-10-20.md).
Runtime mode and restoration behavior still need validation within the
recorded compatibility scope.

## Local data and lighting

Production preferences and profiles retain their existing local application-data
paths. Heating and color JSON live separately under
`%LOCALAPPDATA%/desk_Puff/profiles`. Documents are capped at 16 KiB, validated as
data, guarded against traversal/reparse files, and saved atomically. Local
saving/loading never writes a device slot. Old vapor intent is preserved until
an explicit named mode is chosen; JSON colors are display-only for live sessions.

Profile lighting reads are bounded at 4,096bytes in 125-byte chunks. Solid-color
edits patch only supported color bytes while preserving the original CBOR
metadata/program/identity representation; the entire document is verified.
Unsupported edits are refused before profile mutation. Local display palettes
allow 32 stops; device-profile writes retain their separate 1–4 stop validation.

Each run creates a diagnostic log beside the executable. Serial numbers are not
read or logged. Reads are structurally format-checked rather than allowlisted;
this remains a privacy boundary to review, not a newly implemented read gate.
`--trace-writes` retains policy checks and discards constructed allowed writes
at the transport boundary. It still connects and reads real hardware.

## Build and distribution

The Windows release is self-contained, single-file compressed, without trimming,
AOT or an external executable packer. The required BLE helper remains separate.
The release assembler retains runtime files and legal notices, excludes debug
symbols/logs, and emits an inventory plus ZIP. It does not delete local caches,
preferences, evidence or previous packages. See [RELEASE.md](RELEASE.md).
