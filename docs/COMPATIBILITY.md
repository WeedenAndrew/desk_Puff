# Compatibility

Compatibility claims are deliberately narrower than product names. A device is
eligible only if its base advertises the app-enabled Bluetooth service and the
Lorax service can be opened through Windows.

| Product | Chamber | Discovery | Read-only telemetry | Device control |
| --- | --- | --- | --- | --- |
| Peak Pro Bluetooth base | 3D | Implemented; physical validation pending | Implemented; physical validation pending | Safety-locked |
| Peak Pro Bluetooth base | 3DXL | Validated on hardware | Validated on hardware | Validated on model 13 / firmware AN only |
| New Proxy app-enabled base | Product chamber | Experimental name/service detection | Experimental; physical validation pending | Safety-locked |

The validated row covers **one physical device**, model code 13 on firmware AN.
Every other model and firmware pair is read-only regardless of product name. What
has been exercised on that device, and the two defects it exposed, is recorded in
[compatibility/peak-pro-13-AN.md](compatibility/peak-pro-13-AN.md): allowlisted
setting writes with read-back, stop, start at a factory profile, profile
switching, and both quick-hit boosts to their cumulative ceilings.

The New Peak, Pivot, legacy Proxy, original Peak, and any other product that
does not expose the app-enabled Bluetooth service are excluded. A 3D or 3DXL
chamber is not itself a Bluetooth device.

## Safe first hardware check

For a full laptop handoff, prerequisites, clean build commands, privacy rules,
failure checks, and the evidence template, follow
[LAPTOP_VALIDATION.md](LAPTOP_VALIDATION.md).

1. Make sure the device is cool, charged, idle, and within reach of its physical
   stop control.
2. Run `desk_Puff.exe --trace-writes` and choose **Scan again**.
3. Connect only to a base you own. Unknown firmware stays read-only; an admitted identity can show control
   enabled. During this read-only procedure, use only reads and retain trace
   mode as a transport-level write guard.
4. In Settings, record only the displayed family, model code, firmware, chamber
   type, and whether battery/temperature/state appear plausible.
5. Do not publish a serial number, Bluetooth address, pairing record, or packet
   capture containing personal identifiers.

Physical results should be reviewed before any allowlist entry or device limit
decoder is added. Control enablement requires a separate code change, tests,
review, and the staged procedure in [SAFETY.md](SAFETY.md).
