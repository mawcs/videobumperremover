// (this is a planning doc — no license header; headers go on source files only)

# Iterative plan — fixing the visual matcher's black-frame false positives

**Status (final, 2026-07-18):** **all sections implemented and validated.** §B (CLI features)
and §D (doc updates) landed first; §A (correctness fixes) followed on maintainer approval, and
the full §C re-validation matrix passed — perfect separation (begin: TP 12/12 @ 99–100% with
present=18/18 vs FP 0/33 files with bestCos ≤56%; end regression: TP 12/12 @ 99–100% vs FP 0/20
@ ≤71%; see §C below for the recorded numbers). This doc captures the diagnosis of the bad-match
results reported during begin-region (Netflix ident) testing, the fix plan, and the outcome.

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

### Finding 1 — "14 frames" is really 3 distinct images, only one of them distinctive

*(Corrected 2026-07-18, second pass — the first write-up of this finding said "13 of 14 frames
are pure black," an interpolation from viewing only 3 dump frames. Ground-truth verification
below fixed the composition; the mechanism is unchanged.)*

`GetDenseAiFrames` decodes **keyframes only** (`-skip_frame nokey`, inherited from VDF's whole-file
dedup scan), then the `fps=1/0.2` filter fills the 0.2s grid by **duplicating** each keyframe.
Daredevil S01E01's first 5s has exactly three keyframes (full ffprobe frame map verified —
everything between them is P/B):

- I-frame at 0.021s — black (the file genuinely opens on black)
- I-frame at 1.022s — **blank white** (the ident background flashing on — the scene cut that
  earned the I-frame)
- I-frame at 2.607s — the red NETFLIX card

The fps grid turns that into **6 copies of black + 7 copies of blank white + 1 red card** —
14 frames total, exactly the `present=…/14` denominator in the reported output. The letters
animation (~1.4–2.5s, the most distinctive content in the ident) sits **entirely mid-GOP and is
never sampled at all**; the card's ~2.2s on-screen hold is represented **once**.

**Ground-truth verification (maintainer challenge, same day):** the maintainer exported a
per-frame 0.2s reference grid from DaVinci Resolve
(`test_materials/dd_netflix_bumper_davinci_export/24Frames/`) that looked nothing like the
pipeline dump — and follow-up checks confirmed why, while validating the mechanism:

- A **full-decode** `fps=1/0.2` dump of the same 5s yields **25 frames matching the DaVinci
  reference frame-for-frame** (black → blank white → letters flying in → 3D shadow → red card).
  ffmpeg has no problem producing the right frames — the defect is the pipeline's *frame
  selection*, not decode.
- The `-skip_frame nokey` decode itself is **pixel-correct** (the 3 keyframes decode identical
  to full decode — no corruption). Every sampled frame is a *genuine* frame from its timestamp;
  the pathology is which timestamps get represented and how many times.
- The maintainer's separate keyframe dump (`.../Keyframes/`, more visual variety) is from
  `Bumper.mkv` — a DaVinci **re-encode** with a fresh GOP (I-frames at exactly
  0/1.001/2.002/3.003/4.004/5.005) — not the original's keyframe structure, which resolves that
  apparent contradiction.
- The `present=6/14` hits are **precisely the six black duplicates** (the blank-white frames sat
  in the high-80s against these libraries, just under the 0.90 threshold — part of the
  suspicious bestCos floor).
- **Not begin-specific:** the same episode's *end* region keyframes every ~1.4–3s (scene-cut
  driven, bright distinctive cards) — which is exactly why the end-region validation passed.
  Severity is **keyframe-cadence + content dependent, on both the clip and candidate sides**;
  the begin edge just happened to expose it first.

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

### A. Correctness fixes (both needed — either alone still fails) — IMPLEMENTED (2026-07-18)

1. **Low-information frame filter (implements the spec's existing rule).** ✅ Implemented as
   `VBR.Core.Fingerprinting.FrameQuality`: reuses VDF's own AI-partial-scan guards
   (`ScanEngine.SelectUsableDenseFrames` — the ≥80%-dark-pixels rejection and the
   byte-identical-duplicate drop, which the probe/port had bypassed all along) and adds the
   near-uniform rejection those guards lack: mean absolute horizontal luma delta ≥ **1.0**
   (`FrameQuality.MinDetail`). Calibrated on real frames (0.2s full-decode grids of the DD
   ident, DW/Avatar begin windows, DD end credits): blank-white ident background 0.55–0.68 and
   fades ≤0.95, versus letter animation 1.33–1.97, dark-but-real scene content 1.46+, bright
   cards ≥3 — 1.0 sits mid-gap. Applied to **both** sides in `VisualBumperMatcher.Embed`; an
   all-filtered clip **fails loudly** via the new `PrepareClip` (which also caches the clip's
   embeddings per run — the port had been re-embedding the clip for every candidate). Upstream
   `GetDenseAiFrames` untouched.

2. **Decode all frames in edge windows, not just keyframes.** ✅ Implemented as
   `VBR.Core.Fingerprinting.DenseFrameSampler`: the identical ffmpeg recipe minus
   `-skip_frame nokey` (the exact full-decode chain verified frame-for-frame against the
   maintainer's DaVinci reference export). The 5s test clip now yields 26 sampled / 18 usable
   distinct frames where the old path produced 14 fps-duplicates of 3 keyframes with a single
   distinctive image among them.

3. **Defer threshold tuning until after re-validation.** ✅ Resolved — no tuning proved
   necessary: the §C matrix passed with the spec's original presence rule (≥1 distinctive frame
   at ≥0.90 cosine) and every default untouched.

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

### C. Re-validation matrix — PASSED (2026-07-18, all five runs; `--detection-mode visual`, 0.2s interval)

| Test | Expectation | Result |
|---|---|---|
| Daredevil begin clip (5s) vs Daredevil S01 (begin) | all episodes match, on the *card* frames (not black) | ✅ **12/12 MATCH, present=18/18, bestCos 99–100%, rigid 97–98%@0s** |
| Same clip vs Doctor Who S01 (begin) | **0** false positives | ✅ **0/13, bestCos 19–53%** (was 9 false MATCHes @ 87–97%) |
| Same clip vs Avatar S01 (begin) | **0** false positives | ✅ **0/20, bestCos 52–56%** (was 4 false MATCHes) |
| End-stack regression: last-10s clip vs Daredevil S01 (end) | still 12/12 | ✅ **12/12 MATCH, present=32–33/33, bestCos 99–100%, rigid 97%@16–20s** |
| End-stack regression: same clip vs Avatar S01 (end) | still 0 FP | ✅ **0/20, bestCos 62–71%** |

Re-recorded baselines and notes:

- **Begin-region separation: TP 99–100% (presence 18/18) vs FP ≤56% (presence 0/18)** — a
  ~44-point gap with full evidence counts, replacing the broken state's inverted picture
  (false MATCHes at 87–97% off six duplicated black frames).
- **End-region FP floor moved ≤33% → ≤71%** and is expected to: the old ≤33% was distinctive
  bright keyframe-cards compared against Avatar's few sampled keyframes; the honest comparison
  is 33 usable clip frames against ~150 usable real content frames per candidate. The gap to
  the 0.90 presence threshold (and to TP presence counts: 32–33/33 vs 0/33) remains wide.
- Presence denominators are now real evidence: every usable clip frame is a distinct image
  (duplicates dropped), so `present=18/18` means eighteen different pictures found, not one
  black frame counted six times.
- Doctor Who/Avatar library file counts differ from the first (broken) runs because the stray
  `intro*.mkv` clips are no longer in those folders.

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
and `PROGRESS.md`).

**Final note (2026-07-18, same day):** on maintainer approval, §A landed
(`DenseFrameSampler` + `FrameQuality` + clip-embed caching/`PrepareClip`, with 5 unit tests) and
the full §C matrix ran clean — see the recorded results above. The flagged risk was handled as
planned: the end-stack regression re-ran and re-recorded (12/12 @ 99–100%; FP floor ≤71%,
explained above). The defect this doc diagnoses is **fixed and validated**; remaining follow-ups
live in `docs/PROGRESS.md` (cached index, catalog, removal engine — and note the index must be
built on this corrected sampling layer).
