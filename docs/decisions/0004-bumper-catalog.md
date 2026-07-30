# ADR 0004: Persistent bumper catalog (not ephemeral snippets)

- **Status:** accepted
- **Date:** 2026-07-15
- **Implementation status (2026-07-29):** write side built — `vbr add-bumper` (`VBR.Core.Catalog`),
  planned in detail across three maintainer feedback rounds then implemented and live-verified the
  next day. First read-only surface shipped too: `vbr list-bumpers` (2026-07-29). **Video →
  catalog (Apply), the direction this ADR actually motivates, is still not built** — nothing scans
  a video against the whole catalog yet; `remove` can only target one named entry at a time (see
  [ADR 0010](0010-database-backed-removal.md)). See `../iterativeplan.md` → "Bumper catalog" and
  "Bumper CRUD Part 1" for the full plan and implementation write-ups.
- **Related:** [`../design/bumper-catalog.md`](../design/bumper-catalog.md) (data model &
  workflows), [ROADMAP](../ROADMAP.md), [`0009-library-scan-database.md`](0009-library-scan-database.md)
  (the sibling store whose storage pattern this catalog ended up copying),
  [`0010-database-backed-removal.md`](0010-database-backed-removal.md),
  [`0011-cli-library-terminology.md`](0011-cli-library-terminology.md) (catalog-scoped label
  uniqueness)

> **Amendment (2026-07-28) — v1 storage format and schema, as actually built.** This ADR's own
> Open Questions and `design/bumper-catalog.md` both left storage technology open, speculating
> "likely a dedicated SQLite DB." What actually shipped, once `vbr scan` (ADR 0009) had already
> solved the same class of problem for per-file fingerprints, is **MemoryPack**
> (`[MemoryPackable(GenerateType.VersionTolerant)]`, magic-header-checked, atomic
> temp-file-then-move save, per-OS default state folder) — no SQLite anywhere in this codebase.
> Answers this ADR's first Open Question directly: **not** one combined DB with a library's own —
> the catalog is a separate `.vbrcat` file, at its own dedicated `--catalog-db-folder`, a sibling
> of (not shared with) a library database's own folder.
>
> **Per-library, not global.** This ADR's "independent of any particular video" framing was first
> misread during planning as "independent of any particular library" — corrected: each library
> gets its own catalog, mirroring `vbr scan`'s own per-library database. (A later, separate
> simplification, 2026-07-28, went further: a catalog is named directly via `--catalog-name`, with
> no `--library`/folder argument at all — independent of any specific media folder, not just
> multiple-libraries-capable. See `iterativeplan.md`'s "Bumper catalog" entry, "Post-ship
> simplification" section.)
>
> **Schema, relative to this ADR's own original framing:** `Category` → `Region`, reusing
> `VBR.Core.Extraction.ClipEdge` directly (the stored value *is* whatever `--region` was passed at
> add time — no separate inference/category-mapping step) rather than a free string; consequence:
> no interstitial (mid-video) bumper representation exists yet, since `ClipEdge` itself is
> `{ begin, end }` only — deferred until interstitial removal itself is designed, a broader,
> cross-cutting change beyond just this catalog. `Notes` → `Description` (a straight rename, same
> purpose). `Id` stays a GUID, `Label` is the human-facing lookup key (settles this ADR's second
> Open Question in the direction its own reasoning already pointed: if `Id` is a GUID, `Label` is
> the only user-friendly way to specify which bumper you mean) — enforced unique **within one
> catalog**, not globally (ADR 0011).
>
> **Thumbnail** (not originally speculated in this ADR at all): embedded as bytes directly in the
> catalog entry, at original/native decoded resolution, no resize — asymmetric with the reference
> clip, which stays a separate sibling file (a still frame is small enough that embedding costs
> little and avoids orphan-file bookkeeping; a video clip is not).
>
> **Fingerprint representation, confirmed rather than newly decided:** a catalog entry reuses
> `VBR.Core.Fingerprinting.TimedFingerprint` directly (embedding + pHash, timestamp-tagged) — this
> ADR's own "audio_fingerprint, visual_embedding, phash_sequence, duration" framing turned out to
> be exactly that type, already built for the library database. One real, load-bearing difference
> from the library database's own fingerprints, worth recording precisely: a catalog entry's
> `Fingerprints` are **window-relative** (seconds from the start of the sampled clip region), not
> absolute-from-file-start the way `WholeFileSampler`/ADR 0009's are — see ADR 0010's Decision 4
> for why mixing the two is still safe for presence matching.
>
> **Duplicate/variant detection at add time and sub-bumper relationships remain deferred**, per
> this ADR's own original scope — the amendment above only concerns storage/schema, not the
> curation workflows this ADR describes as future work. Still unbuilt: "Apply" (video → catalog
> matching, the actual payoff this ADR is named for), curation (rename/edit/merge/retire — a first,
> narrow read-only step, `vbr list-bumpers`, exists as of `iterativeplan.md`'s "Bumper CRUD Part 1"
> entry, 2026-07-29), export/import, community sharing.

## Context

The original mental model treated a bumper snippet as **ephemeral**: identify a clip → scan
the library for it → remove it everywhere → approve → delete originals → forget the clip.
Every future cleanup starts from zero.

But bumpers recur *over time*, not just across the current library. The same studio/channel
promos reappear on the next DVD rip, the next batch of downloads, and so on. Re-identifying a
bumper you already dealt with last month is wasted effort.

## Decision

Make bumpers **first-class, persistent entities** stored in a curated **bumper catalog**
(a.k.a. gallery). A catalog entry captures everything needed to recognize and remove a
bumper again later — independent of any particular video — so identifying a bumper once makes
it reusable forever.

This introduces a new, primary matching direction alongside the original one:

- **Snippet → library** (original): given one identified snippet, find every current file
  containing it.
- **Video → catalog** (new): given a video (e.g. a fresh DVD rip), match it against *every*
  known bumper in the catalog and propose removals automatically.

The ephemeral flow isn't removed — it becomes the front half of **enrollment**: identifying a
bumper now *adds it to the catalog* instead of discarding it.

## Core workflows this enables

- **Enroll:** promote an identified snippet (from manual identification *or* auto-discovery)
  into the catalog as a durable, labeled entry.
- **Apply (catalog scan):** scan any video/folder against the whole catalog; queue matches
  for verified removal. This is the reusable payoff — new rip → auto-match → clean.
- **On-ingest automation (later):** watch a folder or trigger on new media so fresh rips are
  checked against the catalog without manual steps.
- **Curate:** name/label entries, edit boundaries, merge near-duplicate promos, manage
  sub-bumper (parent/child) relationships, retire obsolete entries.
- **Export / import (two modes):**
  - *Personal portability* (backup, new PC, OS reinstall): export/import the catalog **and the
    fingerprint index** as a portable bundle — **may include reference clips** since it's the
    same owner's data. Higher priority, lower risk.
  - *Community sharing* (consider later): export **derived data only**
    (fingerprints/labels/boundaries), **never the reference clip media**, to avoid copyright
    exposure.
  - Caveat: index portability likely needs **path remapping** (or media-root-relative paths)
    since files live at different paths on a different machine.

## Impact on the VDF-based design

- The catalog is a **new persistent store** separate from VDF's per-file scan database — it
  holds *reference* fingerprints (audio fingerprint, DINOv2 embeddings, pHash, canonical
  boundaries) plus curation metadata and an exemplar preview, keyed by bumper, not by file.
- Matching **reuses VDF's fingerprint primitives** but in a one-to-many direction (a file's
  fingerprints vs. all catalog entries). Linear comparison is fine for hundreds of entries;
  revisit approximate-nearest-neighbor search only if the catalog grows large.
- The **removal manifest** gains a link to the catalog entry that triggered each cut — for
  audit, undo, and per-bumper statistics ("removed from 214 files").

## Consequences

Positive: identify-once/remove-forever; enables automated on-ingest cleaning; auto-discovery
has a natural home (its proposals become catalog candidates); shareable catalogs are possible.

Negative / watch-outs: more persistent state to design, migrate, and back up; curation UX
becomes a real surface (merge/split, sub-bumper relations); catalog matching adds a per-ingest
cost that grows with catalog size; storing reference clips means the catalog holds copyrighted
snippets (fine locally, but a hard constraint on any future sharing — see below).

**Decided:** store a short **reference clip locally** per entry (for preview/curation). It
stays local; only derived data may ever be shared.

**Copyright note (sharing):** distributing reference clips would draw copyright pushback even
if fair use applies — rights holders can make that expensive regardless of merit. Fingerprints
are derived, non-reconstructive data, so a shared catalog must be exportable *without* clip
media. Kept on the "consider later" list, not committed.

## Open questions

- ~~One combined SQLite DB with VDF's, or a separate `catalog.db`?~~ **Resolved — see the
  2026-07-28 Amendment above:** neither. MemoryPack, its own dedicated `.vbrcat` file/folder,
  never shared with VDF's own database or the library database (ADR 0009).
- How to model variants (near-identical promos) vs. distinct entries vs. sub-bumpers?
- Confidence threshold for *auto*-queuing a catalog match vs. requiring human review.
