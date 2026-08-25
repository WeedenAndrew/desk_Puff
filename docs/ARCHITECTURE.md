# Architecture

The codebase has three production layers:

- `DeskPuff.Core`: immutable models, typed device operations, safety policy, and
  the session state machine. It has no Windows or Bluetooth dependency.
- `DeskPuff.Bluetooth.Windows`: the Lorax protocol and the transport that
  carries it. Low-level writes are internal and restricted by an exact path
  allowlist. It does not open Bluetooth itself; see the sidecar below.
- `DeskPuff.App`: compact Avalonia interface. It can request only typed
  operations from `IDeviceClient`.

## The Bluetooth sidecar

Bluetooth is not opened in process. `SidecarLoraxTransport` launches a separate
Rust executable, `desk-puff-ble`, and speaks a line-oriented JSON protocol to it
over stdin and stdout, with binary payloads base64-encoded. The Rust side uses
`btleplug` and is built with `panic = "abort"`.

The helper is located at `AppContext.BaseDirectory/ble/desk-puff-ble[.exe]`.
Resolution deliberately does not consult `PATH`, the working directory, or an
environment variable, so the binary cannot be substituted by altering the
caller's environment.

**This process boundary is a trust boundary and belongs in
[THREAT_MODEL.md](THREAT_MODEL.md).** It is not listed there yet.

> **Not yet buildable.** Nothing in CI, MSBuild, or the publish profile compiles
> `native/desk-puff-ble` or stages it into `ble/`. `HelperPath()` is the only
> reference to that directory in the repository. A published build therefore has
> no helper and fails at the `File.Exists` check on the first real connection
> attempt; only `--demo` runs. Closing this is Milestone 0.

Tests mirror the production layers. The UI includes a deterministic demo client
for development without hardware; demo mode is visually marked and cannot open
Bluetooth.

Local presets are individual, size-bounded JSON documents beneath
`%LOCALAPPDATA%\desk_Puff\profiles`. Color and heating profiles live in separate
folders, with no artificial file-count limit. Each heating document contains
bounded heating values plus an inline copy of its colorway, while an optional
color-profile name is presentation metadata. Loading or refreshing these files
changes only the UI editor; device writes continue through `SessionController`
and the safety policy.

`LocalProfileLibrary` treats the folders as an untrusted local data boundary.
It rejects traversal names and reparse-point files, caps every document at 16
KiB, parses JSON as data, validates all fields, and uses a temporary file plus an
atomic move when saving. Malformed manually written profiles are skipped rather
than partially applied.

## Command flow

The UI can call only `IDeviceClient` operations. `SessionController` serializes
reads and writes, evaluates the safety policy inside the command gate, and never
retries a state change. The Windows client then converts the typed operation to
one exact allowlisted path. The transport accepts only five Lorax opcodes:
authentication, limit discovery, short reads, and short writes. There is no
production API for arbitrary paths.

Device identity is read once per connection. Changing heater telemetry is read
on the UI cadence; battery telemetry is cached for ten seconds. Serial numbers
are not requested, stored, displayed, or logged.

That last property is currently held by there being no such call, not by a gate.
`BuildWriteBody` refuses any path outside `LoraxPaths.IsWriteAllowed`;
`BuildReadBody` performs only a format check, so reads are not allowlisted. A
read allowlist would make the privacy claim structural rather than incidental.

Profile lighting is stored by the device as CBOR rather than a plain RGB value.
The Windows client uses bounded 125-byte reads, accepts only the expected nested
lighting map, and exposes one to four colors to the typed profile model. A future
verified write encodes the bounded solid-lighting form in chunks and compares the
decoded read-back before reporting success. The firmware allowlist remains the
independent gate for that write.
