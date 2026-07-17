# ADR 0006: Edge-focused, variable-density fingerprinting + region tagging

- **Status:** accepted (core approach; specific interval/window values are tuning, TBD)
- **Date:** 2026-07-16
- **Related:** [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md) (validation),
  [`../design/bumper-catalog.md`](../design/bumper-catalog.md),
  [`0004-bumper-catalog.md`](0004-bumper-catalog.md), [`ROADMAP.md`](../ROADMAP.md)

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
   **middle**. One file → one *non-uniform* fingerprint timeline. Applies to **both** signals:
   audio fingerprint blocks and visual keyframe embeddings.
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

## Open questions / tuning

- **Edge-window size N** — small enough to stay cheap, large enough to cover the junk. Note
  title-sequences after a cold-open sit deeper in, but those are *not* targets; the junk idents
  we target sit at the true edge, so a modest N (e.g. tens of seconds) likely suffices. Tune.
- **Dense interval** (~0.2–0.5s) and **sparse/middle interval** (VDF ~5–15s), possibly different
  for audio vs. visual.
- Whether the middle is sampled at all in the fast path, or only in the interstitial pass.
- How region tags interact with removal (edge bumpers cut to BOF/EOF; middle = split/concat).
