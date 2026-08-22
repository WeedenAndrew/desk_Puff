# Contributing

Thank you for helping make `desk_Puff` safer and more reliable.

## Ground rules

1. Open an issue before introducing new device writes or compatibility claims.
2. Keep the UI dependent only on typed domain operations. Raw BLE and Lorax
   operations belong in the Windows Bluetooth assembly.
3. Do not add firmware, bootloader, reset, raw-command, automatic-heating, or
   arbitrary-path features.
4. Add tests for every protocol change and every newly supported device state.
5. Never commit device serial numbers, Bluetooth addresses, pairing data,
   packet captures containing personal identifiers, secrets, or signing keys.
6. Treat a device/firmware pair as read-only until hardware verification is
   documented.

## Pull requests

- Keep changes focused and formatted with `dotnet format`.
- Build with warnings treated as errors.
- Include unit, fault-injection, and safety-invariant tests where applicable.
- Update documentation and the compatibility matrix.
- Certify every commit under the repository license:

  `Certified-by: Your Name <your@email.example>`

By doing so, you certify the statement in
[CONTRIBUTOR_CERTIFICATE.md](CONTRIBUTOR_CERTIFICATE.md).
