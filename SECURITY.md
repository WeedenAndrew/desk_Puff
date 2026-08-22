# Security Policy

## Supported versions

Only the latest published release receives security fixes while the project is
pre-1.0.

## Reporting a vulnerability

Do not open a public issue for vulnerabilities that could trigger unauthorized
device writes, bypass safety gates, expose identifiers, or access firmware
services. Use GitHub's private security advisory feature for this repository.

Include the affected version, device/firmware if relevant, reproduction steps,
impact, and any proposed mitigation. Do not test against devices you do not own
or have explicit permission to use.

## Scope

High-priority issues include command replay, unsafe range bypasses, writes before
authentication, unredacted identifiers, arbitrary Lorax access, OTA/bootloader
reachability, dependency compromise, and unsigned release substitution.
