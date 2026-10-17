# Run card — capture what the phone app writes

Passive observation. **No writes from desk_Puff, and desk_Puff does not connect
at all.** The phone app drives the device; Android records the Bluetooth traffic;
we read it afterwards.

Goal: find the path the phone app writes to run a one-off profile, and settle
whether the +15 °F and 30 s boost ceilings are the app's policy or the firmware's.

---

## ABORT

**Press and hold the device's button until it powers off.** Unchanged, and it
works regardless of what the phone or the computer is doing.

---

## Before anything

> [!CAUTION]
> **The capture file must never enter the repository.** A btsnoop log contains
> the device's Bluetooth address, your phone's address, and traffic from every
> other Bluetooth device connected during the capture — headphones, watch, car.
> `docs/SAFETY.md` forbids committing raw captures containing identifiers, and
> this is exactly that. Keep it outside the repo, extract findings by hand, and
> quote only paths, opcodes and values.

**desk_Puff must be closed.** A BLE peripheral accepts one central connection at
a time, and the phone app needs it exclusively. If desk_Puff is connected the
capture will show nothing useful.

Turn off other Bluetooth devices you can spare. Every one of them adds traffic to
the same log.

## 1. Enable the snoop log

Android Developer Options → **Enable Bluetooth HCI snoop log**.

Then **toggle Bluetooth off and on.** On most Android builds the log does not
start until the stack restarts, and skipping this produces an empty or stale
file — check this before running anything, not after.

## 2. Pick a temperature that appears nowhere else

This is what makes the log readable. The device's four slots hold 450, 420, 500
and 480 °F, so any of those is ambiguous. Use a value that exists nowhere:

| one-off temperature | Celsius | f32 little-endian — search for this |
|---|---|---|
| **467 °F** | 241.666667 | `AB AA 71 43` |
| 473 °F | 245.000000 | `00 00 75 43` |
| 487 °F | 252.777778 | `1C C7 7C 43` |
| 493 °F | 256.111111 | `39 0E 80 43` |

Pick one. If the app only offers 5 °F steps, 475 or 485 works — just compute it
or ask and avoid the four slot values.

Also pick a **distinctive duration** that is not 90 or 120 s. 75 s or 105 s.

## 3. The sequence

Write down the wall-clock time at each step. That is how you find each event in
a log with thousands of packets.

1. Open the Puffco app. Let it connect and settle. **Note the time.**
2. Run a **normal saved-slot session** — profile 3, unchanged. Let it finish.
   This is the reference: it shows what a slot-based start looks like.
   **Note the time.**
3. Now set up a **one-off** at your distinctive temperature and duration, without
   saving it to a slot. **Note the time before you start it.**
4. Start it. Let it reach temperature.
5. **Boost temperature repeatedly until the app refuses.** Note how many boosts
   it accepted and what it said when it stopped. **Note the time.**
6. **Boost time repeatedly until the app refuses.** Same. **Note the time.**
7. Let the session end on its own. Close the app.

Do not power the device off during the run unless something is wrong — a clean
end gives a clean tail to the log.

## 4. What to look for

Open the log in Wireshark. Filter to ATT writes.

**The one-off setter.** Search for the f32 bytes from step 2. The write carrying
them names the path. The hypothesis is `/p/app/thc/temp` and `/p/app/thc/time`
— `thc` looks like the live heat cycle's own parameters, which `hcs` normally
populates from a slot, and `tcmd = thc/temp + tmpo` was measured across 80
samples. **If it is a different path, the hypothesis was wrong and the real path
is what matters.** Report what is there, not what was expected.

**Whether any slot was touched.** Search for `/u/app/hc/`. A one-off should write
none of those. If it writes a slot and restores it afterwards, that is the answer
too — and it means the phone app does the thing we ruled out as unsafe.

**The boost ceilings.** Count the `tmpo` writes and read their values. Expect
`1D C7 31 40` (2.777778, the device's +5 °F step) accumulating.

| if the app stops at | then |
|---|---|
| `55 55 05 41` (8.333333 = +15 °F) | the +15 °F ceiling is confirmed, source still ambiguous |
| a **higher** value | +15 °F is the phone app's policy, not the firmware's — our catalog is conservative, correctly |
| a value the device **rejects** with a status byte | the ceiling is firmware, which is the strongest result |

A rejection status in the reply is the decisive evidence. `0x7A` is
limit-exceeded; anything non-zero is worth quoting exactly.

**Time boosts.** Same, with `00 00 20 41` steps. Does it reach `00 00 F0 41`
(30.0 s) and stop, or go past it?

## 5. Extract the log

The file is normally `/data/misc/bluetooth/logs/btsnoop_hci.log`, but a direct
`adb pull` usually fails without root on current Android. The reliable route:

```
adb bugreport bugreport.zip
```

The snoop log is inside, under `FS/data/misc/bluetooth/logs/`. Extraction details
vary by Android version and vendor — if neither route produces a file, say so
rather than guessing at it.

**Turn the snoop log back off afterwards.** It records everything Bluetooth your
phone does, continuously.

## 6. Bring back

- The wall-clock notes from step 3 — as useful as the log itself
- How many boosts of each kind the app accepted, and the exact refusal wording
- The write frames for the one-off: path, opcode, offset, value bytes
- Whether anything under `/u/app/hc/` was written

**Not the log file.** Keep it local. Quote from it.
