# ADR 0011: CLI/library terminology, multi-folder libraries, and the `cleanup`→`commit` rename

- **Status:** accepted, implemented
- **Date:** 2026-07-28 (retroactively promoted to an ADR 2026-07-30 — see the note on
  [ADR 0009](0009-library-scan-database.md), same situation)
- **Related:** `docs/design/clarification-terms-cli.md` (the maintainer's three-round UX
  terminology analysis this ADR is grounded in), [`0008-cleanup-command.md`](0008-cleanup-command.md)
  (carries only a one-line pointer note for the rename this ADR actually explains),
  [`0009-library-scan-database.md`](0009-library-scan-database.md) (the `index`→`database` rename),
  [`0004-bumper-catalog.md`](0004-bumper-catalog.md) (catalog-scoped label uniqueness),
  [`../iterativeplan.md`](../iterativeplan.md) → "CLI terminology & multi-folder libraries" (full
  sequencing plan, phase-by-phase implementation log, and live-verification numbers)

## Context

Three rounds of UX terminology review (`clarification-terms-cli.md`) surfaced real, user-facing
confusion baked into the CLI's existing vocabulary:

- **"Index" is technically precise but tested as confusing** relative to "database" for describing
  a persisted, per-library fingerprint cache (ADR 0009).
- **"Library" meant three different things** depending on context: a user's own mental media
  collection (unpersisted, potentially spanning multiple folders); a `vbr scan`-persisted, named
  database tied to one folder tree; and a bumper catalog's loose, non-durable association with
  *some* media collection (not necessarily a scanned one, and not durably tied to any one).
  Collapsing these under one word made it easy to design a feature (e.g. `add-bumper`'s original
  `--library`/`--library-name` pair) that accidentally implied a tighter coupling than was ever
  intended.
- **"Cleanup" collided with vocabulary this project will need for itself.** VDF's own menu already
  has both "Cleanup Database" and "Prune Ghost Entries" — database-maintenance operations this
  project's own database (ADR 0009) will eventually need equivalents of. `vbr cleanup`
  (ADR 0008's promote-verified-output command) had already claimed that word for something
  unrelated.

## Decision

1. **`index` → `database`/`db`, full internal rename, not just CLI-facing.** `VBR.Core.Index` →
   `VBR.Core.Database`; `LibraryIndex(Entry/Store/Key)` → `LibraryDatabase(Entry/Store/Key)`;
   `.vbridx` → `.vbrdb`; magic header `VBRIDX01` → `VBRDB001`; every CLI flag/help-text/log-line
   mention updated to match. Chosen as a full rename (not just a CLI alias) for consistency with
   this project's own established practice of keeping internal vocabulary matching external
   vocabulary (`CatalogName`/`--library-db-folder`/`--catalog-db-folder` already did this). No
   behavior change — confirmed via unaffected test count and a live smoke test of the new
   extension/header/default-folder resolution.

2. **`--library` becomes a semicolon-delimited list, not a single folder — a library can span
   multiple parent folders.** New, parallel `--exclude-folders` flag (mirroring VDF's own GUI's
   own include/exclude-folders concept) — a path-prefix rule, deliberately independent of the
   existing `.vbr.`-output filename filter (both stay, checked separately). The same physical
   folder may legitimately appear in more than one library's `--library` list across separate runs
   — nothing checks for or prevents that cross-library overlap; only within-one-run overlap
   (e.g. a nested folder pair) is deduplicated, since duplicate candidates within a single run would
   otherwise be sampled/matched twice.

3. **`vbr cleanup` renamed to `vbr commit`; the `clean` alias dropped, not kept or repointed.**
   Frees "cleanup"/"clean"/"prune" for a future phase of VDF-style database-maintenance commands
   (mirroring "Cleanup Database"/"Prune Ghost Entries") once that work is designed. A CLI-surface
   rename only — `VBR.Core.Cleanup.LibraryCleaner` and its internal vocabulary are unchanged, since
   "cleaner"/"cleanup" as engine-internal terms describing what the algorithm does remain accurate
   regardless of what CLI verb exposes it (see [ADR 0008](0008-cleanup-command.md)'s own top-of-file
   amendment note, which records this rename but not the reasoning — this ADR is that reasoning).

4. **Tombstoning: a scanned database stops discarding data the moment a file goes missing.**
   `LibraryDatabaseEntry` gained a nullable `TombstonedUtc` — set (not removed from the database)
   the moment a scan finds a previously-known file's path no longer exists, cleared automatically
   the moment a scan finds the *same path* present again. Purely additive for now: nothing reads
   this field yet (no re-linking a reappeared file at a *different* path, no "prune ghost entries"
   command) — this decision is narrowly "keep the fingerprints instead of throwing them away,"
   laying groundwork for both of those future features without committing to either one's design.
   [ADR 0010](0010-database-backed-removal.md)'s database-backed `remove` already relies on this:
   tombstoned entries are skipped as candidates rather than causing an error.

5. **Bumper-label uniqueness is scoped to one catalog, not global.** Two different catalogs may
   each have their own bumper labeled e.g. "Studio ident" without conflict — checked in
   `add-bumper` before any sampling work starts (a cheap check ahead of the expensive one, same
   principle as this project's other upfront validation guards).

6. **Terminology recap, for future reference** (not new vocabulary introduced by this ADR, but the
   settled definitions everything above depends on): **Library** — a user's own conceptual media
   collection; purely mental until named, may span multiple folders, not itself a stored artifact.
   **Named Library** — internal-team term only, never user-facing; what you get once a user
   supplies a library name correlating to a folder set, so the team can talk precisely about "the
   thing a database corresponds to." **Library Database** — the persisted, named, single-file
   fingerprint cache (ADR 0009); correlates to exactly one named library, but the folder set itself
   is never stored anywhere except inside this file. **Bumper Catalog** — deliberately
   uncorrelated to any one library or database; a catalog built against one media collection can be
   applied to a different one later.

## Consequences

Positive: CLI vocabulary now matches VDF's own established menu language, reducing future
collision risk as database-maintenance commands get designed; multi-folder libraries match how
real users actually organize media (e.g. ripped discs and downloaded clips kept in physically
separate folders but conceptually one library); tombstoning costs almost nothing today and avoids
a future migration once re-linking/pruning is designed.

Negative / watch-outs: the `index`→`database` rename touched a lot of surface area for zero
functional change — pure churn, accepted deliberately for long-term clarity rather than deferred
indefinitely; tombstoned entries accumulate in a database with no automatic cleanup yet (no size
concern at today's scale, but worth remembering); the same-folder-in-multiple-libraries allowance
means a user can create genuinely inconsistent state (two libraries disagreeing about a file)
with no guard rail — accepted as the user's own choice, not this project's to prevent.

## Open questions

- **Whether `vbr scan`'s own `--library`/`--library-name` naming deserves the same "library" vs.
  "named library" scrutiny this ADR gave `add-bumper`'s now-removed pairing** — raised, not
  resolved; explicitly out of scope for this pass per the maintainer's own instruction not to touch
  `scan` at the time.
- **The Phase 3 database-maintenance commands** ("Cleanup Database"/"Prune Ghost Entries"
  equivalents) this rename frees vocabulary for — sketched as a future direction, not designed.
- **Re-linking a tombstoned entry that reappears at a new path** — the actual payoff tombstoning
  sets up for — not designed; nothing consumes `TombstonedUtc` yet beyond ADR 0010's
  skip-if-tombstoned check.
