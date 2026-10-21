# Vapor trace status

## 2026-10-20 software implementation status

The [temporary-session implementation](VAPOR-RUNTIME-IMPLEMENTATION-2026-10-20.md)
now supports explicit named mode intent, runtime intensity write/readback,
app-only hcs context, exact-byte recovery, and legacy JSON choice guards.
This is tested software, not physical device validation. Activation remains
inside the existing admitted 13/AN identity plus API39 / 3DXL / runtime
preflight; reported AW firmware was not admitted. The read-only PC checks
found zero candidates and further checks were stopped at the user's request.
The dated observations and their limits below remain unchanged.


## Additional 2026-10-17 outcome: runtime writes observed in app-only sessions

The [app-only session capture](VAPOR-APP-ONLY-SESSION-2026-10-17.md) now records
successful runtime `thc/intn` writes/readbacks for Extreme 1.5 and Intense 1.0,
followed by hcs FF successful write/read and mc 07. No persistent-slot requests
were observed. This supersedes the older absence-of-runtime-write findings
below for these observed actions; it does not implement or unlock JSON vapor.

Composite telemetry confirms second-session Preheating→Active→Idle, with an
Active interval of 90.000321 seconds. Later hcs 03/state 06 occurs at current 7510;
cause unknown, last observed state 6. Runtime intensity restoration remains unproven.
User-reported AW firmware remains distinct from the current AN admission.
The linked document records a reviewable implementation/cleanup design and
remaining identity, FF-context, JSON-meaning, and lifecycle gaps. Existing
production capability and safety gates remain unchanged.


## Additional 2026-10-17 outcome: four labels, handoff inconclusive

The [four-mode capture](VAPOR-FOUR-MODE-CAPTURE-2026-10-17.md) adds directly
observed Smooth 0.0 and Intense 1.0 writes/readbacks and restoration to Extreme
1.5; prior Bold 0.5 remains supported by its opposite-change capture. These
are float32 LE values for persistent slot 3, not app-enum ordinals.

The attempted idle Rosin/P selection produced no observed `hcs` or runtime
`intn` requests, so active-slot handoff and runtime reload remain inconclusive.
User-reported firmware AW-1d90ae1 and app3.13.31 are distinct from captured
API39; firmware identity/admission remains a separate boundary. Temporary JSON
session vapor is still unsupported. A separately reviewed and explicitly
authorized bounded idle runtime probe is a possible next investigation, not
an implemented or authorized operation. See the linked evidence for limits.


## 2026-10-17 outcome and next scope

The Android export now contains complete target ATT payloads. Two opposite
user-controlled official-app edits established persistent slot-3 `intn`
values for Bold (float32 LE 0.5) and Extreme (1.5), including successful
write replies and immediate matching reads. See
[Vapor capture: 2026-10-17](VAPOR-CAPTURE-2026-10-17.md) for the exact evidence,
identity limits, hashes, and record indices.

This is persistent-slot evidence only. No runtime `/p/app/thc/intn` write was
observed, the actual firmware version remains unknown, and saved JSON session
vapor stays unsupported. The next distinct question is an observed official-app
temporary-session operation and its lifecycle/restoration; do not substitute
a persistent slot edit. No additional capture or write is authorized here.

## Historical plan, retained 2026-10-17 before the successful Android capture

The text below records the earlier evidence and proposed workflow. Its claims
about what had been captured/readable apply to that earlier point in time;
the outcome above and linked evidence document supersede them only for the
proven persistent-slot fields. It is not a statement of new client support.

# Vapor capability and next passive trace

No verified Lorax field or encoding controls session vapor. The recorded
2026-09-14 Extreme-to-Bold comparison changed only a modification timestamp in
the readable profile document; candidate vapor paths returned absent. See
[the compatibility evidence](../compatibility/peak-pro-13-AN.md#vapor-is-not-in-anything-readable--closed).
The trailing older CBOR hypothesis in that document is historical, not a
discovered mapping. App enum values are not firmware encodings.

The live client writes only session time and temperature. It cannot read vapor
and does not change it. Its `Standard` admission rule does not establish the
device's current setting. Non-Standard JSON intent remains stored, but that
saved session cannot start. Device-slot writes are not a workaround: they also
have no implemented vapor write.

## A future laptop capture, without desk_Puff writes

1. Keep desk_Puff disconnected. Choose a passive capture point on the laptop's
   Bluetooth path that actually retains complete ATT request/reply payloads.
   First establish that payloads are present; the previous Android bugreport
   export stripped ACL payloads and cannot identify this command.
2. Record the model, firmware, chamber, host/capture-tool version, idle state,
   target slot and whether the official app is editing a slot or a temporary
   session. Keep identifying capture metadata outside the repository.
3. Through the official app only, compare a baseline with one vapor-only change
   and its reversal. Keep temperature, duration, boosts, colorway and slot
   selection fixed. Do not start heating for this investigation. If the app
   exposes only persistent slot editing, label that evidence accordingly; it
   does not prove temporary-session support.
4. Extract only the differing path/opcode, offset, payload format and reply
   status. Repeat the same transition to separate the setting from timestamps,
   identities and unrelated traffic. Do not infer encoding from enum order.
5. Before any desk_Puff write is designed, establish readback or another
   verifiable result, supported values, session-vs-slot scope and restoration
   semantics. Capture evidence alone is not permission to enable writes.

This is a plan, not an executed capture. Do not guess paths, remove the current
safety rule, substitute another vapor value, or automatically mutate a slot.
Raw traces may contain addresses, keys and unrelated-device traffic; keep them
outside Git and promote only sanitized findings.
