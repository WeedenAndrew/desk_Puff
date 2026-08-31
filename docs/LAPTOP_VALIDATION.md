# Laptop read-only validation

This checklist moves live Bluetooth validation to a Windows laptop without
moving desktop-specific state into Git. It is a read-only milestone. The version
0.1 verified-firmware allowlist must remain empty for the entire pass.

## What Git does and does not transfer

The repository contains source, tests, protocol fixtures, and documentation. It
does not contain Windows pairing records, Bluetooth addresses, device serials,
local preferences, saved profiles, build output, the .NET SDK, or NuGet caches.

The laptop starts with fresh local application data beneath
`%LOCALAPPDATA%\desk_Puff`. Do not copy the desktop's local application-data
folder to the laptop.

## Laptop prerequisites

- 64-bit Windows 10 or 11 with Bluetooth Low Energy support
- .NET SDK `10.0.400` or a compatible `10.0.4xx` patch selected by `global.json`
- an owned Peak Pro that is cool, charged, idle, and physically within reach
- the phone app fully disconnected from the base during the laptop test
- no packet-capture, Bluetooth-debug, or third-party controller tool attached

Confirm the laptop's Bluetooth adapter and driver work with an ordinary BLE
device before involving the e-rig.

## Clone and verify

```powershell
git clone <private-repository-url>
Set-Location .\desk_Puff

dotnet --info
dotnet restore .\desk_Puff.slnx --locked-mode
dotnet build .\desk_Puff.slnx --configuration Release --no-restore
dotnet test .\desk_Puff.slnx --configuration Release --no-build
dotnet format .\desk_Puff.slnx --verify-no-changes --no-restore
dotnet publish .\src\DeskPuff.App\DeskPuff.App.csproj `
  --configuration Release `
  --no-restore `
  -p:PublishProfile=Windows-x64
```

Do not test a live base if restore, build, tests, formatting, or publish fails.
The deterministic UI can be checked without Bluetooth by running the published
executable with `--demo`.

To inspect the complete frames that an owned, hardware-verified device would
receive without transmitting any writes, run the published executable with
`--trace-writes`. Reads and safety checks still run normally. An allowed write
is constructed and logged before it is discarded; a policy-blocked write stays
blocked. The UTF-8 log is written beside the executable as
`desk_Puff-<yyyyMMdd-HHmmss>.log` and never includes a serial number.

## Read-only live pass

1. Close the phone app and confirm the base is idle and cool.
2. Launch the published `desk_Puff.exe` without `--demo` or `--trace-writes`.
3. Scan and select only the base you own. Complete ordinary Windows pairing if
   Windows requests it.
4. Confirm the application reports the exact firmware as read-only. Stop if any
   state-changing control is enabled.
5. Compare device name, model code, firmware, chamber, battery, operating state,
   and current temperature with the physical device and expected room conditions.
6. Read every device-profile card. Compare each name, target temperature,
   duration, vapor level, and colorway without pressing Write Profile or Start.
7. Disconnect from Settings, reconnect, restart the application, and repeat the
   read. No heat cycle or profile change should occur.
8. Test one expected failure, such as declining a pairing prompt or turning
   Bluetooth off before scanning. The app must fail closed and remain read-only.
9. Reconnect the phone only after desk_Puff has disconnected and exited.

Use the physical device button and close desk_Puff immediately if the base state
or displayed telemetry is uncertain.

## Evidence to record

Copy `docs/compatibility/VALIDATION_TEMPLATE.md` to a new Markdown file named
for the product and firmware. Record only the minimum compatibility evidence.
Never commit a serial number, Bluetooth address, Windows pairing identifier,
account detail, personal device name, or raw capture containing identifiers.

If a decoder needs adjustment, first add a sanitized deterministic fixture and a
failing test. Do not add a firmware allowlist entry during this milestone.

## Completion gate

The pass succeeds only when all expected reads are plausible, reconnect behavior
is stable, failures remain read-only, no state-changing command is emitted, and
the sanitized result has test coverage. A successful read-only pass authorizes
planning the Settings UI; it does not authorize real-device writes.
