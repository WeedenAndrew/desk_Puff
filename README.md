# desk_Puff

`desk_Puff` is a compact, Windows-native Bluetooth controller for supported,
app-enabled Puffco e-rigs. It keeps the useful device controls in a small desktop
window without accounts, advertising, location tracking, social features, or
cloud services.

> [!WARNING]
> This project is early, independent, and not affiliated with Puff Corp. Live
> device writes remain gated until the exact device and firmware combination has
> passed the hardware safety suite. Use the physical device button if software
> state is ever uncertain.

## Supported hardware

- Peak Pro Bluetooth bases with a 3D or 3DXL chamber
- Current app-enabled New Proxy bases (experimental discovery)

Only products that expose a compatible Bluetooth connection directly to the app
are in scope. The base is the Bluetooth device. A 3D or 3DXL chamber is detected
through its connected base and never connects independently. Non-Bluetooth
products are intentionally excluded.

See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the exact support and
verification matrix.

Live Bluetooth validation can be performed from a separate Windows laptop; the
development desktop does not need a Bluetooth adapter. Follow the strict
[laptop read-only validation checklist](docs/LAPTOP_VALIDATION.md). Local build
output, SDKs, package caches, pairing state, and device identifiers are excluded
from Git.

## Front-end tour

The interface uses a fixed 458 by 758 logical layout that scales uniformly with
the window, so fonts, controls, and spacing grow together. The default footprint
is roughly phone-sized, but the window can be resized or maximized. Home,
Profiles, and Color now form the version 0.1 visual baseline. Settings remains
provisional and will be finalized after the first read-only hardware-validation
milestone.

### Home

- The title bar opens **Settings** from the gear and identifies the connected
  model. In demo mode, the model badge is prefixed with `DEMO`.
- The header shows four battery blocks plus the percentage on the left, the
  device's custom name and vapor level in the center, and chamber type as text on
  the right.
- The large center circle shows the active profile name, live chamber temperature
  while heating (or the saved target while idle), and session time. Its segmented
  rim blends the one-to-four colors stored in the active device profile.
- Pressing the circle starts an allowed heat cycle. During preheat or an active
  session it changes to **Stop**, displays live sensor temperature, and counts the
  session down. Start is never available without a recognized chamber, a valid
  temperature reading, an idle device, and a verified writable firmware profile.
- The left and right buttons change profiles. Left Arrow and Right Arrow are the
  defaults and can be reassigned in Settings. Profile changes never start heat.
- The temperature and time quick-hit buttons apply one configured increment per
  click. Up Arrow and Down Arrow are their defaults. They are available only
  during an active session, are rechecked against device limits every time, and
  share a four-increment application cap per session.

The bottom navigation keeps **Profiles** on the left, **Home** in the center, and
**Color** on the right.

### Profiles

The device has four bounded on-device profile slots; desk_Puff does not invent
extra slots that the base cannot store. A horizontal device-profile strip lets
you click a slot directly, while the center presents a smaller preview of the
Home session circle. The preview shows the profile name, temperature, session
time, and segmented device colorway. Target temperature and session time remain
direct-entry fields beneath vapor level, alongside the boost controls.
Selection is blocked during a heat cycle and on unverified firmware.

For the selected slot, the editor exposes:

- name (1 to 31 characters);
- target temperature and session duration;
- vapor level;
- configured temperature and time boost values;
- the profile color shared with the Color page; and
- an optional local keyboard macro.

The **Color Profile** row opens the Color selector directly. After choosing or
editing a colorway, **Use with Heat Profile** returns to Profiles without writing
to the device. **Save Heat JSON** stores the heating values and an inline copy of
that colorway together. There is no artificial profile-count limit. Loading a
pair updates only the editor; **Write Profile** remains the separate,
safety-gated device write.

A profile macro selects that profile from Home only while the device is safe to
edit. It never starts a heat cycle. Macro keys cannot conflict with profile arrows,
quick-hit keys, or another profile macro. Profile data is validated against both
conservative application bounds and the connected device's verified limits before
any write is attempted.

### Color

Color edits apply to the currently selected profile. The page is headed by that
profile's name. desk_Puff reads the bounded CBOR lighting object from the profile
and displays up to four RGB colors as one full-width blended surface with
contrast-aware text. A wheel plus brightness rail edits the selected color stop; the
previous/next, add, and remove controls manage the one-to-four-color sequence.
The former base-color buttons and visible hex fields are removed.

The bottom color-profile window pans horizontally through the JSON-backed
library, showing each name above its blended bar. **Use Profile** and **Delete
Profile** sit above the library; clicking a card selects it. **Use with Heat
Profile** returns the planned colorway to the heating editor, and **Write Profile**
performs the separately gated device update. The heating-profile library uses
the same click-to-select, horizontally pannable layout. Invalid colors, oversized
documents, malformed CBOR, and failed read-back are rejected.

### Settings

Settings contains:

- the current control/read-only safety status;
- connected device name, family, model code, and firmware;
- a locally saved six-digit RGB hex value for the app accent, with live preview
  and automatic light/dark text contrast (`#BB376A` by default);
- rebindable previous profile, next profile, temperature quick-hit, and time
  quick-hit keys;
- configurable temperature and time increments for the quick-hit buttons;
- Safe E-Rig Handoff for moving the app to another nearby Peak base;
- Fahrenheit/Celsius display selection;
- stealth and lantern controls where verified and supported; and
- an explicit safe disconnect.

Preferences, the app accent, and macros remain in
`%LOCALAPPDATA%\desk_Puff\preferences.json`. Color and heating profiles are
separate, manually editable JSON documents in:

- `%LOCALAPPDATA%\desk_Puff\profiles\colors\`
- `%LOCALAPPDATA%\desk_Puff\profiles\heating\`

There is no artificial file-count limit. Each document is independently
size-bounded, parsed as data rather than code, and validated before display or
use. **Refresh JSON** reloads manual edits without restarting the app. These
files contain no account credentials or device secrets.

A manually written color profile uses this shape:

```json
{
  "schemaVersion": 1,
  "name": "Aurora",
  "colors": ["#581CFF", "#20DCE5", "#6BFF8F"]
}
```

A heating profile stores its own inline color copy so it still works if the
separately named color profile is renamed or deleted:

```json
{
  "schemaVersion": 1,
  "name": "Evening",
  "deviceProfileName": "PURPLE",
  "targetTemperatureCelsius": 260,
  "durationSeconds": 40,
  "vapor": "Standard",
  "boostTemperatureCelsius": 5,
  "boostDurationSeconds": 10,
  "colorProfileName": "Aurora",
  "colors": ["#581CFF", "#20DCE5", "#6BFF8F"]
}
```

Names must be 1–64 characters, color arrays must contain 1–4 six-digit RGB
values, and heating values must remain inside the documented safety bounds.
Invalid files are ignored rather than partially loaded.

## How the Bluetooth drop works

The Settings feature labeled **Safe E-Rig Handoff** is desk_Puff's Bluetooth
"drop". It is a controlled disconnect-and-connect sequence, not a forced takeover:

1. The current base must be connected, authenticated, and idle, sleeping, or
   powered off. Handoff is blocked during preheat, an active session, cooling, or
   any uncertain operating state.
2. **Find Nearby E-Rigs** performs a time-bounded Windows Bluetooth scan. Only
   Peak-named e-rig candidates are offered, and the currently connected address is
   excluded.
3. After the user chooses a target, desk_Puff safely stops polling and disconnects
   from the current base before attempting the new connection.
4. Windows performs normal pairing where required, then desk_Puff completes the
   Puffco authentication exchange. The destination must authenticate and identify
   as a supported Peak e-rig.
5. Only after identity checks pass does desk_Puff load telemetry and profiles. An
   unverified firmware remains connected in read-only mode. If connection or
   validation fails, the destination is disconnected and no state-changing command
   is sent.

The feature cannot seize a base held by a phone or another computer, bypass
Windows pairing, interrupt heat to take control, or silently choose a device. The
clean release prevents two desk_Puff-controlled bases from being active in the
same handoff. If handoff fails after release, select and reconnect the original
base normally.

## What works today

Version 0.1 provides Windows BLE discovery and pairing, Lorax authentication,
read-only device identity and telemetry, profile and colorway reading, live temperature and
countdown display, local preferences/keybinds/macros, and the complete deterministic
demo. The demo supports profile selection/editing, color selection, start/stop,
quick hits, and handoff without opening Bluetooth or touching hardware.

Real-device state-changing writes are deliberately locked because no exact
model/firmware pair has completed the owned-hardware safety sequence yet. The
empty firmware allowlist is enforced below the UI; changing or enabling a button
cannot bypass it. Profile color CBOR encoding and read-back verification are now
wired, but no physical color write can run until an exact firmware entry passes
owned-hardware validation. Profile vapor writes and independent boost remain
unavailable until their layouts are verified on owned hardware.

## Next move: read-only hardware validation

The next milestone is a real Peak Pro connection that proves discovery,
authentication, identity, chamber detection, telemetry, and profile reads while
the write allowlist remains empty. It does **not** enable heating, boosting,
profile writes, lighting writes, firmware operations, or raw protocol access.

The validation pass is deliberately staged:

1. Connect an owned, cool, idle, charged Peak Pro through normal Windows pairing.
2. Confirm the reported model code, firmware, chamber type, battery, operating
   state, and current temperature are plausible.
3. Read all four profile slots and verify names, target temperatures, durations,
   vapor values, and one-to-four-color CBOR lighting objects without modifying
   the base.
4. Exercise disconnect, reconnect, sleep/wake, app restart, and failed-auth paths
   while confirming that no state-changing packet is emitted.
5. Record only sanitized compatibility evidence—never serial numbers, Bluetooth
   addresses, pairing records, or other personal identifiers.
6. Turn every confirmed response into a deterministic packet fixture or simulator
   test before changing a decoder or compatibility claim.

The laptop procedure and sanitized result template live in
[docs/LAPTOP_VALIDATION.md](docs/LAPTOP_VALIDATION.md) and
[docs/compatibility/VALIDATION_TEMPLATE.md](docs/compatibility/VALIDATION_TEMPLATE.md).

This milestone is complete only when telemetry stays stable, malformed and
unexpected responses fail closed, reconnects do not duplicate commands, and the
exact tested model/firmware/chamber combination is documented in
`docs/COMPATIBILITY.md`. The firmware write allowlist remains empty afterward;
write validation is a later, separately reviewed milestone.

## Settings comes after validation

Settings is the next UI planning pass after read-only hardware behavior is known.
It will consolidate connection details, keybinds, increments, handoff, units,
appearance, diagnostics, and additional user-facing catches. Those controls may
add confirmations and make safety state clearer, but they are never the sole
enforcement layer. Chamber checks, firmware allowlisting, operating-state gates,
bounds validation, command serialization, at-most-once writes, and read-back
requirements remain below the UI and cannot be disabled by a hidden setting,
edited preference, or visual change.

No advanced or diagnostic setting will provide a raw command console, arbitrary
Lorax path, firmware updater, bootloader access, forced Bluetooth takeover, or a
switch that bypasses the read-only lock.

## Safety boundaries

The production application contains no firmware updater, bootloader access,
debricking, factory reset, raw command console, arbitrary Lorax path access, or
automatic heating. Unknown firmware is read-only. All state-changing commands are
serialized, issued at most once, and never blindly retried. Relevant writes use
bounded paths and read-back verification where the protocol supports it.

See [docs/SAFETY.md](docs/SAFETY.md), [SECURITY.md](SECURITY.md), and
[docs/THREAT_MODEL.md](docs/THREAT_MODEL.md).

## Run the interface

The published `desk_Puff.exe` is self-contained for 64-bit Windows 10/11. Run it
normally for local Bluetooth discovery. Run it with `--demo` to exercise the full
interface without opening Bluetooth or touching hardware.

```powershell
.\desk_Puff.exe --demo
```

The application never connects to an account or cloud service.

## Build and test

The project targets .NET 10 on Windows 10/11. A workspace-local SDK can live in
`.tools/dotnet` and is ignored by Git.

```powershell
.\.tools\dotnet\dotnet.exe restore .\desk_Puff.slnx --locked-mode
.\.tools\dotnet\dotnet.exe build .\desk_Puff.slnx -c Release --no-restore
.\.tools\dotnet\dotnet.exe test .\desk_Puff.slnx -c Release --no-build
.\.tools\dotnet\dotnet.exe format .\desk_Puff.slnx --verify-no-changes --no-restore
.\.tools\dotnet\dotnet.exe publish .\src\DeskPuff.App -c Release --no-restore -p:PublishProfile=Windows-x64
```

To preview the interface without hardware:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\src\DeskPuff.App -- --demo
```

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md). Protocol or device-write changes
must include tests, source attribution, an explicit safety argument, and owned-
hardware verification evidence before a firmware entry can enable writes.
Security issues should be reported through [SECURITY.md](SECURITY.md), not a public
issue containing sensitive details.

## License

Source is available for personal and other noncommercial use under the
[PolyForm Noncommercial License 1.0.0](LICENSE). This is community-contributable
source-available software, not OSI-approved open-source software, because its
license intentionally restricts commercial use.
