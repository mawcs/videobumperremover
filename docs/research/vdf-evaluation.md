# VDF evaluation findings (hands-on, 2026-07-15)

First hands-on evaluation of the forked VDF against a real subset of the library, to see what
we inherit and where the gaps are (Phase 0 throughput + Phase 2 matching). All numbers are
from the maintainer's GPU desktop reading media over SMB.

## Test setup

- Subset: **TV shows only** — **2,110 files, ~2.5 TB** (movies, clips, BTS, etc. excluded).
- Media on TrueNAS, scanned from the Windows GPU desktop over SMB.

## Measurement 1 — default (visual) scan

- **2,110 files / ~2.5 TB in 17m49s** (~118 files/min).
- Effective ~2.3 GB/s far exceeds any SMB sequential read, so VDF is **sampling frames, not
  reading whole files** — the default scan is *not* bottlenecked on full-byte I/O over SMB.
  Per-file cost ≈ seek + sampled-frame decode + hashing. Whether CPU- or network-bound at the
  margin is still TBD.

## Observation — default whole-file compare over-matches on dark footage

The default compare grouped **unrelated** TV episodes (The Chosen, Battlestar Galactica, Star
Trek Discovery, Firefly, Spartacus, …) at 92–96% "Match." Cause: the default method reduces
sampled frames to small grayscale signatures, and **dark, low-detail frames all look alike**,
so unrelated night/interior scenes score as highly similar. This is a known limitation of naive
frame-hash dedup, not a bug.

UI/column meanings (for reference):

- **"Match %"** = whole-file visual similarity to the group's reference file.
- **"Wasted space"** sort = orders groups by disk reclaimable if you keep one copy — a *dedup*
  metric, irrelevant to bumpers.

**Why it doesn't threaten us:** we do not use whole-file similarity dedup. Bumper matching uses
audio-fingerprint **partial-clip** detection and **AI visual partial** detection — both find a
shared *segment with a time offset* (the "Clip Offset" column), not overall file resemblance.
Raising the threshold / enabling pHash collapses most of these false positives, confirming it's
method/threshold, not breakage.

## Measurement 2 — Deep Clean (AI embeddings + audio fingerprinting)

- **~6 files/min (~10s/file)** → ~6 hours for this 2,110-file subset; extrapolates to **days**
  for the full library. ~20× slower than the default visual scan.

## Root cause — the GPU is idle (code-confirmed)

Deep Clean runs **entirely on the CPU**; the GPU desktop does nothing:

- **Decode on CPU.** FFmpeg hardware acceleration defaults to `none`
  (`VDF.CLI/Commands/SharedOptions.cs`), though `cuda` is a supported mode
  (`VDF.Core/FFTools/FFHardwareAccelerationMode.cs`, applied in `FfmpegEngine.cs`).
- **ONNX inference on CPU.** `VDF.Core/AI/OnnxEmbedder.cs:46` creates a plain
  `new SessionOptions()` (only `IntraOpNumThreads`, clamped to *half* the cores) with **no**
  `AppendExecutionProvider_CUDA`; `VDF.Core.csproj` uses the CPU ONNX Runtime
  (`Microsoft.ML.OnnxRuntime.Managed` + CPU native), not `Microsoft.ML.OnnxRuntime.Gpu`.
- The embedding itself is cheap (~50 ms/file per VDF docs), so at ~10s/file the **dominant cost
  is decode + SMB I/O**, not inference.

This *validates* the ADR 0002 thesis: the GPU desktop matters, and GPU acceleration is real
net-new value — VDF ships CPU-default.

## Levers, ranked

1. **Structural (biggest, already in design):** audio-first + targeted matching. Do **not** run
   DINOv2 across the whole library — reserve it for ambiguous cases, and for our actual
   workflow (match against the catalog / find one snippet's occurrences) all-pairs Deep Clean
   is rarely needed at all.
2. **GPU decode (no code):** enable FFmpeg hardware acceleration = `cuda` in Settings and
   re-measure. Caveats: may fall back on some codecs/HDR; per-file seek overhead can blunt the
   gain — measure it.
3. **GPU ONNX (small code change):** swap to `Microsoft.ML.OnnxRuntime.Gpu` and add
   `options.AppendExecutionProvider_CUDA()` at `OnnxEmbedder.cs:46`. Smaller win than decode.

## Code touch-points (for when we implement GPU acceleration)

| Concern | File | Change |
|--------|------|--------|
| GPU decode | `VDF.CLI/Commands/SharedOptions.cs`, `VDF.Core/FFTools/FfmpegEngine.cs` | Default/opt into `-hwaccel cuda`; keep graceful CPU fallback (an `FfmpegErrorClassifier` for hwaccel failures already exists). |
| GPU inference | `VDF.Core/AI/OnnxEmbedder.cs:46` | `options.AppendExecutionProvider_CUDA(deviceId)` before creating the `InferenceSession`. |
| GPU runtime pkg | `VDF.Core/VDF.Core.csproj` | Add/swap to `Microsoft.ML.OnnxRuntime.Gpu` (CUDA native). |

## Measurement 3 — Deep Clean with GPU decode (`hwaccel=cuda`), no code change (2026-07-16)

Lever 2 (GPU decode, Settings-only toggle) confirmed on the maintainer's **RTX 3080**:

- **Settings:** Processing → Hardware Acceleration = `cuda`; **"Use native Ffmpeg binding" left
  off** to isolate this one variable (that toggle is a separate, undocumented-until-now lever —
  its own description claims it's a bigger win than GPU decode; not yet measured).
- **Same subset as Measurement 2:** the identical 2,110-file TV-only set, for a controlled
  comparison.
- **Confound:** a background TrueNAS vdev **scrub was running for the entire scan** (started
  ~25 min before the scan, per maintainer check). The scan wasn't re-run scrub-free — the
  directional finding (GPU decode is a large win) doesn't depend on it, and if anything this
  measurement is a **conservative floor**: the scrub-free rate is likely at or above what's
  recorded here.
- **Checkpoints** (cumulative, then incremental since the prior checkpoint):

  | Elapsed | Files | Cumulative rate | Incremental rate |
  | ------: | ----: | --------------: | ---------------: |
  |      2m |    82 |  41.0 files/min |                — |
  |      8m |   304 |  38.0 files/min |   37.0 files/min |
  |     15m |   574 |  38.3 files/min |   38.6 files/min |
  |  26m17s | 1,000 |  38.1 files/min |   37.8 files/min |

  Rate is **stable at ~37–41 files/min** throughout — notably, it did *not* fluctuate wildly
  despite the subset mixing 1080p/4K and varying file sizes, scanned sequentially (show by show).
  Either per-segment resolution mix was similar enough not to show up at this granularity, or
  GPU decode throughput is less resolution-sensitive than expected. Not a controlled test of
  that specifically, just an observation.
- **Result:** measured at **~38 files/min** average vs. the CPU-only baseline of ~6 files/min
  (Measurement 2) — a **~6.3× speedup** (conservative, per the scrub caveat above). Projects to
  **~55 min** for the full 2,110-file subset vs. ~6 hours on CPU. Stopped at 1,000/2,110 files —
  four consistent checkpoints were enough to confirm the rate; a full run to completion wasn't
  judged necessary.

**GPU decode alone gets most of the way to GPU-desktop-class throughput**, without touching
`OnnxEmbedder`'s CPU-only ONNX inference (lever 3) or the native FFmpeg binding (unranked,
possibly bigger, not yet measured). **Considered validated — no re-test planned.**

## Matching test — single season (18 episodes) & the threshold finding

Test: ~18 episodes of one season, scanned in ~41s; result was only **2 files matched at 92%**
— *not* the shared intro grouping we hoped for. This is a **settings artifact, not a matching
failure**:

- **`PartialClipMinRatio = 0.10` (default 10%)** — partial-clip detection discards any matched
  segment shorter than 10% of the source duration (`VDF.Web/Services/WebSettingsService.cs:69`,
  mirrored elsewhere; matches the README's 10% default). A TV intro (~30–90s) inside a ~45-min
  episode is **~1–3%**, so it's filtered out by design.
- Whole-file compare (the default method) can't catch a shared intro at all — a ~1-min intro is
  <2% of a 45-min runtime, so overall similarity stays low.

**Design requirement (important):** bumper matching must key off **absolute clip duration**
(e.g. "≥5s"), **not** a percentage of the source. VDF's ratio gate is built for the opposite
shape ("is this file a clip of that longer movie"); bumpers are tiny relative to full episodes,
so a ratio gate structurally excludes them.

**Concrete confirmation (the integer 1% floor):** the *Min clip/source ratio* field is an
integer percent with a floor of **1%**. In a 49-min episode (~2954s) that means the shortest
detectable clip is **~30s**; a real 5s bumper is ~0.17% and a 14s clip ~0.47% — both filtered
*before* any matching. Bumpers commonly run ~5–15s (the maintainer literally keeps a
`front05sec` trim bucket), so VDF's ratio gate cannot see them in full-length episodes. Our
matcher must gate on absolute seconds, not a percentage. (Also relevant to test design: an
extracted test clip must exceed 1% of the source, i.e. ≥~30s here, to make VDF's partial-clip
pass run at all.)

**Matcher semantics — CONFIRMED (read `ScanEngine.cs` `ScanForPartialDuplicates` /
`CollectPartialMatchCandidates` / `SlidingWindowCompare`).** VDF's audio partial-clip detection
models exactly one shape: **a whole shorter file contained inside a longer file.** For each pair
it computes `ratio = clipDuration / sourceDuration` and:

- `if (ratio >= 0.95) continue;` — **skips near-equal-length pairs** (the blocker for our
  same-length episodes; they're rejected *before* any fingerprint comparison).
- `if (ratio < PartialClipMinRatio) break;` — skips clips under 10%.
- `SlidingWindowCompare(shorter, longer)` then slides the **whole** shorter fingerprint over the
  longer and averages Hamming similarity across the **entire** clip — it looks for "the whole
  short file appears here," not "a shared sub-region exists."

**Empirically corroborated (2026-07-15):** a scan with Partial Clip Detection **on** and min
ratio 1% over 16 equal-length Caprica episodes produced **no clip offsets** — the episodes
grouped only via the whole-file visual compare (Matching similarity threshold was set to 1%).
Exactly what the `ratio >= 0.95` gate predicts: equal-length pairs are rejected before any audio
comparison.

Implications for our two matching directions:

- **Video → catalog (reuse, ~works today):** a catalog bumper (~60s) vs. a full episode (~45min)
  is ratio ~2% — exactly VDF's "short clip in long file" shape. Lower `PartialClipMinRatio`
  below the bumper/episode ratio and VDF's existing `SlidingWindowCompare` finds it with an
  offset. Little net-new code for the catalog-*apply* path.
- **Discovery (net-new):** finding a shared bumper between two full-length episodes (before it's
  isolated) needs a **windowed/local matcher** that finds the best-matching *contiguous run* of
  fingerprint blocks (a shared ~60s region), not a whole-file average. The `ratio >= 0.95` gate
  and whole-clip averaging structurally exclude this. Reuse VDF's chroma fingerprints + Hamming
  primitives; write the shared-segment detection on top.

## The `TooDark` guard

VDF flags entries as `EntryFlags.TooDark` and drops too-dark sampled frames from the visual
fingerprint (`grayBytes`). Dark shows therefore produce **sparse visual fingerprints**, which
weakens visual matching on dark content and is another reason to **lean on the darkness-immune
audio fingerprint** as the primary signal.

## Direct probe result — matcher CONFIRMED WORKING (2026-07-15)

Ran `BumperMatchProbe` (`VDF.IntegrationTests`) — calls `ChromaprintEngine.ExtractFingerprint`
+ `ScanEngine.SlidingWindowCompare` directly, bypassing every gate. A 40s Doctor Who S1
title-sequence clip vs. all 13 season-1 episodes:

- **13/13 episodes matched at ≥80% audio similarity.**
- Source episode E01: **97.7% @ 0s**. Others: **84.8–95.0%** at offsets **36–199s**.
- The varying offsets correctly locate each episode's intro *after its cold open* — i.e. the
  matcher finds a shared segment at **any position**, not just the start (interstitial-capable).

Conclusions:

- The inherited fingerprint + sliding-window matcher **works**. Every earlier GUI failure was
  gates/settings (ratio floor, 95% ceiling, already-grouped exclusion, stale cache), **not the
  engine**. The **catalog-apply / snippet→library** path is proven on inherited code.
- **Thresholds need tuning:** even the identical-source E01 scores 97.7% (not 100%) — block
  quantization/alignment — and the lowest true positive is 84.8%. Before trusting an automated
  cut we must characterize the **false-positive floor** (run the clip vs. unrelated shows) and
  set the threshold in the gap.
- Offsets are ~1s (block) resolution: good for locating, needs refinement for frame-accurate
  trimming.

### False-positive floor (2026-07-15)

Same clip vs. an unrelated show (Avatar: The Last Airbender S1, 20 eps): **0/20 matched**,
scores **59–65%**. So:

- **Clean ~20-point gap:** false-positive ceiling ~65% vs. true-positive floor ~85%. A
  threshold around **75%** separates them well.
- The floor sits at ~60% (not ~50%) because `SlidingWindowCompare` returns the **best of ~N
  offsets** — with a 40-block clip over ~1400-block episodes, chance finds a mediocre alignment
  somewhere. Consequence: the false-positive floor **rises with longer sources and shorter
  clips**. Very short bumpers (≈5s = ~5 blocks) will be much noisier → enforce a **minimum clip
  length** and/or a higher bar for short clips.

**Eval corpus:** assemble a small local labeled set — positives (DW S1), variants (DW S10,
re-arranged same theme), negatives (Avatar, others) — reused for threshold tuning, the
discovery algorithm, and regression tests.

### Short clips: audio alone collapses (5s test, 2026-07-15)

- 5s intro clip vs DW S1 (**true positives**): mostly **72–86%**, with several real matches only
  ~72–77%; localization also degraded (best offset often landed on the reused **outro** theme or
  a spurious spot rather than the intro).
- 5s clip vs Avatar (**false positives**): **69–76%**.
- **These overlap — no threshold separates them.** At ~5 fingerprint blocks the audio matcher
  can't distinguish a real bumper from unrelated content (too few bits + best-of-many-offsets).
- 3s clip vs DW S1: TP **76–85%** but offsets now mostly **wrong** (random / near-end) — audio
  has lost *localization* too, not just discrimination. Full collapse.
- **Practical floor for audio-only: ~15–20s.** Reliable at 40s, unusable at ≤5s.

**Design consequence — audio needs a second signal for short bumpers.** Audio fingerprinting is
reliable only for **longer** bumpers (clean 20pt gap at 40s; fully collapsed at ≤5s). Short
bumpers (≤~15s) need a **visual** signal. Note: the maintainer estimates **~98% of bumpers are
motion sequences**, not static cards — and motion *helps* here. A moving bumper is a distinctive
**temporal sequence**, so matching a *sequence of frames at a consistent offset* is far more
discriminating than a single frame, and it dissolves the "which frame do I match?" problem.
VDF's DINOv2 visual-partial already works exactly this way (requires ≥4 keyframe hits agreeing on
one time offset) and its embeddings also absorb our mixed-resolution/letterbox normalization.
Architecture: **audio = fast candidate generator for long bumpers; visual sequence match = the
primary signal for short bumpers** (combining inherited parts; empirically justifies
matching-approaches Approach 3).

**Caveat — VDF's dense sampling is too coarse *and* culls dark frames.** `GetAiPartialIntervalSeconds`
samples every **5–15s**, and `SelectUsableDenseFrames` drops dark/duplicate frames; the matcher
needs **≥4 consistent hits**. **Empirically (2026-07-15):** even the **40s** DW intro produced only
**2 usable frames** (5s interval, and the dark vortex intro is mostly culled) → 0/13 matched. Both
sides are affected — clip *and* episodes are coarsely sampled with dark culling — so a short and/or
dark bumper has too few frames on either side. The DW dark-vortex intro is a **worst case**; bright
motion idents (most real bumpers) would cull far fewer frames.

**Task #22 (fine-grained visual):** needs finer sampling on **both** the clip and the episodes,
plus reconsidering the dark-frame cull. The self-embed recipe is mapped and ready to build:
`FfmpegEngine.GetDenseAiFrames(path, interval, …)` → `OnnxEmbedder.EmbedBatchQuantized(...)` (store
byte format), with model/runtime/store resolved by setting `DatabaseUtils.CustomDatabaseFolder` to
the GUI's DB folder. Requires re-embedding episodes at a finer interval too (a scan-setting change
or in-probe episode embedding), so it's deliberate Phase-2 engineering, not a quick probe.

## Positional windowing rescues short clips (2026-07-15)

Constraining the offset search to the first/last N seconds shrinks the offset space, dropping the
false-positive floor — the exact effect that killed short clips at full-file. **5s DW intro clip,
head window 15s:**

- E01 (intro genuinely at the head): head **96%** — true positive preserved.
- All other DW episodes (intro after a cold-open → effectively head-negatives): head **50–68%**,
  down from 72–86% at full-file. The noise floor cratered.
- Gap widened from ~10–20pt (full-file) to **~28pt** (96% TP vs. ≤68% floor) — for a **5s** clip
  that was undiscriminable before.

**This validates the maintainer's variable-density + edge-tagging design:** dense-sample and match
short edge bumpers only within the head/tail region — cheaper *and* more accurate (fewer offsets =
lower FP floor). Design notes: targets are junk **edge idents** (e.g. "Coming to Blu-ray 2012" at
the very start, "Bad Robot" 7s at the very end) — *not* title sequences (kept, integral). Bumpers
sit at the true file edges (no cold-open issue for these), and a bumper appearing at both ends is
fine as two region-tagged snippets. Data model needs per-frame timestamps (non-uniform density).

**Real target found — and it's visual-only (2026-07-15):** Caprica's ~5s end bumper is a class the
audio path *cannot* touch — its audio varies per episode (credits music fading out on some,
silence on others), so the fingerprint differs every time. This proves a real, common class of junk
bumpers (studio/network **end-cards**) has no usable audio → **the visual path is required, not
optional.** Testing it needs task #22: fine-sample the clip *and* each episode's **tail** (~last
30s), embed via ONNX, and match with the tail window. This is the concrete target for the
fine-grained visual build.

## VISUAL PATH CONFIRMED — real junk-bumper stack (2026-07-15)

`VisualTailProbe` (self-embeds the clip + each episode's last 30s at a fine interval; matches via
`TryMatchDenseFrames`, tail-windowed; ONNX model via `AiComponents.TestOverrideModelPath`, runtime
from the test project's package). Test: **Marvel's Daredevil S1** — the ~20s end-bumper stack
(DeKnight, Goddard, ABC Studios, Marvel, Netflix, black; mixed sound/silent) vs. all 13 episodes,
clip interval **0.5s**, tail 30s:

- **13/13 matched at 97–98%**, consistent tail offsets ~8–12s (variation = differing credit-roll
  lengths before the stack). Run took 26s; 27 clip frames, 43–51 tail frames/episode.
- This is the real target class — a **silent/mixed-audio studio ident stack at the edge** that
  audio cannot touch. **The visual path works cleanly, with a huge margin.**

**Combined architecture validated end-to-end:** audio fingerprint for audible/long bumpers +
fine-grained DINOv2 visual (tail/head-windowed) for silent/short edge bumpers. Both signals now
empirically confirmed on real bumpers.

**Corrected diagnosis — the limiter is STATIC content + VDF's ≥4-hit rule, not darkness (2026-07-15).**
The Netflix logo is *bright* (red on near-white). But once it forms it's **static**, and
`GetDenseAiFrames` **dedupes near-identical frames**, collapsing a held logo to ~1 distinct frame
(6 total, with the black tail). VDF's `TryMatchDenseFrames` requires **≥4 distinct frames agreeing
on one time offset** — built to find a *moving* clip inside a recording, so it structurally cannot
match a short/static bumper. This is a **tool mismatch**, and **short detection is a hard
requirement** (Caprica's 3s end-card; standalone short idents cannot be routed around by
"grab-generous + cut-to-edge" — you must detect the short thing first).

**Direction — presence matcher:** instead of "≥4 frames agree on an offset," ask "does the bumper's
distinctive frame *appear* in the target's edge region at high cosine?" One strong match of a
distinctive frame IS the detection; drop the ≥4-hit rule and control false positives with a high
cosine threshold + the positional (edge) constraint. `VisualTailProbe` now reports per-frame best
cosine + a presence count alongside the rigid matcher, so we can characterize TP vs FP for presence.

**RESOLVED — it was corrupted/mis-extracted clips all along (2026-07-16).** The "static logo" and
"darkness" diagnoses above were both wrong: they were theorizing about broken input. The isolated
Netflix clips were hand-cut and defective (`daredevil-netflix8.mkv` is literally corrupt — MPC-BE
crashes, VLC hangs; an earlier one was ~1.2s of the wrong content → 6 frames, `bestCos` ~45%). Once
the probe **auto-cuts the clip from a reference episode** (`BUMPER_CLIP_EPISODE`; same reliable
extraction as the tails), the last ~4.8s of Daredevil E01 gave **24 clean frames** and matched
**12/12 at 98–99%** on **both** the presence matcher (present=24/24) **and** VDF's rigid ≥4-hit
matcher (~98%). So short visual bumpers work fine — the whole ordeal was clip integrity, not a
matcher limit. **Lesson: never trust a hand-cut clip; the tool extracts clips itself.** Presence is
still useful as a fallback for genuinely static/tiny cases, but for real motion bumpers the rigid
matcher also succeeds once the input is valid.

**False-positive floor — CLEAN (Avatar, 2026-07-16):** the same Daredevil clip vs. 21 Avatar
episodes scored **bestCos 23–33%, present 0/24, rigid:no** across the board (even a stray intro clip
rejected at 21%). So **TP 98–99% vs FP ≤33% — a ~65-point gap with zero false positives.** Any
threshold from ~40–95% separates them cleanly.

**Matching risk RETIRED.** All signals validated on real bumpers: audio fingerprint (long/audible),
audio + positional window (short/audible), and visual presence/rigid DINOv2 (silent/short). The one
hard rule learned along the way: **the tool must extract clips itself — never trust a hand-cut clip.**

Next: **boundary precision** (0.5s interval → refine toward frame-accurate cut points) and
**sub-clip / sub-bumper tests** — extract the last 5s (just Netflix) or last 7s and confirm they
match too, then work out how to distinguish "the whole 20s stack" from "one piece of it" (the
boundary-growing idea).

## Open questions / next tests

- **Corrected validation for video → catalog:** the same-length-episodes test can't work even
  at min ratio ~1% — the `ratio >= 0.95` gate rejects equal-length pairs before matching.
  Instead, extract just the ~60s **intro as a standalone short clip**, drop it in a folder with
  the full episodes, enable Partial Clip Detection with min ratio below the clip/episode ratio,
  and confirm VDF finds the intro **inside each episode** with a sensible offset. That validates
  the reusable catalog-apply path.
- **Discovery matcher (net-new):** prototype a windowed/local shared-segment detector over VDF's
  chroma fingerprints (best contiguous run of matching blocks), since VDF's whole-clip averaging
  + 95% gate can't find a shared sub-segment between two full-length files.
- ~~Measure Deep Clean files/min with `hwaccel=cuda`.~~ **Done — see Measurement 3** (~41
  files/min, ~6.8× over CPU). Follow-ups: confirm the rate holds for the full 2,110-file run
  (not just the 82-file checkpoint), and measure "Use native Ffmpeg binding" as its own lever.
- Profile the Deep Clean bottleneck now that decode is on GPU: decode vs. SMB I/O vs. the
  cross-file matching phase vs. ONNX inference (still CPU — lever 3, unmeasured).
- Correctness test: does partial-clip / AI-partial detection group same-series episodes on
  their shared **intro** with a sensible clip offset? (Use a small single-series folder.)
