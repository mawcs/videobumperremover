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

---

## Phase 0 — Foundations & decisions

**Goal:** lock the decisions that constrain everything downstream before writing app code.

Work:

- Decide the **platform shape**: GPU desktop app vs. Dockerized server service vs. a hybrid
  (headless engine + thin UI). This drives the stack. See
  [`decisions/0001-tech-stack.md`](decisions/0001-tech-stack.md).
- Decide how the tool reaches the media: direct SMB/Windows share from the GPU desktop, or
  running where the files live on TrueNAS.
- Choose the **matching strategy** to prototype first (see Phase 2 and
  [`research/matching-approaches.md`](research/matching-approaches.md)).
- Define a small, representative **test corpus**: a handful of videos that share known
  bumpers, including at least one sub-bumper case and one mid-video interstitial.

Key decisions: platform/stack; where compute runs; primary matching approach.

Open questions:

- How large is the library (file count, total TB)? This decides whether a full-library
  scan can be brute force or needs an index.
- What container formats/codecs are present? (Affects whether stream-copy trims are viable.)
- Acceptable false-positive vs. false-negative tolerance for automated matching?

**Exit criteria:** ADR 0001 marked `accepted`; test corpus assembled; matching approach
chosen for the spike.

---

## Phase 1 — Media access & metadata inventory

**Goal:** reliably enumerate the library and read technical metadata, read-only.

Work:

- Walk the library over the chosen access path; build an inventory (path, duration,
  resolution, codec, container, fps, audio streams).
- Store the inventory in a lightweight local database/index.
- Establish an **absolute rule**: nothing in this phase (or ever) modifies source files.

Key decisions: inventory storage format; how to detect changes/new files on re-scan.

Open questions: are there symlinks, mixed mounts, or files that are actively being written?

**Exit criteria:** a queryable inventory of the full library that refreshes incrementally.

---

## Phase 2 — Detection & fingerprinting spike

**Goal:** prove we can fingerprint a snippet from one video and find it in others. This is
the technical heart of the project and deserves a throwaway spike before committing.

Work:

- Implement snippet extraction: given a source video + start/end, pull the snippet.
- Implement one or more fingerprinting methods and evaluate on the test corpus:
  - **Perceptual video hashing** (per-frame pHash/dHash sequences).
  - **Audio fingerprinting** (Chromaprint/landmark-style) — bumpers often share identical
    audio and this is cheap and re-encode-resilient.
  - **Combined / scene-boundary-assisted** detection.
  - Note ML/embedding approaches as a later option if the cheap methods fall short.
  - Details and trade-offs: [`research/matching-approaches.md`](research/matching-approaches.md).
- Implement **frame normalization before hashing**: downscale to a common working size and
  **detect + crop letterbox bars** (FFmpeg `cropdetect`) so mixed-resolution and letterboxed
  copies of the same bumper produce matching fingerprints. Handle **studio text burned onto
  letterbox bars** deliberately (see research notes) — it can both break matching and *be* a
  bumper signal.
- Measure: match precision/recall on the corpus, tolerance to re-encoding and resolution
  changes, and speed per video.

Key decisions: primary fingerprint representation; similarity threshold; alignment method
(how to locate the snippet's offset within a longer file, and its exact length).

Open questions:

- Does audio-only matching get us most of the way with far less compute?
- How do we detect the **full** extent of a bumper (to avoid the sub-bumper trap) — e.g.
  grow the match boundaries until the fingerprint stops agreeing across files?

**Exit criteria:** on the test corpus, we can point at one bumper and reliably list the
other files containing it, with correct start/end, including the sub-bumper case.

---

## Phase 3 — Matching pipeline at library scale

**Goal:** turn the spike into a dependable batch process over the whole library.

Work:

- Build/maintain a fingerprint index so scans don't reprocess unchanged files.
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

**Goal:** cut confirmed snippets from many files safely and auditably.

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

1. Answer the Phase 0 open questions (library size, codecs, access path).
2. Record ADR 0001 (platform + stack).
3. Assemble the test corpus.
4. Run the Phase 2 fingerprinting spike (start with audio fingerprinting — cheapest signal).
