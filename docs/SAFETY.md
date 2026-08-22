# Device safety model

`desk_Puff` treats the Peak/Proxy base as safety-critical external hardware.
The device firmware remains authoritative for heater control.

## Non-negotiable invariants

- No writes before pairing, authentication, model recognition, firmware
  recognition, limit discovery, and chamber detection all succeed.
- Unknown devices and firmware are read-only.
- Commands are serialized and state-changing commands use at-most-once delivery.
- Lost replies cause a state read, never an automatic retry.
- Profile values are validated before encoding and verified by read-back.
- Profile lighting accepts one to four RGB colors. CBOR reads are capped at 512
  bytes and parsed with bounded depth and collection sizes; writes use the exact
  allowlisted profile-color path and require decoded color read-back.
- Heating is never scheduled, resumed, or initiated in the background.
- Profile switching and editing are disabled during heating.
- Local color and heating presets are separate, size-bounded JSON documents.
  Heating presets store only bounded heating values and one-to-four inline RGB
  colors. Saving, refreshing, or loading a local file never selects a device
  profile, starts heat, or sends Bluetooth data; the separate device-write
  action revalidates the entire pair against current hardware state and limits.
- Configurable quick-hit amounts are validated when saved and again on every
  press. Per-hit device limits, cumulative target/session limits, and the
  four-hit application cap are independent gates.
- Device handoff is disabled during heating and accepts only nearby candidates
  that advertise as Peak e-rigs. The source is released before the destination
  uses normal Windows pairing and Puffco authentication; connection takeover
  and pairing bypass are not implemented.
- Firmware, bootloader, OTA, factory reset, arbitrary files, and raw commands are
  absent from the production API.

## Hardware verification stages

1. Simulator and packet-vector tests.
2. Fuzz and fault-injection tests.
3. Physical read-only discovery and telemetry.
4. One allowlisted setting write with read-back.
5. Stop-command validation.
6. Start-command validation at a standard factory profile.
7. Boost validation.
8. Extended connection, sleep/resume, and disconnect testing.

A stage must pass before the next begins. Hardware validation results belong in
`docs/compatibility/` without serial numbers or Bluetooth addresses.

## Version 0.1 control lock

The hardware-verified firmware allowlist is intentionally empty. Device limits
are also left unavailable until their response encoding is confirmed on owned
hardware. Either condition independently blocks every write. This means the
first physical test can inspect discovery and telemetry but cannot start heat,
stop heat, select or edit a profile, boost, or change lighting.

The demo client is the only version 0.1 client with controls enabled. It has no
Bluetooth dependency and cannot address physical hardware.

## Safe device handoff

The Settings handoff control is intentionally not a force-connect mechanism.
It scans while the current base is idle, requires the user to select a target,
disconnects the source cleanly, and then performs the ordinary authenticated
connection flow. If the destination does not identify as a supported Peak Pro,
the app disconnects it and fails closed. A device held by another Bluetooth
central remains unavailable; desk_Puff does not attempt to evict that central.
