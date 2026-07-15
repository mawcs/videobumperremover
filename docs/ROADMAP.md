# Project Roadmap

A phased roadmap for the Video Bumper Remover. This is a living plan meant to be refined,
not a fixed spec. Each phase lists its goal, the work, the key decisions to make, and the
open questions to resolve before or during it.

The problem decomposes into four hard parts, and it helps to name them up front:

1. **Detection** — given one video, find where a bumper starts and ends.
2. **Fingerprinting + matching** — represent a snippet so the *same* snippet can be found
   in other videos, robustly, despite re-encoding, resolution, or letterboxing differences.
3. **Verification UX** — let a human confirm matches before and after cutting, with special
   attention to sub-bumpers and mid-video interstitials.
4. **Removal** — cut the confirmed snippet out of many files safely and auditably.

Everything else (storage, deployment shape, batching) is in service of these four.

## What we inherit from VDF vs. what's net-new

This project is a fork of Video Duplicate Finder (see
[`decisions/0003-repository-structure.md`](decisions/0003-repository-structure.md)), which
already implements most of parts 1–2:

- **Inherited (build on, don't rebuild):** library scanning + a persisted scan database with
  fast incremental rescan; perceptual hashing; **audio-fingerprint partial-clip detection**
  (finds a shorter clip inside a longer video via Chromaprint-style matching with a clip
  offset) — essentially our "find this snippet elsewhere" primitive; and **DINOv2/ONNX visual
  partial matching** that already tolerates cropped, **letterboxed**, zoomed and color-graded
  copies and locates trimmed clips with no audio. FFmpeg decode, GPU paths, and an Avalonia
  GUI + CLI + Web front-ends also come for free.
- **Net-new (the real work):** VDF *finds and deletes whole duplicate files*; it does not cut
  a segment out of a file. So our substantial additions are: (a) a **trim/removal engine**
  that excises a snippet (front/end/mid-video) safely and auditably; (b) **bumper-centric
  workflow** — take one identified snippet and act on every file containing it; (c) the
  **sub-bumper boundary problem** (find a bumper's full extent, not a fragment); (d)
  **interstitial** handling (mid-video, not just edges); (e) **auto-discovery** of recurring
  segments; and (f) a **verification UX** tailored to previewing and confirming *cuts*.

The phases below are annotated accordingly.

---

## Phase 0 — Foundations & decisions

**Goal:** lock the decisions that constrain everything downstream before writing app code.

**Mostly resolved:**

- **Platform/stack — decided:** C#/.NET fork of VDF, Avalonia UI, ONNX for ML, desktop app
  reaching media over SMB. See [`decisions/0002-tech-stack.md`](decisions/0002-tech-stack.md)
  and [`decisions/0003-repository-structure.md`](decisions/0003-repository-structure.md).
- **Library size — known:** >~5TB, many thousands of files → an index is mandatory (VDF's
  scan DB provides it) and I/O over SMB is the expected bottleneck.
- **Matching strategy — chosen:** start from VDF's audio-fingerprint partial-clip detection
  (cheapest, letterbox/resolution-immune), then its DINOv2 visual partial matching for
  audio-less cases. See [`research/matching-approaches.md`](research/matching-approaches.md).

**Still open:**

- Assemble a small, representative **test corpus**: a handful of videos that share known
  bumpers, including at least one **sub-bumper** case and one mid-video **interstitial**.
- What container formats/codecs are present? (Decides whether stream-copy trims are viable.)
- Acceptable false-positive vs. false-negative tolerance for automated matching.
- Measure **SMB throughput** (1GbE vs 10GbE) and confirm the desktop ffmpeg has NVDEC/NVENC.

**Exit criteria:** test corpus assembled; codecs surveyed; throughput measured; fork builds
and runs locally.

---

## Phase 1 — Media access & metadata inventory

**Goal:** reliably enumerate the library and read technical metadata, read-only. *(Largely
inherited — VDF already scans directories and persists a scan DB with incremental rescan;
this phase is mostly validating and, if needed, extending it.)*

**Subset or full library?** Both, in order — and the distinction matters because *inventory
is cheap but fingerprinting is expensive*:

- **Inventory is metadata-only.** Reading path, duration, codec, resolution, fps, and audio
  streams uses `ffprobe`/container headers — it moves almost no data and doesn't decode
  frames. So a **full-library inventory pass is realistic even early**, and is the right way
  to answer the Phase 0 codec/format survey.
- **Develop against the test corpus first** for fast iteration, then run the full inventory
  once the capability is proven.
- **Reserve subset-first caution for the fingerprinting passes (Phase 2/3)** — those are
  decode- and I/O-heavy, so validate accuracy and throughput on the small corpus before ever
  committing to a full fingerprint run.

Work:

- Confirm VDF's scan reaches the media over SMB and inventories correctly; extend the stored
  metadata if bumper work needs fields VDF doesn't already keep.
- Establish an **absolute rule**: nothing in this phase (or ever) modifies source files.

Key decisions: reuse VDF's DB schema as-is vs. extend it; how re-scan detects changed files.

Open questions: are there symlinks, mixed mounts, or files that are actively being written?

**Exit criteria:** a queryable inventory of the full library that refreshes incrementally,
plus a completed codec/format survey from the first full inventory pass.

---

## Phase 2 — Evaluate & adapt VDF's matching for snippets

**Goal:** confirm VDF's existing matching can find a *known bumper snippet* across the test
corpus, and identify the gaps we must close. This replaces "build fingerprinting from
scratch" — the spike is now mostly *evaluation and adaptation*, not invention.

Work:

- **Evaluate VDF as-is on the test corpus:** run its audio-fingerprint partial-clip detection
  and its DINOv2 visual partial matching against the known-bumper set. Measure precision/
  recall, tolerance to re-encoding/resolution/letterboxing (VDF's AI pass claims letterbox
  tolerance — verify it), and speed per file.
- **Snippet-centric querying (net-new):** VDF pairs whole files; we need "given one snippet,
  return every file containing it, with the in-file offset." Determine how much of this falls
  out of VDF's clip-offset machinery vs. needs new code.
- **Letterbox/burned-in text (verify, then fill gaps):** confirm how VDF's normalization
  handles mixed resolution and cropped bars; add `cropdetect`-based handling only where its
  coverage falls short. Treat burned-in studio text as both a hazard and a possible signal.
- **Sub-bumper extent (net-new):** prototype growing a match's boundaries outward until the
  fingerprint stops agreeing across all containing files — the true bumper is the largest
  region that agrees everywhere.
- Details and trade-offs: [`research/matching-approaches.md`](research/matching-approaches.md).

Key decisions: which VDF matcher is primary for our case; similarity thresholds; how to
express "find this snippet everywhere" on top of VDF's APIs.

Open questions:

- Does VDF's audio matching alone get us most of the way with far less compute?
- Where exactly does VDF's clip detection stop and our net-new snippet/extent logic begin?

**Exit criteria:** on the test corpus, we can point at one bumper and reliably list the other
files containing it, with correct start/end, including the sub-bumper case — reusing VDF
wherever possible and documenting the gaps.

---

## Phase 3 — Matching pipeline at library scale

**Goal:** turn the spike into a dependable batch process over the whole library. *(The
fingerprint index + incremental rescan are largely inherited from VDF's scan DB; the
net-new parts are snippet-centric querying, confidence scoring for bumpers, and
auto-discovery.)*

Work:

- Reuse/extend VDF's fingerprint index so scans don't reprocess unchanged files.
- Run "find all videos containing snippet X" efficiently across the collection.
- Rank/group results by match confidence; flag ambiguous or partial (sub-bumper) matches
  for extra scrutiny.
- **Auto-discovery (stretch):** mine the fingerprint index for segments that recur across
  many files to *propose* bumpers automatically, without the maintainer identifying one
  first. Cluster recurrences by frequency and position; each cluster becomes a candidate for
  the verification UI. See Approach 6 in
  [`../docs/research/matching-approaches.md`](research/matching-approaches.md).
- Use the GPU where it actually helps (decode/feature extraction) and record where it does.

Key decisions: index structure; incremental update strategy; confidence scoring model.

Open questions: memory/time budget for a full pass; how to handle near-duplicate-but-not-
identical bumpers (slightly different promos).

**Exit criteria:** a repeatable scan that returns a scored candidate list for any snippet.

---

## Phase 4 — Verification UI (pre-cut)

**Goal:** let a human quickly confirm which matches are real before anything is removed.
*(Builds on VDF's Avalonia results UI, but reframed around confirming **cuts** rather than
choosing which duplicate file to delete.)*

Work:

- Present candidate matches with thumbnails and inline playback of the matched region.
- Show the proposed cut (start/end) on a timeline, with a little context on each side.
- Make the **sub-bumper** and **interstitial** cases explicit: surface whether a match is
  at an edge or mid-video, and whether a shorter match might be part of a longer bumper.
- Batch controls: approve/reject per file, select-all-with-guardrails, adjust boundaries.

Key decisions: UI framework (follows Phase 0 platform decision); how much manual boundary
editing to support.

Open questions: how many items will a typical review batch contain? (Drives UX for scale.)

**Exit criteria:** the maintainer can review a batch of matches and produce a confirmed
removal queue with confidence.

---

## Phase 5 — Removal engine

**Goal:** cut confirmed snippets from many files safely and auditably. *(This is the single
biggest net-new component — VDF finds and deletes whole duplicate files, but has no notion of
excising a segment. Everything here is ours to build.)*

Work:

- Trim via FFmpeg. Prefer stream-copy (`-c copy`) where frame accuracy allows; re-encode
  only when necessary (note the cost and reasons).
- Handle all three cases: front trim, end trim, and mid-video interstitial removal
  (split + concat).
- Write outputs to a staging area; never overwrite originals until explicitly confirmed.
- Maintain a **removal manifest** per file (source, snippet start/end, method, output path,
  reversible?) so every cut can be audited and undone.

Key decisions: stream-copy vs. re-encode policy; keyframe handling for frame-accurate cuts;
staging vs. in-place-with-backup.

Open questions: how to handle files where a clean cut requires re-encoding a segment only?

**Exit criteria:** confirmed queue processes into trimmed outputs with a full audit trail.

---

## Phase 6 — Post-cut review & library reconciliation

**Goal:** verify the right thing was removed and fold results back into the library.

Work:

- Review list of processed videos: confirm correct snippet removed, no over/under-trim, no
  sub-bumper mistake left behind.
- Re-scan trimmed files to confirm the bumper fingerprint is gone.
- Promote validated outputs into the library (replace originals per policy); keep the
  manifest and, if desired, backups.

Key decisions: replacement/backup policy; retention of manifests and staged originals.

Open questions: do we want a one-click undo per file from the manifest?

**Exit criteria:** a closed loop — detect, match, verify, remove, validate, reconcile.

---

## Cross-cutting concerns (apply to every phase)

- **Mixed resolution & letterboxing:** the library spans multiple resolutions and some files
  have letterbox bars with **burned-in studio text**. Fingerprinting must normalize for scale
  and crop bars so the same bumper matches across copies; the burned-in text is both a
  matching hazard and a potential bumper signal. This is a first-class requirement, not an
  edge case.
- **Safety:** originals are never mutated without explicit confirmation; everything auditable
  and reversible until then.
- **Reproducibility:** deterministic fingerprints and recorded thresholds so results can be
  reproduced.
- **Performance:** measure per-phase; know where the GPU helps and where I/O over SMB is the
  bottleneck.
- **Testability:** keep the test corpus current, especially the sub-bumper and interstitial
  edge cases.

## Suggested first steps

1. **Build and run the fork locally** (VDF as-is) to confirm the toolchain works end to end.
2. **Assemble the test corpus** — a few videos sharing known bumpers, incl. a sub-bumper and
   an interstitial case.
3. **Run a full metadata inventory** (cheap) to survey codecs/formats and confirm SMB access.
4. **Evaluate VDF's matching** (audio partial-clip + AI visual partial) on the test corpus to
   see how far it gets us before we write anything (Phase 2).
5. Measure SMB throughput and confirm NVDEC/NVENC in the desktop ffmpeg.
