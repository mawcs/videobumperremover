# Working doc: code organization (VBR vs. VDF)

**Status:** working doc / for discussion — *not* decided. When we converge, promote to an ADR.

We forked Video Duplicate Finder (VDF) and have been adding diagnostic probes inside VDF's own
test project for expediency. Before real development, we need to decide how *our* code
(VideoBumperRemover — "VBR") is organized relative to VDF's. Captures the maintainer's concerns
plus options and a leaning for each.

## The maintainer's concerns (as raised)

1. The new probes live in the VDF tree (`VDF.IntegrationTests`) for good reason, but we don't
   want to keep growing *our* code inside VDF's projects. **Prefer a separate tree.**
2. VDF is a set of subprojects (`VDF.Core`, `VDF.GUI`, `VDF.CLI`, `VDF.Web`, each with its own
   `.csproj`). Our development will mirror this — **should we have our own parallel set of
   subprojects (`VBR.*`)?**
3. We'll eventually **remove** parts of VDF and **significantly modify** others. How do we do
   that while keeping VBR and VDF cleanly separate?
4. When we significantly modify a VDF file, **do we add our copyright, and where does that file
   live?**

## Background: how the reuse actually works today

The primitives we depend on in `VDF.Core` are mostly `internal` (e.g. `ChromaprintEngine`,
`OnnxEmbedder`, `ScanEngine.SlidingWindowCompare`, `ScanEngine.TryMatchDenseFrames`,
`FfmpegEngine.GetDenseAiFrames`). Our probes reach them only because `VDF.IntegrationTests` is
granted `InternalsVisibleTo`. Any separate `VBR.*` project needs one of: (a) VDF grants
`InternalsVisibleTo` to it, (b) those primitives become `public`, or (c) we fork the primitives
into VBR. This choice drives most of what follows.

## Concern 1 & 2 — a separate `VBR.*` tree

**Leaning: yes.** Create our own subprojects that reference `VDF.Core` as an engine dependency.
A strawman layout (react to it):

- **`VBR.Core`** — everything that's *ours*: edge-scan, matchers (presence/rigid wrappers +
  thresholds), the catalog, boundary detection, the removal engine. References `VDF.Core`.
- **`VBR.Gui`** — our Avalonia UI (new, or a reshaped copy of `VDF.GUI`).
- **`VBR.Tests`** — our tests; the diagnostic probes graduate here from `VDF.IntegrationTests`.
- Namespace: `VideoBumperRemover.*` (matches the license header) or short `Vbr.*`. TBD.

Keep `VDF.Core` as the inherited engine. **Drop** the VDF projects we don't use (candidates:
`VDF.Web`, `VDF.CLI`, `VDF.Benchmarks`) by removing them from the `.sln` and deleting the folders
— they're independent, so this is clean and doesn't affect `VDF.Core` or upstream merges.
`VDF.GUI`: keep as a UI starting point, or replace with `VBR.Gui`.

Bridging the `internal` gap (pick one, low → high divergence):

- **Minimal:** add `[assembly: InternalsVisibleTo("VBR.Core")]` to `VDF.Core`. One tiny edit to
  an upstream file; low merge-conflict risk; lets VBR.Core use VDF internals directly. *Leaning.*
- **Cleaner API:** make the specific primitives we use `public`. Larger, more deliberate upstream
  edit; a real "engine API." Consider once we know exactly which primitives we depend on.
- **Fork:** copy the primitive files into VBR. Most control, but we lose upstream updates for them.

## Concern 3 — removing / diverging from VDF while staying separate

Framing it as a spectrum, and a hybrid we can slide along over time:

- **Track upstream (mostly add):** keep `VDF.Core` as a dependency, build all new logic in
  `VBR.*`, touch VDF only for tiny glue (e.g. `InternalsVisibleTo`). Best mergeability; we pull
  VDF updates. Good for *now*.
- **Absorb (mostly rewrite):** fork the pieces we heavily change into `VBR.*`; let the VDF
  originals wither or be deleted. Full control + our copyright; lose upstream updates for those
  pieces.
- **Hybrid (recommended path):** stay on "track upstream" while our net-new work lives in
  `VBR.*`. When a specific VDF file needs *substantial* change, **fork that file into `VBR.*`**
  rather than editing it in place. Over time more moves to VBR and `VDF.Core` shrinks toward just
  the primitives; if/when divergence is large, we can **vendor-freeze** `VDF.Core` (stop tracking
  upstream) as a deliberate, later decision.

"Removing VDF code" then means two different things: (a) dropping whole unused **projects** —
clean, do it freely; (b) not *using* parts of `VDF.Core` — fine, they just sit unused as long as
`VDF.Core` is a dependency; only vendor-freezing lets us physically delete inside it.

## Concern 4 — copyright on modified VDF files, and where they live

AGPL rule of thumb: when you modify an AGPL file you **keep the original copyright and add yours**
(and note it's modified) — you never remove `0x90d`'s notice. So a changed file carries *both*:

```
// Copyright (C) 2026 0x90d          (original)
// Modifications Copyright (C) 2026 mawcs
```

Where it lives — tie it to the divergence rule above:

- **Trivial/surgical edit** (e.g. add `InternalsVisibleTo`, one-line fix): edit **in place** in
  `VDF.Core`; add the "Modifications Copyright" line. Accept minor future merge friction on that
  file.
- **Substantial change** (real new logic): **fork the file into `VBR.*`**, carry both copyrights,
  and leave `VDF.Core`'s original pristine (keeps VDF.Core mergeable; our fork owns the changes).

Rule to adopt: *glue stays in VDF (dual copyright); real changes fork into VBR (dual copyright);
brand-new files are pure VBR (our header only).*

## Also shaping the structure — the two-tier matching design

Module boundaries should anticipate the two paths (from the interstitial discussion): a **fast,
optimized edge path** (dense-fingerprint only first/last N seconds — the common case) and a
**heavier mid-video interstitial path** (full-timeline scan, run on demand). Likely distinct
modules/strategies under `VBR.Core` sharing the same fingerprint/embedding primitives.

## Open questions to resolve before the ADR

- Namespace/assembly naming: `VideoBumperRemover.*` vs `Vbr.*`.
- Which VDF projects to drop now vs. keep (`VDF.Web`, `VDF.CLI`, `VDF.Benchmarks`, `VDF.GUI`).
- `InternalsVisibleTo` vs. public-API vs. fork for the primitives — and the exact primitive list.
- The threshold for "substantial change → fork the file," stated concretely.
- Whether/when to vendor-freeze `VDF.Core`.
- UI: extend `VDF.GUI` or start `VBR.Gui`.
