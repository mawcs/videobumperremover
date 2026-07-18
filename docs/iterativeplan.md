// (this is a planning doc — no license header; headers go on source files only)

# Iterative plan — fixing the visual matcher's black-frame false positives

**Status (updated 2026-07-18):** diagnosis done; **§B (CLI features) and §D (doc updates) are
implemented**; **§A (correctness fixes) and §C (re-validation) are pending a maintainer decision
and remain unimplemented.** This doc captures the diagnosis of the bad-match results reported
during begin-region (Netflix ident) testing and the plan to fix them.

**Related:** [`design/matcher-spec.md`](design/matcher-spec.md) (the "definition of done" this
restores), [`decisions/0006-edge-focused-fingerprinting.md`](decisions/0006-edge-focused-fingerprinting.md)
(sampling), [`research/vdf-evaluation.md`](research/vdf-evaluation.md) (validation log to update).

---

## The reported problems

1. `match` should traverse subtrees by default; a switch to *not* traverse would be prudent.
2. A 5s Netflix bumper from the **begin** of Daredevil scored 99% — but validating against Doctor
   Who (which does not contain that bumper) produced **matches**. Something is badly broken.
3. Matching that same 5s begin clip against Avatar gave `bestCos` in the high-80s across the board,
   plus **four** matches for a Netflix bumper that does not appear in those videos.
4. We need a switch to write match results to a file.

Symptoms 2 and 3 are one bug. Symptoms 1 and 4 are missing CLI features.

---

## Root cause: the matcher is comparing black frames to black frames

The CLI faithfully reproduces the validated probe — this is **not** a mis-port. The problem is two
latent defects in the shared decode/sample pipeline that the begin-region / Netflix-ident scenario
exposes, plus the fact that the spec's "do not match on black" rule was never actually implemented.

The extraction + decode chain was replicated on the real test files
([`VisualBumperMatcher.cs:121`](../VBR.Core/Matching/VisualBumperMatcher.cs#L121) →
`GetDenseAiFrames` → [`FfmpegEngine.cs:1009`](../VDF.Core/FFTools/FfmpegEngine.cs#L1009)) and the
frames the matcher actually sees were dumped to PNG and inspected.

### Finding 1 — "14 frames" is really 3 distinct images, 13 of which are pure black

`GetDenseAiFrames` decodes **keyframes only** (`-skip_frame nokey`, inherited from VDF's whole-file
dedup scan), then the `fps=1/0.2` filter fills the 0.2s grid by **duplicating** each keyframe.
Daredevil S01E01's first 5s has exactly three keyframes:

- ~0.02s — black
- ~1.02s — black
- ~2.61s — the NETFLIX card

The fps grid turns that into ~5 copies of black, ~8 copies of black, and **one** copy of the
NETFLIX card — 14 frames total, which is exactly the `present=…/14` denominator in the reported
output. Worse: fps ticks stop at the last keyframe timestamp, so the remaining ~2.4s of the clip
(where the card is actually on screen) is **never represented at all**.

### Finding 2 — the search windows are black too

Doctor Who's mp4s have keyframes every ~6s (0, 6, 12, 18, 24), and the keyframes at 0s and 6s are
pure black (verified visually). So each candidate's search window is also mostly duplicated black
frames.

### Finding 3 — there is no black-frame filter anywhere

The spec's "skip empty/black frames" step ([`matcher-spec.md`](design/matcher-spec.md), §2 step 3)
is implemented in both the probe and the port as "skip zero-length buffers" — but
`GetDenseAiFrames` never emits a zero-length frame (it slices fixed-size chunks or fails the whole
call). The guard is **dead code**; nothing has ever filtered black frames. The end-region
validation passed anyway because the Daredevil end-stack clip is a long run of distinctive bright
cards landing on scene-cut keyframes — the pathological all-dark-keyframes case simply never came
up until begin-region testing.

### Why this explains every symptom

- **DINOv2 embeddings of near-black frames cluster tightly** — cosine 0.87–0.97 against other
  near-black frames (compression noise keeps them just off 1.0). Episodes where the noise happened
  to land ≥0.90 became "MATCH"; the rest produced the suspicious 87–89% `bestCos` floor. That is
  the Avatar high-80s and the four Avatar false matches — sampling luck, nothing more.
- **`present=6/14` almost everywhere** is six duplicated black frames crossing the threshold — one
  degenerate image masquerading as six pieces of corroborating evidence.
- **Rigid corroboration is fooled by the same duplicates**: ≥4 "temporally-consistent" hits are
  trivially satisfied when both sides repeat identical frames (e.g. rigid@10s in Doctor Who = the
  black keyframe at 6.0s smeared across ticks 6–11.8s).
- **Audio behaved correctly** throughout (45–73%, all below the 0.80 threshold) — no action needed
  on the audio path.

**Caveat worth internalizing:** the Daredevil-vs-Daredevil 99% "success" was **inflated by the same
defect** — some of those hits were black-on-black too. The real end-region validation still holds
(distinctive cards genuinely matched), but the exact numbers are not trustworthy and must be
re-recorded after the fix.

---

## Plan

### A. Correctness fixes (both needed — either alone still fails)

1. **Low-information frame filter (implements the spec's existing rule).**
   In `VisualBumperMatcher.Embed`, the raw 224×224 RGB24 bytes are already in memory before
   embedding — compute mean/variance luma per frame and drop near-black / near-uniform frames on
   **both** the clip and candidate sides. Calibrate thresholds against the dumped real frames. If
   the clip ends up with zero distinctive frames, **fail loudly** ("reference clip is all
   black/low-information — adjust region/length") instead of matching on noise. Do this in
   `VBR.Core`, **not** in VDF's `GetDenseAiFrames`, so the upstream dedup scan path is untouched.

2. **Decode all frames in edge windows, not just keyframes.**
   Keyframe-only decode is a whole-file-scan optimization that is wrong for ≤30s extracts —
   fully decoding 25s of video is cheap. Give VBR its own dense sampler (same ffmpeg recipe **minus**
   `-skip_frame nokey`) so `--sample-interval 0.2s` yields ~25 *distinct* frames, including the ~12
   real NETFLIX-card frames the current pipeline never sees. This makes `--sample-interval` honest
   (today, a denser interval only manufactures duplicates — distinct content is capped at keyframe
   count) and fixes the "ticks stop at the last keyframe" truncation. **Note:** this quietly
   invalidates the assumption behind the documented "4s clip needed 0.2s interval" result — that was
   about rescuing keyframes from fps rounding, not about density.

3. **Defer threshold tuning until after re-validation.**
   With filtering + real frames, the spec's presence-≥1 decision may well be fine as-is. If false
   positives persist, the next knobs, in order: require ≥2 *distinct* hits; report the margin
   between `bestCos` and the window's median cosine. Do **not** touch the 0.90 presence threshold
   yet — reproduce clean numbers first, then tune (per the spec's own anti-pattern).

### B. CLI features requested — IMPLEMENTED (2026-07-18)

4. **Recursive library traversal by default.**
   [`MatchCommand.cs:198`](../VBR.CLI/Commands/MatchCommand.cs#L198) currently enumerates a single
   folder. Switch to `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }`,
   add a `--no-recurse` switch, update the `--library` help text (it currently says
   "non-recursive"), and print **library-relative paths** so same-named files in different
   subfolders remain distinguishable.

5. **`--output <file>`.**
   Write the same per-file lines + summary to a file. The probe already did this (its
   `visual-tail-results-*.txt`); the feature was lost during productionization. Restructure each row
   into a small record while doing this so a later `--output-format json` follows cheaply
   (`VDF.CLI` already has a JSON-output precedent).

6. **Optional but recommended: `--dump-frames <dir>` diagnostic.**
   Write the sampled clip/window frames as images. This diagnosis required rebuilding the pipeline
   by hand; this switch makes the next "why did this match?" a ten-second glance.

### C. Re-validation matrix (definition of done for the fix)

| Test | Expectation |
|---|---|
| Daredevil begin clip vs Daredevil S01 (begin) | all episodes match, on the *card* frames (not black) |
| Same clip vs Doctor Who S01 (begin) | **0** false positives |
| Same clip vs Avatar S01 (begin) | **0** false positives |
| Original end-stack regression (Daredevil 12/12 @ 98–99%, Avatar 0/21 ≤33%) | still clean — numbers may legitimately shift with full decode; **re-record them** |

### D. Documentation debt this uncovered — DONE (2026-07-18)

- **`matcher-spec.md`:** "skip empty/black frames" must be specified as a real luma filter, and the
  keyframe-only-decode discovery recorded (it also colors ADR 0006's "dense sampling" framing —
  density past the keyframe cadence was previously an illusion).
- **`research/vdf-evaluation.md` / `PROGRESS.md`:** log this failure mode and the corrected
  begin-region results; annotate the earlier "~65-pt gap" claim as **end-region-specific**.

---

## Ordering & risk

Do **A1 + A2 together**, then re-validate (**C**), then **B4 / B5** (independent, can land anytime),
then docs (**D**).

**Flagged risk:** full-frame decode changes the validated pipeline, so the end-stack regression run
in C is **not optional** — it is the guard against trading one wrong thing for another.

**Progress note (2026-07-18):** §B and §D landed first (B4 recursive traversal + `--no-recurse` +
relative paths, B5 `--output` with structured `MatchRow` rows, B6 `--dump-frames` via
`VBR.Core.Diagnostics.FrameDump`; docs updated per §D plus `running_and_building.md`, `AGENTS.md`,
and `PROGRESS.md`). **§A and §C remain open pending a maintainer decision.** Until they land,
begin-region results still exhibit the black-frame false positives this doc diagnoses.
