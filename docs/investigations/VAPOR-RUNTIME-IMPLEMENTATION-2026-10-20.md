# Temporary-session vapor implementation — 2026-10-20

## Implemented scope

Explicit `VaporMode` is separate from legacy `Vapor` in schema1 local heating
JSON. Smooth/Bold/Intense/Extreme encode as float32LE0/0.5/1/1.5 through an
explicit codec, not enum ordinals. New drafts show Smooth; existing files with
no named mode retain their old value and need one explicit choice/save.
Saving offline remains available. Colors and names stay device-owned/display
only; this implementation does not fabricate runtime color transfers.

Production activation is narrow: existing admitted PeakPro13/AN identity,
API39, chamber2(3DXL), valid readable runtime context, and verified limits.
AW-1d90ae1 is user-reported and not newly admitted. The known legacy firmware
payload27000000 (and one-byte27) remains AN; complete printable firmware labels
are preserved, arbitrary binary suffixes rejected. Raw firmware bytes remain
available as `DeviceIdentity.FirmwareWireHex`; API is never an identity fallback.
Enabling the captured runtime protocol within existing AN admission is a
software inference, not new cross-version hardware validation.

## Runtime transaction and recovery

Under the controller command gate, fresh idle preflight snapshots original hcs,
exact temperature/time/intensity bytes, runtime name/color, zero boost offsets,
and all four slots' scalar/name/color bytes. Originals must be restorable within
verified bounds and intensity must exactly match a known encoding. Unsupported
name/color state or nonzero pre-existing boost offsets is refused before mutation.

Time is written first, then temperature and intensity, each with flags04,
status/readback verification, followed by hcsFF/readback. No mc02 is inferred
or transmitted. Another fresh verification precedes Start. FF is represented
as app-only context, never slot255 or a substituted saved slot. Boosts use the
captured source slot's existing step sizes and the verified cumulative limits.

Original bytes are retained before the first frame, including lost-reply
uncertainty. Cleanup uses a bounded independent token, restores original
selection followed by original runtime scalars, verifies opaque name/color and
all slot snapshots, and restores captured zero boost offsets if needed. It
performs no saved-slot writes or unsupported runtime color writes.

The recovery obligation is bound to the same opaque candidate identity in
memory; identity is not logged. Disconnect invalidates application proof without
erasing originals. Same-device reconnect recovers when idle; reconnect during
heat retains cleanup tracking until Idle. A different device cannot consume
another device's recovery. External selection/runtime/slot/opaque changes
cause refusal before cleanup writes; uncertain recovery blocks Start and
preserves Stop. Explicitly resumed partial cleanup retains its own mutation
tracking rather than assuming an external reselect. Process loss cannot retain
in-memory originals; an untracked FF context is detected and cannot start as
a saved/device slot. The user must explicitly reselect a device profile.

The implementation requires original runtime name/color to match the source
slot exactly, and verifies they remain unchanged. It may refuse unsupported
real-device document layouts. This is deliberate: no invented program or color
replacement is sent to make a vapor request appear successful.

## Evidence and validation limits

The [official-app capture](VAPOR-APP-ONLY-SESSION-2026-10-17.md) directly observed
Extreme1.5 and Intense1.0 runtime writes, matching reads, hcsFF, and start. All
four scalar values have persistent-slot capture evidence. Smooth/Bold runtime
writes, exact original-byte recovery, zero-boost restoration, and activation on
the currently admitted AN tuple still need physical validation. Software tests
are fake-wire evidence only; they do not establish physical behavior.

Tests cover all four explicit encodings, real managed framing, lost replies and
cancellation at application boundaries, recovery ownership/bounds, reconnect
while heating, cleanup uncertainty, boost restoration, JSON preservation,
legacy explicit choice, offline save, and real headless 460x760 rendering.

## Use and build

Open Profiles, load an older heat JSON, choose a named vapor mode, and save once.
Then use the saved profile for the session. A read-only firmware message means
this exact device identity has not been admitted; no AW bypass is provided.

Rebuild/publish current source with the documented Windows-x64 publish profile;
launch `artifacts/publish/win-x64/desk_Puff.exe` after rebuilding, with its existing
`ble/desk-puff-ble.exe`. A previously published executable stays stale until
rebuilt.
