# desk_Puff

[![CI](https://github.com/WeedenAndrew/desk_Puff/actions/workflows/ci.yml/badge.svg)](https://github.com/WeedenAndrew/desk_Puff/actions/workflows/ci.yml)

A compact Bluetooth controller for app-enabled Puffco e-rigs, in a small desktop
window. No accounts, advertising, location tracking, social features, or cloud
services.

> [!WARNING]
> Early, independent, and not affiliated with Puff Corp. Live device writes are
> gated on an allowlist of exact model and firmware pairs, each with recorded
> operating limits. **One entry exists — the author's own device.** Everything
> else connects read-only and says so. Use the physical device button if
> software state is ever uncertain.

![desk_Puff home screen](docs/media/home.png)

## Supported hardware

- Peak Pro Bluetooth bases with a 3D or 3DXL chamber
- Current app-enabled New Proxy bases (experimental discovery)

The base is the Bluetooth device. A 3D or 3DXL chamber is detected through its
base and never connects independently. Products without the app-enabled
Bluetooth service are out of scope. Exact support and verification status is in
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## What works today

**The Bluetooth path runs end to end on real hardware.** A Peak Pro is
discovered, bonded, authenticated, and read: device name, chamber, battery,
charge state, live heater temperature and session timing, the four device
profile slots and their colorways. Control has been confirmed on one device —
switching profiles, stealth mode, starting a heat cycle, and disconnecting
cleanly.

There is also a deterministic demo client that exercises the whole interface
with no Bluetooth and no hardware, which is how the screenshots below are
produced.

| Profiles | Color | Settings |
|---|---|---|
| ![The Profiles page](docs/media/profiles.png) | ![The Color page](docs/media/color.png) | ![The Settings page](docs/media/settings.png) |

Profiles come in two kinds. The device's four slots behave as they always have.
Saved local profiles are JSON files that apply their parameters to a **single
session** and are dropped when it ends; selecting one writes nothing to a slot,
and the display says **SAVED PROFILE • NOT ON DEVICE** so it can never be
mistaken for the device's own state.

> [!IMPORTANT]
> **Control is limited to hardware that has been characterised, and the list is
> short.** Writes require an exact model and firmware pair on an allowlist, plus
> temperature and duration limits recorded for that pair. The list currently
> holds **one entry** — the author's own Peak Pro. Every other device connects
> read-only and says so.
>
> The gate sits below the interface. Enabling a button cannot bypass it, and
> neither can editing the allowlist alone: a device with no recorded limits is
> refused even if its firmware is listed.

Bluetooth is opened by a separate Rust helper, `desk-puff-ble`, which the
application launches from `ble/desk-puff-ble.exe` beside itself. CI builds that
helper, runs its tests, and stages it into the published package, so a
downloaded build can connect. Building locally with the `dotnet` commands below
does **not** produce it — see [Build and test](#build-and-test).

Running a saved profile for one session is implemented in the application and
the demo client only. The Bluetooth client does not implement that path, so on
hardware the app refuses and says so rather than starting the device's own slot.
The session-scoped paths `/p/app/tmpo` and `/p/app/timo` are allowlisted but
nothing writes them yet, and no session-scoped path is known for colorway or
vapor.

## Safety boundaries

The device firmware stays authoritative for heater control. The application
contains no firmware updater, bootloader access, debricking, factory reset, raw
command console, arbitrary Lorax path access, or automatic heating.

- Unknown devices and firmware are read-only.
- Commands are serialized; state-changing commands are at-most-once and are
  never blindly retried.
- Every value is validated against the device's own limits and a conservative
  absolute ceiling before encoding, and verified by read-back where the protocol
  allows it.
- Handoff is a controlled disconnect and reconnect, never a forced takeover. It
  cannot seize a base held by another controller or bypass Windows pairing.

Detail in [docs/SAFETY.md](docs/SAFETY.md),
[docs/THREAT_MODEL.md](docs/THREAT_MODEL.md), and [SECURITY.md](SECURITY.md).

## Run it

The published `desk_Puff.exe` is self-contained for 64-bit Windows 10/11.

```powershell
.\desk_Puff.exe
.\desk_Puff.exe --demo
.\desk_Puff.exe --trace-writes
```

With no flag it opens Bluetooth and looks for a device. It never connects to an
account or cloud service, and it sends nothing anywhere but the e-rig in front
of you.

`--demo` exercises the whole interface without opening Bluetooth or addressing
hardware.

`--trace-writes` connects to hardware and performs reads normally, but any write
that passes the existing safety policy is fully constructed, logged, and then
discarded before transmission. It never enables a blocked write. This lets the
interface and exact outgoing Lorax frames be inspected without changing device
state. Each run writes a UTF-8 diagnostic log beside the executable as
`desk_Puff-<yyyyMMdd-HHmmss>.log`; serial numbers are never included.

## Build and test

.NET 10 on Windows 10/11, Avalonia for the interface. A workspace-local SDK can
live in `.tools/dotnet` and is ignored by Git. `global.json` pins SDK 10.0.400,
and a contributor may have no suitable SDK on `PATH`, so prefer the local SDK
when it exists:

```powershell
$dotnet = if (Test-Path .\.tools\dotnet\dotnet.exe) {
    (Resolve-Path .\.tools\dotnet\dotnet.exe).Path
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet build   .\desk_Puff.slnx -c Release --no-restore
& $dotnet test    .\desk_Puff.slnx -c Release --no-build
& $dotnet format  .\desk_Puff.slnx --verify-no-changes --no-restore
```

### Publish and launch the demo

With `$dotnet` selected as above, publish and launch the hardware-free demo
from the repository root:

```powershell
& $dotnet restore .\desk_Puff.slnx --locked-mode
& $dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj `
    -c Release --no-restore -p:PublishProfile=Windows-x64
& .\artifacts\publish\win-x64\desk_Puff.exe --demo
```

Keep restore and publish as separate commands, and keep `--no-restore` on the
publish. A self-contained publish otherwise makes the SDK inject
`Microsoft.NET.ILLink.Tasks` into `packages.lock.json`, even though the project
does not declare it. CI's later `dotnet restore --locked-mode` then fails with
NU1004. Once published, the same executable can be launched with `--demo`
again without rebuilding.

### Run the read-only device prober

Wake the device and close the Puffco phone app first; a BLE peripheral accepts
only one central connection at a time. Run the retained PowerShell prober from
its own directory with profiles disabled and the Windows PowerShell execution
policy bypassed for this process:

```powershell
Push-Location .\tools\capture
try {
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Capture-DeviceNoise.ps1
} finally {
    Pop-Location
}
```

The prober is read-only: it never constructs the write opcode and cannot heat
the device. It leaves `survey-<stamp>.log` and `frames-<stamp>.jsonl` beside the
script so decoded results and raw frames remain paired.

Bluetooth additionally needs the Rust helper in `native/desk-puff-ble`, which
those commands do **not** produce. Build it separately:

```powershell
cargo build --release --locked
```

then copy `target\release\desk-puff-ble.exe` into a `ble\` folder beside
`desk_Puff.exe`. CI does both steps and uploads the assembled package, so a
downloaded build already has it.

## Architecture

Three layers, described in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md):

| | |
|---|---|
| `DeskPuff.Core` | models, typed device operations, safety policy, session state machine. No Windows or Bluetooth dependency. |
| `DeskPuff.Bluetooth.Windows` | the Lorax protocol and its transport. Writes are restricted by an exact path allowlist. |
| `DeskPuff.App` | Avalonia interface. Can request only typed operations. |

Bluetooth is not opened in process: a separate Rust executable speaks a JSON
protocol over stdin and stdout, and is resolved from a fixed path beside the
application.

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md). Protocol or device-write changes
need tests, source attribution, an explicit safety argument, and owned-hardware
evidence before a firmware entry can enable writes. Report security issues
through [SECURITY.md](SECURITY.md), not a public issue containing sensitive
detail.

## License

Source-available for personal and other noncommercial use under the
[PolyForm Noncommercial License 1.0.0](LICENSE.md). Not OSI-approved
open source, because the license intentionally restricts commercial use.
