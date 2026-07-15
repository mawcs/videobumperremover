# ADR 0004: Persistent bumper catalog (not ephemeral snippets)

- **Status:** accepted
- **Date:** 2026-07-15
- **Related:** [`../design/bumper-catalog.md`](../design/bumper-catalog.md) (data model &
  workflows), [ROADMAP](../ROADMAP.md)

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
- **Import / export / share (future):** a catalog is portable data; open-source users could
  share community bumper catalogs — but **derived data only** (fingerprints/labels/boundaries),
  **never the reference clip media**, to avoid copyright exposure.

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

- One combined SQLite DB with VDF's, or a separate `catalog.db`?
- How to model variants (near-identical promos) vs. distinct entries vs. sub-bumpers?
- Confidence threshold for *auto*-queuing a catalog match vs. requiring human review.
