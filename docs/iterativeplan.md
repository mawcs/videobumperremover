# Iterative Plan Document

This document catalogs planning concepts as we iterate in development. Newest plan goes at the
top, under its own second-level heading; older plans stay below under theirs, kept for historical
reference rather than deleted or overwritten.

## Mixed-density edge/middle fingerprinting — spike plan (2026-07-21)

**Status: implemented and validated (2026-07-21), same day.** Written up per the maintainer's
request after an earlier same-day test (`VisualBumperMatcherOffsetTests`, kept in the repo — see
below) turned out to validate a different claim than the one in question. Built exactly as planned
below, with one deliberate accommodation: the maintainer asked to leave room for pHash as a second
per-position signal "very soon," so `MixedDensitySampler` factors frame-gathering (extract →
full-decode → low-information filter → timestamp) into its own signal-agnostic internal step
(`GatherFrames`, returning plain timestamped RGB24 frames) separate from embedding (`Sample`) — a
future pHash addition consumes the same gathered frames rather than triggering a second decode
pass. No pHash code was written; this is structure only.

**Result — real media, both directions:**

| Test | Corpus | Expectation | Result |
|---|---|---|---|
| `MatchMixedDensity_FindsAnEdgeBumperLongerThanTheBoundary` (positive) | Avatar: The Last Airbender S01, 20 episodes, 47s true-begin intro, profile = 20s dense @ 0.5s / 27s sparse @ 4s | most/all episodes match | ✅ **19/19 other episodes MATCH**, present 21–25/40 usable clip frames, bestCos 96–99% |
| Same clip vs. Doctor Who (2005) S01, 13 episodes (negative control) | unrelated content | zero false positives | ✅ **0/13**, present=0/40 on every file, bestCos 23–49% |

~50-point gap between the true-positive floor (96%) and the false-positive ceiling (49%), zero
false positives — the mechanism works cleanly on the actual scenario: one 47s bumper, genuinely
mixed density on both the reference clip and every candidate, matched via
`VisualBumperMatcher.MatchMixedDensity` with no temporal alignment between the two sides.

**Regression check (constraint a):** `VisualBumperMatcherOffsetTests` was re-run byte-for-byte
identical before and after the `VisualBumperMatcher.Match` refactor (same `present`/`bestCos`/
`rigid`/`win` numbers on every one of the 12 Daredevil episodes) — the existing single-interval
path is provably unaffected.

**Files:** new — `VBR.Core/Fingerprinting/EdgeDensityProfile.cs`, `TimedFrame.cs`,
`MixedDensitySampler.cs`; `VBR.Tests/Matching/VisualBumperMatcherMixedDensityTests.cs`. Modified —
`VBR.Core/Matching/VisualBumperMatcher.cs` (`Match` refactored onto a shared `ComparePresence`
helper via a new `ToTimedFrames` conversion; added `MatchMixedDensity`). No changes to
`DenseFrameSampler`, `FrameQuality`, `ClipExtractor`, or any VDF.Core file.

**Related:** [`decisions/0006-edge-focused-fingerprinting.md`](decisions/0006-edge-focused-fingerprinting.md)
(decisions 1/4/5 — the density profile and the non-uniform `(timestamp, value)` data model this
spike needs a minimal slice of), [`PROGRESS.md`](PROGRESS.md) ("Edge-focused scan + a cached
fingerprint/embedding index," the still-open item this spike de-risks).

### The problem, precisely

A bumper can touch the true edge of a file and still be longer than `edge-boundary` (the
ultra-dense sampling window) — e.g. a 47s title sequence at the true beginning against a 20s
boundary. That single bumper's fingerprint then needs **two densities inside one record**: dense
samples from the true edge out to `edge-boundary`, sparse samples the rest of the way. Today's
sampling (`VBR.Core.Fingerprinting.DenseFrameSampler`) and frame record
(`VDF.Core.AI.DenseEmbeddingStore.DenseRecord`) both assume **one** interval for an entire region
and infer each frame's time as `index × interval` — a formula that breaks the moment two
intervals coexist. This was misdiagnosed once already this session as an *offset/alignment*
problem (does a clip extracted away from the true edge still match?) — already tested and
confirmed fine, but a different question from this one, which is about **density mixing within a
single edge-anchored fingerprint**, not extraction offset.

**Test corpus:** *Avatar: The Last Airbender* Season 1 (`test_materials/Avatar/Season 01`, 20 real
episodes) — every episode opens with the same ~47s title sequence at the true beginning, long
enough to genuinely exceed a 20s `edge-boundary`. Negative control: an existing unrelated corpus
already validated as sharing no content with Avatar (Doctor Who or Daredevil).

### Constraints (maintainer, 2026-07-21)

Modifying `VBR.Core` is in bounds, provided the change: (a) doesn't lose existing functionality,
(b) improves our existing matching, (c) isn't a huge new stack of code, (d) doesn't fundamentally
change the existing strategy/architecture. Each step below is sized against these explicitly.

### Step 1 — two small new types, additive only (`VBR.Core/Fingerprinting/`, new files)

`EdgeDensityProfile.cs` — the three knobs the maintainer asked to expose, bundled as one value so
they thread through signatures together instead of as three loose primitives:

```csharp
public readonly record struct EdgeDensityProfile(
    TimeSpan EdgeBoundary, TimeSpan DenseInterval, TimeSpan SparseInterval);
```

`TimedFrame.cs` — an explicit timestamp per embedded frame, replacing the implicit
`index × interval` that breaks under mixed density. This is the minimal slice of ADR 0006
decision 4/5's non-uniform `(timestamp, value)` model needed to represent mixed-density data at
all — not the full persistent sidecar record, just the in-memory shape:

```csharp
public readonly record struct TimedFrame(double TimestampSeconds, byte[] Embedding);
```

Neither type touches `DenseEmbeddingStore`/`DenseRecord` — those stay exactly as they are, still
used by VDF's own whole-file AI-partial pass. This is new, additive surface area, not a
replacement (constraint a).

### Step 2 — the sampler (`VBR.Core/Fingerprinting/MixedDensitySampler.cs`, new)

A small class (owns an `OnnxEmbedder`, same lifetime pattern as `VisualBumperMatcher`) with one
method:

```csharp
public IReadOnlyList<TimedFrame> Sample(
    string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
    CancellationToken ct = default);
```

Algorithm — entirely composed from existing primitives, nothing new at the ffmpeg/decode level:

1. Extract the **whole** requested region once: `ClipExtractor.ExtractToTemp(sourcePath,
   ClipRegion.For(region, totalLength))` — identical to what `VisualBumperMatcher` already does.
2. Within that temp file, carve the dense and sparse sub-regions as two further temp files via
   `ClipRegion.At(...)` (already public, already used for the offset spike) — for `begin`: dense
   = `At(0, edgeBoundary)`, sparse = `At(edgeBoundary, totalLength - edgeBoundary)`; for `end`,
   mirrored: dense = `At(totalLength - edgeBoundary, edgeBoundary)`, sparse =
   `At(0, totalLength - edgeBoundary)`.
3. Run `DenseFrameSampler.SampleFrames` on each sub-region at its own interval — unchanged, reused
   as-is.
4. Run `FrameQuality.SelectUsable` on each — unchanged, reused as-is, applied consistently to both
   densities.
5. Embed the usable frames via `OnnxEmbedder.EmbedBatchQuantized`, batched exactly like
   `VisualBumperMatcher.Embed` already does (same `OnnxEmbedder.MaxBatch` chunking loop).
6. Assign each surviving frame its real timestamp (`zoneStart + index × zoneInterval`) and emit a
   `TimedFrame`. Unlike `DenseRecord`, filtered-out frames are simply **omitted** rather than kept
   as empty placeholder slots — the index↔time trick existed only to preserve an implicit time
   formula that explicit timestamps no longer need. One small simplification, not a new concept.

No changes to `ClipExtractor`, `DenseFrameSampler`, or `FrameQuality` — all three are reused
verbatim (constraint c: this is composition, not a new stack).

### Step 3 — teach `VisualBumperMatcher` to compare `TimedFrame` lists (modify existing file)

`VisualBumperMatcher.Match` ([`VisualBumperMatcher.cs:147-185`](../VBR.Core/Matching/VisualBumperMatcher.cs#L147-185))
currently inlines the presence loop directly over two `DenseEmbeddingStore.DenseRecord`s. Extract
that loop (lines 161–176) into a small private static helper over the general shape both callers
actually need:

```csharp
static (bool present, float best, double? bestTime, int hits) ComparePresence(
    IReadOnlyList<TimedFrame> clip, IReadOnlyList<TimedFrame> candidate, float presenceThreshold);
```

Then:

- The **existing** `Match(string referenceClipPath, string candidatePath, ClipRegion, ...)` path
  converts its `DenseRecord` frames to `TimedFrame`s inline (`frame[i]` + `i × interval`, skipping
  empty slots — a few lines) and calls the shared helper. This must produce **byte-identical
  results** to today's behavior — same thresholds, same math, purely reorganized — and is the
  concrete check for constraint (a).
- A **new** public method, `MatchMixedDensity(IReadOnlyList<TimedFrame> clip, IReadOnlyList<TimedFrame> candidate)`,
  calls the same shared helper directly with sampler-supplied frames. This is the literal answer
  to "can `VisualBumperMatcher` handle this data" — yes, through this entry point, with zero
  duplicated matching logic (constraint b: the matcher genuinely gains a capability, not a
  bolted-on parallel path).

**Explicitly not attempted here:** adapting the "rigid" ≥4-consistent-offset corroboration matcher
(`ScanEngine.TryMatchDenseFrames`) to mixed-density data. It's corroboration-only, never gates a
decision, and its `DenseRecord` input assumes a single interval — forcing it to accept
`TimedFrame`s means touching upstream `VDF.Core` for no matching-correctness benefit. The
mixed-density path reports presence-only results; the rigid number is simply absent for it.

### Step 4 — the configurable test (`VBR.Tests/Matching/VisualBumperMatcherMixedDensityTests.cs`, new)

A new file, not a modification of `VisualBumperMatcherOffsetTests.cs` — that test stays as
committed, under its current name, and may get reused/tweaked for interstitial matching later per
the maintainer's own call. Same env-var-gated, skip-cleanly convention as the existing real-media
tests. Parameters, matching the maintainer's own worked example exactly:

- `BUMPER_CLIP_EPISODE`, `BUMPER_EPISODES_DIR`, `BUMPER_REGION` — reused as-is from the existing
  tests.
- `BUMPER_MIXED_TOTAL_LENGTH_SECONDS` (e.g. `47`) — the full known bumper length.
- `BUMPER_MIXED_EDGE_BOUNDARY_SECONDS` (e.g. `20`) — the ultra-dense zone length.
- `BUMPER_MIXED_DENSE_INTERVAL_SECONDS` (e.g. `0.5`) — sampling interval inside the boundary.
- `BUMPER_MIXED_SPARSE_INTERVAL_SECONDS` (e.g. `4`) — sampling interval beyond it.
- Optional `BUMPER_MIXED_NEGATIVE_DIR` — an unrelated-content folder; when set, asserts **zero**
  matches, alongside the positive assertion (at least one match) against `BUMPER_EPISODES_DIR`.

Both the clip and every candidate get sampled through the **same** `MixedDensitySampler` call with
the **same** `EdgeDensityProfile` before `VisualBumperMatcher.MatchMixedDensity` compares them —
proving the actual scenario: one bumper, two densities, matched correctly end to end.

### Explicitly out of scope for this spike

- **Persistence.** No serialization of `TimedFrame` records to disk. This spike only needs
  in-memory data for one test run; the real sidecar format is separate, already tracked (ADR 0006
  decision 5, `PROGRESS.md`).
- **The library-scan CLI.** This proves the sampling+matching primitive works. Wiring it into a
  `vbr scan`-style command that walks a tree and builds a persistent index is the next, separate
  task — this spike is a prerequisite for it, not a first draft of it.
- **Middle-region/interstitial matching.** Likely served by the same primitives eventually (why
  `VisualBumperMatcherOffsetTests` was kept), but a distinct effort from this one.

### Verification plan

1. Build clean.
2. Re-run `VisualBumperMatcherOffsetTests` and confirm identical output to before the Step 3
   refactor — the concrete proof that existing functionality survived (constraint a).
3. Run the new mixed-density test live against Avatar with the maintainer's own numbers
   (47 / 20 / 0.5 / 4) — confirm a positive match across the 20 episodes.
4. Run again with a negative corpus set — confirm zero false matches.
5. Only then treat the mixed-density mechanism as validated and ready to inform the real
   `edge-boundary` default and the library-scan design.

### Open questions, deliberately deferred until after the spike

- Production defaults for `edge-boundary`/dense/sparse intervals — this spike's numbers are for
  exercising the mechanism, not necessarily what ships.
- Whether `MixedDensitySampler` becomes the *only* sampling path for `VisualBumperMatcher`
  (retiring the single-interval path in `Embed`) or coexists as a special case for regions longer
  than `edge-boundary`.

---

## Fixing the visual matcher's black-frame false positives

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

### The reported problems

1. `match` should traverse subtrees by default; a switch to *not* traverse would be prudent.
2. A 5s Netflix bumper from the **begin** of Daredevil scored 99% — but validating against Doctor
   Who (which does not contain that bumper) produced **matches**. Something is badly broken.
3. Matching that same 5s begin clip against Avatar gave `bestCos` in the high-80s across the board,
   plus **four** matches for a Netflix bumper that does not appear in those videos.
4. We need a switch to write match results to a file.

Symptoms 2 and 3 are one bug. Symptoms 1 and 4 are missing CLI features.

---

### Root cause: the matcher is comparing black frames to black frames

The CLI faithfully reproduces the validated probe — this is **not** a mis-port. The problem is two
latent defects in the shared decode/sample pipeline that the begin-region / Netflix-ident scenario
exposes, plus the fact that the spec's "do not match on black" rule was never actually implemented.

The extraction + decode chain was replicated on the real test files
([`VisualBumperMatcher.cs:121`](../VBR.Core/Matching/VisualBumperMatcher.cs#L121) →
`GetDenseAiFrames` → [`FfmpegEngine.cs:1009`](../VDF.Core/FFTools/FfmpegEngine.cs#L1009)) and the
frames the matcher actually sees were dumped to PNG and inspected.

#### Finding 1 — "14 frames" is really 3 distinct images, only one of them distinctive

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

#### Finding 2 — the search windows are black too

Doctor Who's mp4s have keyframes every ~6s (0, 6, 12, 18, 24), and the keyframes at 0s and 6s are
pure black (verified visually). So each candidate's search window is also mostly duplicated black
frames.

#### Finding 3 — there is no black-frame filter anywhere

The spec's "skip empty/black frames" step ([`matcher-spec.md`](design/matcher-spec.md), §2 step 3)
is implemented in both the probe and the port as "skip zero-length buffers" — but
`GetDenseAiFrames` never emits a zero-length frame (it slices fixed-size chunks or fails the whole
call). The guard is **dead code**; nothing has ever filtered black frames. The end-region
validation passed anyway because the Daredevil end-stack clip is a long run of distinctive bright
cards landing on scene-cut keyframes — the pathological all-dark-keyframes case simply never came
up until begin-region testing.

#### Why this explains every symptom

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

### Plan

#### A. Correctness fixes (both needed — either alone still fails) — IMPLEMENTED (2026-07-18)

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

#### B. CLI features requested — IMPLEMENTED (2026-07-18)

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

#### C. Re-validation matrix — PASSED (2026-07-18, all five runs; `--detection-mode visual`, 0.2s interval)

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

#### D. Documentation debt this uncovered — DONE (2026-07-18)

- **`matcher-spec.md`:** "skip empty/black frames" must be specified as a real luma filter, and the
  keyframe-only-decode discovery recorded (it also colors ADR 0006's "dense sampling" framing —
  density past the keyframe cadence was previously an illusion).
- **`research/vdf-evaluation.md` / `PROGRESS.md`:** log this failure mode and the corrected
  begin-region results; annotate the earlier "~65-pt gap" claim as **end-region-specific**.

---

### Ordering & risk

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
