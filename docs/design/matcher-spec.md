// (this is a design doc — no license header; headers go on source files only)

# Matcher specification — the "definition of done" for bumper matching

**Status:** authoritative build spec. If any other doc's wording conflicts with this one about
*how matching works or what to build*, **this doc wins** — raise the conflict and fix the other doc.

**Audience:** an agent (or human) about to build the real matcher. Read this **before** writing
any matcher code. It exists because the first productionization attempt built the wrong thing
(audio-only, no AI, no edge sampling) by following the research narrative literally instead of a
spec. This is the spec.

**Related:** [`../decisions/0006-edge-focused-fingerprinting.md`](../decisions/0006-edge-focused-fingerprinting.md)
(sampling), [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md) (the validation data
this spec is built on), [`bumper-catalog.md`](bumper-catalog.md) (where matches go),
[`../decisions/0005-code-organization.md`](../decisions/0005-code-organization.md) (VBR vs. VDF).

---

## 0. TL;DR — read this first

1. **Visual DINOv2 presence matching is the PRIMARY matcher.** It is the only path validated on
   the bumpers this project exists to remove (short, often **silent** studio/network idents —
   "Bad Robot," "Coming to Blu-ray 2012," the Netflix end-card stack).
2. **Audio fingerprinting is a secondary *accelerator*, not the main event.** It works only for
   *audible* bumpers, and is **dead** for silent/varying-audio ones — i.e. dead for the common
   case. Never ship or scope the matcher as "audio, visual later."
3. **The reference implementation already exists and works:** `VisualTailProbe.cs` (in
   `VDF.IntegrationTests/Comparison/`). Productionizing = **porting that faithfully**, not
   reinventing. It scored **98–99% on true matches vs. ≤33% on unrelated content** (~65-pt gap,
   zero false positives). Do not "improve" the algorithm before it's ported and re-validated.
4. **Edge-focused, variable-density sampling ([ADR 0006](../decisions/0006-edge-focused-fingerprinting.md))
   is part of the matcher**, not a later index optimization. Dense sampling at the edges is *why*
   short bumpers are detectable at all.
5. **The tool extracts clips; the user never supplies a pre-cut clip.** Every entry point takes
   `(source video, time range)`. (Already a standing rule — see AGENTS.md.)
6. **Target: a standalone, expandable `vbr` CLI** that runs the *complete* validated pipeline
   end to end — not a probe in the test project, and not the current audio-only `vbr match`.

If you build only the audio matcher again, you have built the wrong tool.

---

## 1. What "a match" means here

The job is **video → bumper**: given a short reference bumper (extracted from some source video)
and a library of full-length videos, report — for each library file — whether the bumper is
**present**, and roughly **where**. This is *not* VDF's whole-file dedup, and it is *not* "is
file A a duplicate of file B." It is "does this ident appear inside this episode, near an edge."

A correct matcher fuses three things. In priority order:

| Signal | Role | Works on | Validated result |
|---|---|---|---|
| **Visual DINOv2 embeddings + presence matcher** | **PRIMARY** | silent, short, static-or-moving idents | 98–99% TP vs. ≤33% FP |
| **VDF rigid dense-frame matcher** | corroborating (visual) | idents with ≥4 temporally-consistent frames | agrees with presence on true matches |
| **Audio Chromaprint + sliding-window** | accelerator | *audible* bumpers only | clean gap for long/audible; collapses for silent/short |

**Presence beats rigidity for our case.** VDF's rigid matcher requires ≥4 sampled frames to agree
on a single offset. Short/static bumpers don't give it that. The **presence matcher** (ours) asks
a weaker, correct question: *does any distinctive clip frame appear somewhere in the search region
at high cosine similarity?* One distinctive frame found at high cosine **is** the detection. Keep
the rigid matcher's result alongside as corroboration, but presence is the decision.

**Do not match on black/silence.** Mostly-black frames and silent padding are low-information: a
black frame matches black anywhere → false positives. Match on the **distinctive** content; the
padding gets removed later by inclusion (edge bumpers cut to BOF/EOF), not by being the signal.

---

## 2. The reference algorithm (port this)

Source of truth: `VDF.IntegrationTests/Comparison/VisualTailProbe.cs`. The productionized matcher
must reproduce its behavior. The essential pipeline:

1. **Extract the reference clip from a source video** (`ExtractTail`-style ffmpeg stream-copy of
   the last/first N seconds, or an explicit range). Never accept a pre-cut clip. Stream-copy is
   keyframe-bound and that's fine — a generous rough region is all we need.
2. **Sample frames densely at the edges.** For the clip, sample the whole (short) clip at a fine
   interval (~0.2–1s). For each library file, sample **only the relevant edge window** (e.g. the
   last `tailSec` seconds for an end bumper), also at the fine interval — *not* the whole file.
   This is the ADR 0006 edge-focus in action, and it's what makes this cheap enough to run per file.
3. **Embed each sampled frame** with DINOv2 via `OnnxEmbedder.EmbedBatchQuantized` (int8). Batch
   up to `OnnxEmbedder.MaxBatch`. Skip empty/black frames.
4. **Presence match:** for every usable clip frame, compute the best cosine
   (`EmbeddingMath.CosineSimilarity`) against any frame in the file's edge window. Count how many
   clip frames clear the **presence threshold** (default ≈0.90). `presentCount ≥ 1` ⇒ present.
   Track the single best cosine and its timestamp for reporting/boundary work.
5. **Rigid corroboration (optional, report-only):** also run
   `ScanEngine.TryMatchDenseFrames(fileRec, clipRec, hit, …)` and surface its similarity/offset.
6. **Report** per file: `bestCos@time`, `present=<hits>/<clipFrames>`, and the rigid result.

The audio path (`AudioBumperMatcher.FindInLibrary`, already built) plugs in as an **accelerator**:
when the bumper is audible, Chromaprint + `ScanEngine.SlidingWindowCompare` (with positional
head/tail windows) can confirm cheaply. It must never be the *only* path consulted.

### VDF.Core primitives this depends on

`OnnxEmbedder`, `EmbeddingMath.CosineSimilarity`, `FfmpegEngine.GetDenseAiFrames`,
`FfmpegEngine.FFmpegPath`, `DenseEmbeddingStore.DenseRecord`, `ScanEngine.TryMatchDenseFrames`,
`ScanEngine.SlidingWindowCompare`, `ChromaprintEngine.ExtractFingerprint`, `AiComponents`
(model path / `ModelFileName`). Most are `internal`; access is via the
`InternalsVisibleTo("VBR.Core")` glue already added to `VDF.Core.csproj` (ADR 0005).

---

## 3. What to build now — a standalone, expandable CLI

Recreate the validated pipeline as a **first-class `vbr` CLI** that is the product's backbone,
structured so catalog/removal/UI can grow on top later. Not a probe; not the audio-only stub.

### 3.1 Module layout (VBR.Core)

Keep signals behind a common interface so the CLI orchestrates them uniformly and new signals or
a catalog slot in without rework:

- `VBR.Core/Matching/IBumperMatcher` — `Match(referenceClip, candidate, region) → MatchResult`
  where `MatchResult` carries `{ present, bestScore, bestOffset, perSignalDetail }`.
- `VBR.Core/Matching/VisualBumperMatcher` — **new, primary.** The ported presence matcher +
  DINOv2 embedding pipeline from `VisualTailProbe`. Owns edge-window sampling for the visual signal.
- `VBR.Core/Matching/AudioBumperMatcher` — **exists.** Reshape its public surface to fit
  `IBumperMatcher` so it's an accelerator peer, not a parallel universe. Keep its logic.
- `VBR.Core/Fingerprinting/` — edge-focused, variable-density sampling ([ADR 0006](../decisions/0006-edge-focused-fingerprinting.md)):
  the `(timestamp, value)` non-uniform fingerprint + region tags (begin/end/middle). Both matchers
  sample through this, so dense-edge/sparse-middle behavior lives in one place.
- `VBR.Core/Extraction/ClipExtractor` — the one ffmpeg clip-extraction path (fold in
  `AudioBumperMatcher.ExtractClip` and `VisualTailProbe.ExtractTail`; single implementation).

### 3.2 CLI surface (expandable)

`vbr` is a multi-command root (it already is — `System.CommandLine`, see `VBR.CLI/Program.cs`).
Grow it; don't fork a second tool:

- `vbr match` — the end-to-end matcher. **Runs the visual matcher by default**, with audio as an
  opt-in/auto accelerator. Finalized options (superseding the "suggested options" of the first
  draft of this spec, after a design review — see `PROGRESS.md` for the discussion this came
  from; extend the existing command, keep the clip-extraction contract):
  - `--clip-from <video>` (was `--source` — clearer on a command line) + exactly one of
    `--clip-head-seconds` / `--clip-tail-seconds` (the rough region to extract as the reference
    clip — already implemented, keep it).
  - `--library <folder>` — files to search.
  - `--detection-mode visual|audio|both` (was `--signal` — "signal" is audio-engineering jargon;
    default `visual`; `both` runs visual as the decision-maker and reports audio alongside as
    corroboration).
  - **One `--region begin|end` flag, not separate per-edge flags.** A bumper lives at one edge;
    the same edge is used both to extract the reference clip *and* to search each candidate — so
    one flag drives both, instead of letting `--clip-head-seconds`/`--search-tail-seconds` be
    mixed into a nonsensical combination. Multi-region bumpers (e.g. a logo at both begin and
    end) are handled as two separate invocations/catalog entries, not one command trying to do
    both at once. `--region middle` is a future addition (interstitials), not built now.
  - `--clip-length <duration>` (required) — how much of `--clip-from` to extract as the
    reference clip. No sensible default exists across all bumpers; the user always states it.
  - `--search-length <duration>` (optional) — how much of *each candidate's* edge to search.
    **Defaults to `--clip-length` + 20s when omitted**, not a flat constant: the search window
    must have slack beyond the clip's own length (junk position drifts a little across episodes;
    audio's sliding-window match cannot even run if the window is shorter than the clip), and
    tying the default to clip length avoids the foot-gun of an under-sized window (e.g.
    `--clip-length 30s` with a stale flat `--search-length 5s` default). Override only if you
    need a wider or narrower search than that.
  - `--sample-interval <duration>` (was the ADR 0006 "density" idea, renamed — "interval" is
    the accurate term: it's seconds between sampled frames, smaller = denser). Default 1.0s.
    **Hard requirement, not a nice-to-have: must support intervals down to ~0.2s (5
    samples/sec) with no artificial floor.** Validated: a 4s clip failed to match at a 0.5s
    interval and succeeded at 0.2s. Short clips (the common case here) need this; document the
    guidance to lower it for clips under ~8s prominently in `--help`.
  - `--presence-threshold <float>` (default 0.90) and `--rigid-hit-threshold <float>` (default
    0.89, corroboration only) — the remaining ADR 0006/probe tuning knobs, matching
    `VisualTailProbe`'s own defaults so an unmodified invocation reproduces the probe.
  - `--min-similarity <float>` — audio's own match threshold (unchanged); visual's match
    determination *is* presence, no separate threshold needed.
  - Duration values (`--clip-length`, `--search-length`, `--sample-interval`) accept a bare
    number as seconds or a suffixed value (`5.1s`, `200ms`).
- Print, per file: `MATCH/—  name  present=h/n  bestCos@t  [rigid …]  [audio …]`, then a summary.
- **Leave room for** `vbr enroll` (add a match to the catalog), `vbr scan`, `vbr remove` — don't
  build them yet, but the module boundaries above must not preclude them.

### 3.3 Definition of done — MET (2026-07-17)

- [x] `vbr match --detection-mode visual` reproduces `VisualTailProbe`'s numbers on the same test
  media: Daredevil end-stack **12/12 @ 98–99%**, identical per-episode (bestCos/present/rigid) to
  the probe's own output; Avatar unrelated set **0/21 @ ≤33%**, matching the documented FP floor
  exactly (down to a stray `introclip.mkv` scoring the recorded 21%). Full comparison in
  `PROGRESS.md`. `VisualTailProbe` is kept in place for now (graduating/deleting it is a separate
  next step, not required for this definition of done).
- [x] Visual runs **without** any audio track/match required — `--detection-mode visual` never
  invokes the audio matcher at all (separate `IBumperMatcher` implementations, not a shared code
  path with a bypassed audio check).
- [x] Sampling is edge-focused, structurally: `VisualBumperMatcher.Match` always extracts via
  `ClipExtractor` before embedding, so the full-length candidate is never opened for dense decode.
- [x] No entry point accepts a pre-cut clip; all extraction is internal (`--clip-from` + region,
  never a clip file path).
- [x] Every new source file carries the AGPLv3 header (AGENTS.md).

---

## 4. Anti-patterns — do NOT do these

- ❌ **Audio-only.** Building/shipping the audio matcher as "the matcher" and deferring visual.
  Audio is dead for the silent idents that are the whole point.
- ❌ **Reinventing the matcher** instead of porting the validated `VisualTailProbe` presence logic.
- ❌ **Whole-file dense sampling.** Sample the edge window, per ADR 0006 — whole-file dense
  embedding is the expensive thing edge-focus exists to avoid.
- ❌ **Matching on black/silence.** Low-information; causes false positives.
- ❌ **Requiring the rigid ≥4-hit matcher to fire** before declaring a match. Presence (≥1
  distinctive frame at high cosine) is the decision; rigid is corroboration.
- ❌ **Accepting a pre-cut clip file** at any API/CLI/UI boundary.
- ❌ **"Improving" thresholds/algorithm before parity.** Reproduce the probe's numbers first, then tune.

---

## 5. Why this doc exists (so it doesn't happen again)

The earlier docs described audio as "first" (a research-sequencing artifact: audio was the cheapest
thing to validate first) and described the working visual matcher as "a probe, not yet
productionized." Read as a build order, that produces exactly the wrong tool: audio-only, no AI,
no edge sampling — which is what happened. The fix is to state the target plainly, once, here:
**visual-primary, audio-accelerator, edge-focused, ported from the probe, as a standalone CLI.**
