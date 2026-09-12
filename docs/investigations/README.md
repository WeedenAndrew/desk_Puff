# Investigations

Working material from protocol and hardware investigations: the staged prompts
that drove each one, and the run cards used at the device.

These are **process records, not specifications.** They contain superseded
reasoning, hypotheses that turned out wrong, and predictions made before the
evidence arrived — deliberately, because the corrections are the useful part.
Anything load-bearing is promoted out of here:

| finding | lives in |
|---|---|
| validated hardware behaviour | `docs/compatibility/` |
| limit values and their provenance | `CompatibilityCatalog.cs` |
| the verification ladder | `docs/SAFETY.md` |

So do not cite a file here as authority for how the device behaves. If a claim
matters, it should already be somewhere in the table above; if it is only here,
it has not been established yet.

| file | investigation |
|---|---|
| `PROMPT-CBOR-LIMITS.md` | the CBOR reader, and what the `GetLimits` numbers mean. Established 125 as the maximum read payload and `0x7A` as the limit-exceeded status. 40 and 8 are still unidentified. |
| `PROMPT-BOOST.md` | the first real device write — boost, from stub to trace to hardware. |
| `WALKTHROUGH-BOOST-TRACE.md` | build, assemble and run the suppressed-write trace. |
| `PROMPT-BOOST-STAGE3.md` | boost validation, the shared counter it exposed, and the per-type split. Its Stage 2 text is corrected by its own Stage 2b — read both. |
| `RUNCARD-BOOST-30S.md` | the run card for the 2026-09-12 validation. Abort first, `btim` gate at step 0. |
