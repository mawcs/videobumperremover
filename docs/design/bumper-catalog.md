# Design note: the bumper catalog

Detailed design for the persistent bumper catalog decided in
[`../decisions/0004-bumper-catalog.md`](../decisions/0004-bumper-catalog.md). This is a
living design sketch, not a final schema — it captures the shape of the data and the
workflows so we build for them from the start.

## What the catalog is

A curated, persistent store of **known bumpers**, independent of any particular video.
Identify a bumper once and it becomes reusable forever: future videos are matched against the
catalog instead of re-identified from scratch.

Two matching directions coexist:

- **Snippet → library:** given one identified snippet, find every current file containing it.
- **Video → catalog:** given a video (e.g. a fresh rip), match it against *all* known bumpers
  and propose removals. This is the direction the catalog adds.

## Catalog entry (conceptual schema)

Each entry represents one bumper. Fields are grouped by purpose; exact storage TBD.

**Identity & curation**

- `id` — stable unique identifier.
- `label` — human name, e.g. "Disney FBI warning (2003)", "Coming to DVD 2009".
- `category` — `front` | `end` | `interstitial` (a bumper may appear in more than one role).
- `tags` — studio, channel, series, era, etc.
- `notes` — free text.
- `status` — `active` | `retired`.

**Recognition data (the fingerprints)**

- `audio_fingerprint` — Chromaprint-style fingerprint of the bumper's audio.
- `visual_embedding` — DINOv2/ONNX keyframe embedding sequence (audio-less matching,
  letterbox/crop tolerant).
- `phash_sequence` — perceptual-hash frames (cheap first-pass / confirmation).
- `duration` and `canonical_boundaries` — the bumper's true extent (see sub-bumpers below).

**Exemplar (for human verification)**

- `reference_clip` — **decided: store a short reference clip locally** for each entry so the
  user can preview it and manage the catalog without hunting down an original. Optionally
  accompanied by a thumbnail strip for quick scanning. These clips stay **local only** (see
  the sharing note below — they are the part with copyright exposure).

**Relationships**

- `parent_id` / `children` — sub-bumper links (this bumper contains, or is contained by,
  another). Critical for the sub-bumper problem: the catalog records the *full* extent and
  its fragments.
- `variant_group` — near-identical promos grouped as variants of one logical bumper.

**Provenance & stats**

- `source` — where first identified; `date_added`; `occurrence_count` — running tally of
  files it's been found/removed in.

## Storage

- **Fingerprint sampling & data model:** the per-file fingerprint index uses **edge-focused,
  variable-density sampling** (dense at the edges, sparse in the middle) with non-uniform
  `(timestamp, value)` fingerprints and region tags — see
  [`../decisions/0006-edge-focused-fingerprinting.md`](../decisions/0006-edge-focused-fingerprinting.md).
  Catalog bumper entries carry the same fingerprint form plus their begin/end/middle region tag.
- The catalog is a **separate persistent store** from VDF's per-file scan DB — keyed by
  bumper, not by file. Likely a dedicated SQLite DB (`catalog.db`) or a clearly separated set
  of tables. (Open question: combined vs. separate DB.)
- Fingerprints are always stored (compact). A short **reference clip is stored locally** per
  entry (a few MB) beside the DB, for preview and curation.
- The **removal manifest** references the `catalog.id` that triggered each cut, enabling
  audit, undo, and per-bumper stats.

## Workflows

**Enroll** — add a bumper to the catalog.

- Trigger: a human confirms an identified snippet, or accepts an auto-discovery proposal.
- Captures fingerprints + boundaries + exemplar; prompts for label/category/tags.
- Runs the sub-bumper extent check so the stored boundaries are the *full* bumper.

**Apply (catalog scan)** — the reusable payoff.

- Input: a video or folder (e.g. a fresh DVD rip).
- Compare the input's fingerprints against all active catalog entries — **visual presence
  matcher primary** (it covers the silent idents that dominate), with the **audio accelerator**
  confirming *audible* entries cheaply (see [`matcher-spec.md`](matcher-spec.md)) — locate
  offsets, and produce a candidate removal list tagged with which catalog entry matched and at
  what confidence.
- Feed candidates into the verification UI, then the removal engine.

**On-ingest automation (later)** — watch a folder or trigger on new media so rips are checked
against the catalog automatically; still gated by verification before any cut (per safety
rules).

**Curate** — the catalog's ongoing maintenance surface.

- Rename/label, edit boundaries, merge near-duplicate entries, split an entry, manage
  sub-bumper parent/child links, retire obsolete entries.

**Export / import — two distinct modes.** The catalog (and the fingerprint index — see below)
should be portable data, but there are two different use cases with different rules:

- **Personal portability (backup / migration).** A user should be able to export their catalog
  *and* fingerprint index and re-import them on another PC, after an OS reinstall, or from a
  backup. Because it's the same owner's data moving between their own machines, this export
  **may include the reference clips** — nothing is being distributed to others. This is the
  higher-priority, lower-risk mode and should be a real feature, not just a stretch.
- **Community sharing (consider later).** Sharing a catalog with *other people* must export
  **derived data only** (fingerprints, labels, boundaries, categories) and **never the
  reference clip media**. Distributing the actual clips would invite copyright pushback even if
  a fair-use argument exists — rights holders can make that expensive regardless of the merits.
  Fingerprints are derived, non-reconstructive data, so sharing them is far more defensible.

Design implication: the interchange format must support exporting **with** clips (personal) and
**without** clips (shareable), and the index and catalog should ideally export together as a
portable bundle.

**Index portability caveat:** the fingerprint index keys off files, which may live at different
paths on a different machine (different drive letters, mount points, SMB shares). Portable
import likely needs **path remapping** (or storing paths relative to a configurable media root)
so an imported index still resolves to the user's files.

## Precision is the tool's job, not the user's (design principle)

Validated on the Daredevil end-stack (2026-07-15). The user must never need a frame-accurate clip.

- **Enroll from a generous rough region** (e.g. "the last ~20s" of one episode) that contains the
  junk. The distinctive idents in it carry the match (97–98% on Daredevil); black/uniform frames
  are low-information and must **not** be used as the matching signal (a mostly-black clip matches
  black anywhere → false positives).
- **Edge bumpers only need one boundary found.** An end bumper runs to EOF, a begin bumper from
  BOF — so removal is "cut from the content→junk transition to the file edge." The trailing/leading
  **black or silence padding is removed by definition** (it's between the transition and the edge),
  and the *outer* edge never needs marking. The tool finds only the **inner** content→junk boundary
  (boundary-growing / edge detection) and refines it toward frame accuracy.
- **Match on distinctive content; remove the full extent** (including black/silence padding).

Net: rough region in → tool nails the exact cut. No user precision required, ever.

**Interface contract (enforce this in code):** every enrollment/matching entry point — API, CLI,
UI — accepts a **source video path + a time range (or "last/first N seconds")**, never a pre-cut
clip file. The extractor lives inside our code; callers point at a video, we cut and fingerprint.
(Rationale: hand-cut clips were the #1 source of false failures during the matching spike.)

## Matching at catalog scale

- Direction is one-to-many: one file's fingerprints vs. all catalog entries.
- Linear comparison is fine for hundreds of entries; consider approximate-nearest-neighbor
  search only if the catalog grows large.
- The **visual presence matcher is primary** (it covers silent idents); the **audio accelerator**
  keeps per-ingest cost low for *audible* entries. See [`matcher-spec.md`](matcher-spec.md).

## Open questions

- One DB shared with VDF's scan data, or a separate `catalog.db`?
- Variants vs. distinct entries vs. sub-bumpers — where are the lines?
- Auto-queue threshold: how confident must a catalog match be to skip straight to the review
  queue vs. be flagged as uncertain?
- Do we ever auto-*remove* without human review for very high-confidence catalog matches, or
  is verification always required? (Default: always verify, per safety rules.)
