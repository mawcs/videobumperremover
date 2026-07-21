# ADR 0006: Edge-focused, variable-density fingerprinting + region tagging

- **Status:** accepted (core approach; specific interval/window values are tuning, TBD)
- **Date:** 2026-07-16
- **Related:** [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md) (validation),
  [`../design/bumper-catalog.md`](../design/bumper-catalog.md),
  [`0004-bumper-catalog.md`](0004-bumper-catalog.md), [`ROADMAP.md`](../ROADMAP.md)

> **Amendment (2026-07-21) — audio corrected; pHash added as a third signal.** Decision 1 below,
> as originally written, claimed the dense-edge/sparse-middle density profile "applies to both
> signals: audio fingerprint blocks and visual keyframe embeddings." That doesn't match what was
> actually built: `VBR.Core.Matching.AudioBumperMatcher` fingerprints the *whole file* once at
> Chromaprint's native ~1s-block resolution (cheap — decode dominates, not fingerprinting; see
> `vdf-evaluation.md`) and finds alignment via a sliding-window search
> (`ScanEngine.SlidingWindowCompare`) over a sliced region, not a discrete sampled-position array.
> There's no meaningful "denser" for a ~1s Chromaprint block and no accuracy/cost reason to
> special-case audio's edges — the existing whole-file fingerprint already covers everything;
> edge-focus narrows which slice gets *searched*, not how densely it's *extracted*. **Audio is
> exempt from the density profile** — decision 1 is corrected below.
>
> Separately, for the library-scan design (as opposed to the ad-hoc `vbr match`/`remove` CLI this
> ADR was originally validated against): the scan captures **three** per-position signals, not
> one — perceptual hash (pHash) alongside visual (DINOv2) embeddings, both at the same dense/sparse
> positions. pHash wasn't part of the original spike or `matcher-spec.md`'s matcher list; it's
> added because it's a free byproduct of the same frame sampled for visual matching (see decision
> 5, added below). Audio stays whole-file, not position-sampled.

## Context

- The junk bumpers we target (studio/network idents, "coming to Blu-ray" promos) cluster at the
  **very start and end** of files, and are often **short** (~3–15s).
- Short bumpers need **dense** frame/audio sampling to be detected at all — VDF's default 5–15s
  spacing yields too few samples (validated: coarse sampling failed; dense edge sampling +
  presence matcher hit 98–99%).
- Dense-sampling the **whole** file at library scale is far too expensive (decode + embed cost).
- **Positional constraint also improves accuracy:** limiting the offset search to the edges
  shrinks the search space, which **lowers the false-positive floor** (validated: audio FP floor
  dropped from ~72–86% to ~50–68% when constrained to a head window).
- Mid-video **interstitials** exist but are rarer and typically longer; they don't justify dense
  sampling of every file's middle.

## Decision

1. **Variable-density per-file fingerprinting.** Sample **densely** (e.g. ~0.2–0.5s) within the
   first/last **N seconds** (the *edge windows*) and **sparsely** (VDF's ~5–15s) across the
   **middle**. One file → one *non-uniform* fingerprint timeline. Applies to the **position-sampled
   signals** — visual keyframe embeddings and pHash, both sampled at the same dense/sparse
   positions. **Audio is exempt** — see the 2026-07-21 amendment above.
2. **Region tagging.** Each cataloged bumper is tagged with the region(s) it occurs in —
   **begin / end / middle** (multi-region allowed; e.g. Netflix Logo at beginning and end).
   Matching a video against a bumper compares only against that **region's** fingerprints —
   cheaper *and* lower false-positive floor (fewer candidate offsets).
3. **Two-tier matching.** A fast **edge path** (dense edges — the common case, runs on every
   file) and a separate, heavier **mid-video interstitial path** (denser middle scan, run on
   demand / less often). Same fingerprint primitives, different sampling profiles.
4. **Data model.** Store fingerprints as **`(timestamp, value)` pairs** (non-uniform), not a
   single `IntervalSeconds`, plus **region labels**. This supersedes VDF's uniform-interval
   `DenseRecord` for *our* index (VDF's stays as-is; ours is a new structure).
5. **Storage mapping (added 2026-07-21).** Three signals, three storage locations — not one:
   - **pHash + its source grayscale thumbnail** → `FileEntry.grayBytes` / `FileEntry.PHashes`
     (VDF's existing fields — already keyed by absolute timestamp, i.e. already `(timestamp,
     value)` pairs; no new structure needed). pHash is a free byproduct of the same sampling call
     that produces the grayscale thumbnail (`FfmpegEngine.GetGrayBytesFromVideo`), so sampling at
     our dense/sparse positions for pHash costs nothing beyond what visual sampling already pays.
   - **Audio fingerprint** → `FileEntry.AudioFingerprint` (VDF's existing field, whole-file,
     unchanged — per the amendment above, no density profile applies).
   - **Visual DINOv2 embeddings** → a new sidecar (not `grayBytes` — wrong shape: an embedding is
     a ~384-float vector derived from a 224×224 RGB input, not a 32×32 grayscale thumbnail),
     modeled on `VDF.Core.AI.DenseEmbeddingStore` but with decision 4's non-uniform `(timestamp,
     value)` + region tag replacing its single `IntervalSeconds`.
   Note VDF's own position generator (`ScanEngine.BuildPositionList`, N evenly-spaced fractions of
   duration) cannot produce this profile — it only does uniform spacing. A dense/sparse
   position-list generator is net-new work, not something inherited from VDF.

## Consequences

Positive: scanning cost concentrates where bumpers actually are; short edge bumpers become
detectable; the positional constraint raises accuracy (lower FP floor); interstitials are still
handled, just on the heavier tier.

Negative / cost: a non-uniform fingerprint data model (timestamps + region labels); edge-window
size and dense/sparse intervals must be chosen and tuned; two sampling/matching code paths to
maintain.

## Validation

From [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md): positional (edge) windows
reopened the gap for short *audio* clips (FP floor 72–86% → 50–68%); dense-sampled visual edges +
presence matcher detected a real silent bumper stack at **98–99% vs. ≤33%** unrelated (~65-pt
gap, zero false positives).

**Annotation (2026-07-18):** that ~65-pt gap is **end-region-specific**. The first begin-region
test (dark Netflix ident) produced black-frame false positives — the shared decode path has no
low-information filtering and decodes keyframes only. See the 2026-07-18 entry in
[`../research/vdf-evaluation.md`](../research/vdf-evaluation.md) and the fix plan in
[`../iterativeplan.md`](../iterativeplan.md).

## Open questions / tuning

- **Edge-window size N** — small enough to stay cheap, large enough to cover the junk. Note
  title-sequences after a cold-open sit deeper in, but those are *not* targets; the junk idents
  we target sit at the true edge, so a modest N (e.g. tens of seconds) likely suffices. Tune.
- **Dense interval — partially resolved (2026-07-17):** validated that a 4s clip **failed to
  match at a 0.5s sample interval and succeeded at 0.2s**. Short clips (the common case this
  project targets) need sampling as dense as ~0.2s (5 samples/sec); this is a hard requirement
  on the matcher's capability (no artificial floor on how small the interval can go), not just a
  tuning nice-to-have. Still open: the exact interval-vs-clip-length relationship (is 0.2s always
  necessary, or only below some clip-length threshold?) and whether audio needs the same density
  (audio fingerprint blocks are ~1s and haven't shown the same failure mode). See
  [`matcher-spec.md`](../design/matcher-spec.md) `--sample-interval` (default 1.0s, override down
  as needed) and [`vdf-evaluation.md`](../research/vdf-evaluation.md) for the source finding.
  - **Reinterpreted (2026-07-18):** the shared decode path (`GetDenseAiFrames`) decodes
    **keyframes only** and *duplicates* them onto the fps grid, so a smaller interval never added
    distinct frames past the keyframe cadence — 0.2s "succeeding" where 0.5s failed was about
    rescuing sparse keyframes from fps rounding, not true density. **Resolved same day:** VBR now
    full-decodes the (short) extracted edge windows with low-information filtering
    (`VBR.Core.Fingerprinting.DenseFrameSampler` and `FrameQuality`; implemented and re-validated
    clean — see [`../iterativeplan.md`](../iterativeplan.md) §A/§C), so the interval genuinely
    controls density. The no-artificial-floor requirement on the interval stands; the
    interval-vs-clip-length question above can now be studied on honest data.
- **Sparse/middle interval** (VDF ~5–15s) — unchanged, still TBD; not exercised by the current
  edge-only build (see matcher-spec.md — the middle/interstitial path is future work).
- Whether the middle is sampled at all in the fast path, or only in the interstitial pass.
- How region tags interact with removal (edge bumpers cut to BOF/EOF; middle = split/concat).
