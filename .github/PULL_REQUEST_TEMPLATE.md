## What changed

Describe the smallest useful change and why it is needed.

## Safety impact

- [ ] No new device write or write path is introduced.
- [ ] If a write changes, I added invariant and fault-path tests.
- [ ] Unknown firmware remains read-only.
- [ ] No serial number, Bluetooth address, pairing data, secret, or private packet capture is included.

## Verification

- [ ] `dotnet build .\desk_Puff.slnx -c Release`
- [ ] `dotnet test .\desk_Puff.slnx -c Release --no-build`
- [ ] I certified my commits as described in `CONTRIBUTING.md`.

