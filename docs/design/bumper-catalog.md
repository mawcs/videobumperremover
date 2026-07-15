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
- Compare the input's fingerprints against all active catalog entries (audio-first, then
  visual for audio-less cases), locate offsets, and produce a candidate removal list tagged
  with which catalog entry matched and at what confidence.
- Feed candidates into the verification UI, then the removal engine.

**On-ingest automation (later)** — watch a folder or trigger on new media so rips are checked
against the catalog automatically; still gated by verification before any cut (per safety
rules).

**Curate** — the catalog's ongoing maintenance surface.

- Rename/label, edit boundaries, merge near-duplicate entries, split an entry, manage
  sub-bumper parent/child links, retire obsolete entries.

**Import / export / share (future)** — a catalog is portable data. Under the project's
open-source license, users could share community bumper catalogs — but **share derived data
only** (fingerprints, labels, boundaries, categories), **never the reference clip media**.

The reasoning: distributing the actual bumper clips would invite copyright pushback even if a
fair-use argument exists — rights holders have the resources to make that expensive regardless
of the merits. Fingerprints are derived, non-reconstructive data, not the copyrighted work, so
sharing them is a far more defensible position. Reference clips therefore stay local; the
interchange format must be able to export a catalog *without* them. (Still a "consider later,"
not a committed feature.)

## Matching at catalog scale

- Direction is one-to-many: one file's fingerprints vs. all catalog entries.
- Linear comparison is fine for hundreds of entries; consider approximate-nearest-neighbor
  search only if the catalog grows large.
- Audio-first keeps per-ingest cost low; visual embedding pass covers audio-less bumpers.

## Open questions

- One DB shared with VDF's scan data, or a separate `catalog.db`?
- Variants vs. distinct entries vs. sub-bumpers — where are the lines?
- Auto-queue threshold: how confident must a catalog match be to skip straight to the review
  queue vs. be flagged as uncertain?
- Do we ever auto-*remove* without human review for very high-confidence catalog matches, or
  is verification always required? (Default: always verify, per safety rules.)
