# Project Roadmap

A phased roadmap for the Video Bumper Remover. This is a living plan meant to be refined,
not a fixed spec. Each phase lists its goal, the work, the key decisions to make, and the
open questions to resolve before or during it.

The problem decomposes into five hard parts, and it helps to name them up front:

1. **Detection** — given one video, find where a bumper starts and ends.
2. **Fingerprinting + matching** — represent a snippet so the *same* snippet can be found
   in other videos, robustly, despite re-encoding, resolution, or letterboxing differences.
3. **The bumper catalog** — persist identified bumpers as first-class, reusable entities so a
   bumper identified once can be recognized and removed again in future media, not forgotten
   after one cleanup. See [`decisions/0004-bumper-catalog.md`](decisions/0004-bumper-catalog.md)
   and [`design/bumper-catalog.md`](design/bumper-catalog.md).
4. **Verification UX** — let a human confirm matches before and after cutting, with special
   attention to sub-bumpers and mid-video interstitials.
5. **Removal** — cut the confirmed snippet out of many files safely and auditably.

Everything else (storage, deployment shape, batching) is in service of these five.

The catalog reframes the whole workflow: instead of *identify → scan → remove → forget*, the
flow becomes *identify → **enroll** → remove → keep in catalog*, and future media is matched
**video → catalog** (a fresh rip checked against every known bumper) rather than always
starting from a hand-picked snippet.

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
  a segment out of a file, and it has no persistent notion of a "bumper." So our substantial
  additions are: (a) a **trim/removal engine** that excises a snippet (front/end/mid-video)
  safely and auditably; (b) the **bumper catalog** — a persistent, curated store of known
  bumpers plus **video → catalog** matching so a fresh rip is auto-checked against everything
  identified before; (c) **bumper-centric workflow** — take one identified snippet and act on
  every file containing it; (d) the **sub-bumper boundary problem** (find a bumper's full
  extent, not a fragment); (e) **interstitial** handling (mid-video, not just edges); (f)
  **auto-discovery** of recurring segments (which feeds catalog candidates); (g) a
  **verification UX** tailored to previewing and confirming *cuts*; and (h) **GPU
  acceleration** (NVDEC decode + ONNX CUDA) — VDF ships CPU-default, which makes its heavy
  passes ~20× slower than they need to be on our GPU desktop (see
  [`research/vdf-evaluation.md`](research/vdf-evaluation.md)).

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

**First throughput measurement (2026-07-15):** default (visual) scan of a TV-only subset —
**2,110 files / ~2.5 TB in 17m49s** (~118 files/min). Effective ~2.3 GB/s far exceeds any SMB
link's sequential read, empirically confirming VDF **samples frames rather than reading whole
files** — so the default scan is *not* bottlenecked on full-byte I/O over SMB (per-file cost is
seek + sampled-frame decode + hashing). Whether it's CPU- or network-bound at the margin is
still TBD, and the heavier audio-fingerprint / AI passes will read more per file, so they won't
necessarily hold this rate.

**Deep Clean measurement + GPU finding (2026-07-15):** the AI + audio-fingerprint scan ran at
**~6 files/min** (~10s/file) — ~20× slower, ~6h for the subset, days for the full library.
Root cause, confirmed in code: **the GPU is idle** — FFmpeg decode defaults to CPU (`hwaccel`
`none`) and ONNX inference runs on the CPU runtime (`OnnxEmbedder.cs:46`, no CUDA execution
provider). The embedding itself is cheap (~50 ms/file); decode + SMB I/O dominate. This
validates the ADR 0002 thesis that the GPU desktop matters, and makes **GPU acceleration a
priority net-new addition**. Full analysis, levers, and code touch-points:
[`research/vdf-evaluation.md`](research/vdf-evaluation.md).

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
- **Matcher shape — confirmed split (see [`research/vdf-evaluation.md`](research/vdf-evaluation.md)):**
  VDF's audio partial-clip matcher only models "a whole shorter file inside a longer file"
  (pairs with `ratio >= 0.95` are skipped; the whole clip is averaged, not a sub-window). So:
  - **Video → catalog** (short catalog bumper vs. full episode) reuses VDF nearly as-is — just
    lower `PartialClipMinRatio` below the bumper/episode ratio. Match on **absolute duration**
    (≥5s), not a % of source.
  - **Discovery** (shared bumper between two full-length episodes) is **net-new**: a windowed/
    local matcher over VDF's chroma fingerprints that finds the best contiguous run of matching
    blocks. Reuse the fingerprints + Hamming primitives; write the shared-segment logic on top.
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

## Phase 3 — Matching pipeline + bumper catalog

**Goal:** turn the spike into a dependable batch process, and stand up the **persistent
bumper catalog** with both matching directions. *(The fingerprint index + incremental rescan
are largely inherited from VDF's scan DB; the catalog, video→catalog matching, snippet-centric
querying, confidence scoring, and auto-discovery are net-new.)*

Work:

- Reuse/extend VDF's fingerprint index so scans don't reprocess unchanged files.
- Run **snippet → library**: "find all videos containing snippet X" efficiently.
- Stand up the **bumper catalog** store (per
  [`design/bumper-catalog.md`](design/bumper-catalog.md)): persist bumper entries with
  fingerprints, boundaries, exemplar, and curation metadata.
- Implement **enroll**: promote an identified snippet (manual or auto-discovered) into the
  catalog, running the sub-bumper extent check so stored boundaries are the full bumper.
- Run **video → catalog**: match a video/folder against all active catalog entries
  (audio-first, then visual), producing candidates tagged with the matched entry + confidence.
- Rank/group results by match confidence; flag ambiguous or partial (sub-bumper) matches
  for extra scrutiny.
- **Auto-discovery (stretch):** mine the fingerprint index for segments that recur across
  many files to *propose* catalog entries automatically, without the maintainer identifying
  one first. Cluster recurrences by frequency and position; each cluster becomes an enroll
  candidate. See Approach 6 in
  [`research/matching-approaches.md`](research/matching-approaches.md).
- Use the GPU where it actually helps (decode/feature extraction) and record where it does.

Key decisions: catalog storage (shared vs. separate `catalog.db`); reference media vs.
fingerprints-only; index structure; confidence scoring; auto-queue threshold for catalog hits.

Open questions: memory/time budget for a full pass; near-duplicate promos as variants vs.
distinct entries; catalog-scale matching (linear vs. ANN as the catalog grows).

**Exit criteria:** a repeatable scan returning a scored candidate list for any snippet *or*
any catalog entry; a working catalog you can enroll into and apply against new media.

---

## Phase 4 — Verification UI (pre-cut)

**Goal:** let a human quickly confirm which matches are real before anything is removed.
*(Builds on VDF's Avalonia results UI, but reframed around confirming **cuts** rather than
choosing which duplicate file to delete. Fix the inherited UX problems logged in
[`design/ux-issues.md`](design/ux-issues.md).)*

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

- Trim via FFmpeg with two clear modes (see
  [`design/removal-pipeline.md`](design/removal-pipeline.md)): **Mode A** lossless stream-copy
  (fast, but keyframe-bound cut points) and **Mode B** a single re-encode pass
  (`filter_complex` trim+concat, frame-accurate). Multiple segments removed in **one** ffmpeg
  run in Mode B; multi-step in Mode A.
- Build a **filter-graph builder** so removal and any enabled enhancements (Phase 8) compose
  into a single re-encode command rather than multiple passes.
- Handle all three cases: front trim, end trim, and mid-video interstitial removal.
- **Recompute chapter/scene marks and timestamps** against the post-cut timeline.
- Write outputs to a staging area; never overwrite originals until explicitly confirmed.
- Maintain a **removal manifest** per file (source, snippet start/end, method, output path,
  reversible?, **catalog entry that triggered the cut**) so every cut can be audited and
  undone, and per-bumper statistics can be tallied.

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
- **Verify subtitle/audio/chapter tracks survived** — present, correct format for the
  container, and still in sync after the cut.
- Re-scan trimmed files to confirm the bumper fingerprint is gone.
- Promote validated outputs into the library (replace originals per policy); keep the
  manifest and, if desired, backups.

Key decisions: replacement/backup policy; retention of manifests and staged originals.

Open questions: do we want a one-click undo per file from the manifest?

**Exit criteria:** a closed loop — detect, match, verify, remove, validate, reconcile.

---

## Phase 7 — On-ingest automation & catalog curation

**Goal:** make the catalog pay off continuously — new media is cleaned against known bumpers
with minimal effort, and the catalog stays healthy over time.

Work:

- **On-ingest:** watch a folder or trigger on new media (e.g. a fresh DVD rip) to run a
  **video → catalog** scan automatically, queuing candidates for verification. Cutting still
  requires confirmation per the safety rules — automation gathers candidates, it doesn't
  delete unattended.
- **Catalog curation UI:** rename/label, edit boundaries, merge near-duplicate promos, split
  entries, manage sub-bumper parent/child links, retire obsolete entries.
- **Export / import — personal portability:** let users export the **catalog and fingerprint
  index** as a portable bundle and re-import on another PC, after an OS reinstall, or from a
  backup. This is the higher-priority, lower-risk mode and may include reference clips (same
  owner's data). Needs **path remapping** (or media-root-relative paths) so an imported index
  still resolves to the user's files.
- **Community sharing (stretch, consider later):** share catalogs with other users as
  **derived data only** (fingerprints/labels/boundaries), **never the clip media**, to avoid
  copyright exposure. See [`design/bumper-catalog.md`](design/bumper-catalog.md).
- **Stats:** per-bumper occurrence counts and time saved, from the removal manifest.

Key decisions: how much automation to allow before human review; catalog interchange format.

Open questions: dedupe/merge heuristics for near-identical promos; conflict handling when an
imported catalog overlaps an existing one.

**Exit criteria:** dropping new media in triggers a catalog scan that surfaces verified-removal
candidates, and the catalog can be curated and (optionally) exported/imported.

---

## Phase 8 — Optional per-video enhancements (stretch)

**Goal:** offer optional clean-up transforms applied per video (opt-in, or "when noticed"
during review). The pixel-filter enhancements below **require re-encoding**, so they **compose
into the Mode B re-encode pass** alongside removal — no extra passes (see
[`design/removal-pipeline.md`](design/removal-pipeline.md)). (The one exception is a container
change, which is a stream-copy remux and works even in Mode A.) None run by default; each is a
user choice per video/entry.

Candidate options, with the rough ffmpeg mechanism and any caveats:

- **Fix aspect ratio** — correct SAR/DAR. Note: a wrong *aspect flag* can sometimes be fixed
  without re-encoding (bitstream/container metadata); an actual rescale re-encodes.
- **Crop fixed letterbox bars + burned-in text** — `cropdetect` → `crop` to remove bars and
  any "Now in 4K"/"Now on Blu-ray" text riding on them. (Ties into the letterbox/burned-in-text
  work already noted in matching.)
- **Deinterlace** — `bwdif`/`yadif`.
- **Flip left↔right** — `hflip`.
- **Remove/cover overlaid text or logos** — `delogo` (crude blur of a region); AI inpainting is
  better but heavy/external. Flag as best-effort.
- **Smooth framerate via frame interpolation** — `minterpolate` (slow, artifact-prone);
  dedicated tools (e.g. RIFE) do better as an external step. Flag as heavy/optional.
- **Mark scenes & chapter boundaries** — scene detection to propose cut/chapter points; write
  as container metadata (computed against the post-cut timeline).

Output / transcode options (natural add since Mode B already re-encodes — see
[`design/removal-pipeline.md`](design/removal-pipeline.md)):

- **Change container** (e.g. mp4 ↔ mkv) — a **remux**, so available even in lossless Mode A;
  watch subtitle-format and codec-legality differences between containers.
- **Normalize/clear metadata** — clear or rewrite container tags, e.g. an embedded `title`
  that disagrees with the filename (so players/Jellyfin fall back to the filename). Pure
  metadata edit: stream-copy remux, or **in-place with `mkvpropedit`** for MKV — no re-encode.
  Optionally set `title` = filename (sans extension) in bulk.
- **Switch codec** — H.264 → **H.265/HEVC** or **AV1** (smaller files; slower; preserve
  10-bit/HDR; mind playback compatibility). Re-encode → Mode B.
- **Optimize size/quality** — CRF vs. bitrate target, encoder preset, and GPU (NVENC) vs. CPU
  (x265) quality/speed tradeoff; audio passthrough vs. re-encode. Re-encode → Mode B.
- **Scope guardrail:** offer a *focused* set of output options, not a full HandBrake-style
  transcoder.

Key decisions: per-video vs. per-catalog-entry defaults; which enhancements are worth the
re-encode cost; how much to lean on external tools for the heavy ones (interpolation, inpaint);
how far to take output/transcode options before it becomes scope creep.

Open questions: filter ordering for best quality; how "when noticed" surfaces in the UI
(suggest an enhancement when review detects letterboxing/interlacing/a logo?).

**Exit criteria:** a user can enable one or more enhancements for a video and have them applied
in the same pass as removal, previewable before commit.

---

## Cross-cutting concerns (apply to every phase)

- **Mixed resolution & letterboxing:** the library spans multiple resolutions and some files
  have letterbox bars with **burned-in studio text**. Fingerprinting must normalize for scale
  and crop bars so the same bumper matches across copies; the burned-in text is both a
  matching hazard and a potential bumper signal. This is a first-class requirement, not an
  edge case.
- **Subtitles & secondary streams (first-class):** the maintainer relies on subtitles, so
  subtitle tracks must survive every operation. Preserve all subtitle, audio, and chapter
  streams through remux and re-encode; convert subtitle formats when a container change
  requires it (e.g. mp4 `mov_text` ↔ mkv SRT/ASS) rather than silently dropping them; and
  **verify subtitle presence, format, and sync in post-cut review**. (Container/subtitle
  mismatches are a likely cause of pre-existing subtitle problems in the current library —
  worth checking as we go.)
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
