# ADR 0009: Library scan — a persisted, per-library fingerprint database

- **Status:** accepted, implemented and validated
- **Date:** 2026-07-26 (retroactively promoted to an ADR 2026-07-30 — see the note below)
- **Related:** [`0006-edge-focused-fingerprinting.md`](0006-edge-focused-fingerprinting.md)
  (the sampling model this scan builds on), [`0011-cli-library-terminology.md`](0011-cli-library-terminology.md)
  (the `index`→`database` rename, multi-folder `--library`, tombstoning),
  [`0010-database-backed-removal.md`](0010-database-backed-removal.md) (consumes this store),
  [`../iterativeplan.md`](../iterativeplan.md) → "Library scan" (the full 14-decision build log and
  live-verification numbers this ADR summarizes)

> **Note on this ADR's date.** The decisions below were made and the command shipped on
> 2026-07-26, entirely inside `iterativeplan.md`'s "Library scan" entry — no ADR was written at
> the time. This document was written 2026-07-30, after the maintainer flagged that several
> broad, durable topics (scanning, libraries, databases, encoding) had accumulated real decisions
> in the planning log without ever being promoted to the ADR index. It records what was actually
> decided and built, backdated to when that happened, not written prospectively.

## Context

Every matching/removal command (`vbr match`/`vbr remove`, ADR 0007) re-samples the reference clip
and every candidate file **fresh, on every invocation** — a 49-minute episode costs ~21s just to
sample. That's fine for a one-off check, but it means the same library gets re-decoded from
scratch on every single run against it, with no way to make a library "cheap" to query once you've
already looked at it.

VDF's own scan/database mechanism (`ScannedFiles.db`, `FileEntry`) doesn't fit: it's keyed to
VDF's own uniform sampling positions, and this project's fingerprints are non-uniform
(dense-near-edge, sparse elsewhere — ADR 0006). Reusing VDF's store wouldn't actually feed VDF's
own dedup scan, so this needed a wholly separate, dedicated store.

## Decision

1. **New CLI command: `vbr scan`.** Builds/updates one persisted fingerprint cache per named
   library. Samples every candidate file's true edges (dense) and whole-file middle (sparse) up
   front — but doesn't know or check *which* bumper is present; that's a separate, later
   catalog-apply concern (ADR 0004), not this command's job. No `--clip-from`/`--region`/
   `--detection-mode` — there's nothing to match against yet, only fingerprints to gather.

2. **Storage format: MemoryPack (`VersionTolerant`), not SQLite.** `docs/design/bumper-catalog.md`
   had speculated SQLite for a persisted store; what actually got built (here first, then reused
   for the bumper catalog itself — see ADR 0004's amendment) is a MemoryPack-serialized file with a
   magic-header check and atomic temp-file-then-rename save, matching VDF's own `FileEntry`/
   `MediaInfo` convention. No query engine is needed — every lookup is by full key (a file path or
   a whole-database load) — so MemoryPack's simplicity won over SQLite's added complexity for this
   scale.

3. **Sampling model: a third, whole-file-aware sampler.** Unlike ad hoc `match`/`remove`, a scan
   doesn't know which edge (if any) a bumper lives at — so `WholeFileSampler` decodes dense zones
   near **both** true edges plus one sparse, keyframe-only pass across the entire file, merging all
   three into one timestamp-sorted `TimedFingerprint[]` per file. Because this sampler always
   probes the file's real duration up front, its timestamps are **absolute** (seconds from true
   BOF) — unlike the ad hoc `MixedDensitySampler`/`TimedFrame` path (ADR 0006), whose timestamps
   are window-relative. This distinction matters later (see ADR 0010).

4. **Change detection: content hash first, not just mtime/size.** Uses VDF's own OsHash content
   hash; a file whose bytes are unchanged skips re-sampling entirely even if its mtime changed
   (e.g. after a copy to a new drive) — verified live: an unchanged re-scan drops from ~21s to
   0.16s, and a touched-mtime-same-content file stays at 0.16s too (only a genuine content change,
   verified with a real 1-byte append, forces a full re-sample).

5. **Checkpointing.** Periodic incremental saves during a scan, not only at the end — so an
   interrupted long scan loses at most the last checkpoint interval, not the whole run's work.

6. **One physical database file per named library**, not one global store — matches the mental
   model that a library is a user-chosen, independently-scoped collection. Storage location and
   naming mirror the bumper catalog's own later pattern (a dedicated per-library-name file under a
   VBR-specific state folder, folder overridable via `--library-db-folder`).

7. **`.vbr.`-output files excluded from a scan by default.** A prior `vbr remove` run's
   transitional sibling output is not real library content and shouldn't be indexed as if it were
   — `--include-vbr-outputs` opts back in when explicitly wanted.

8. **Adaptive sparse-frame cap**, sized from the file's own probed duration rather than a fixed
   constant — a long file's whole-file sparse pass is never silently truncated the way a fixed cap
   would risk.

9. **Sequential per-file scanning for v1** — no parallelism. Simplicity first; revisit only if
   real-world scan throughput becomes an actual bottleneck.

10. **Independently-tunable reporting**: `--console-info`/`--log-file`/`--log-level` (five tiers:
    quiet/info/debug/verbose/trace), decided and built after an early, simpler `--verbose`-only
    scheme produced enough console output to overflow a real terminal's scrollback on a 102-file
    run. A quiet console with a fully-detailed log file is the out-of-the-box default.

## Consequences

Positive: once a library is scanned, matching against it (via ADR 0010's database-backed `remove`)
touches no decode at all — a scanned+cataloged run is near-instant for matching, with only the
actual removal cut doing any ffmpeg work. Re-scanning an unchanged library is nearly free.

Negative / watch-outs: a second persisted store now needs to stay consistent with the library it
describes — a file moved out from under a database isn't automatically detected as "the same
content, new location" (see the deferred library-root-move gap below). A scan's own sampling
parameters (`--edge-boundary`/`--sample-interval`/`--sparse-interval`) are baked into the resulting
database; nothing warns or re-validates if a later query wants different density than what was
actually scanned — the caller gets whatever coverage exists at whatever positions were sampled,
silently thinner than requested if the parameters don't match.

## Open questions

- **Library-root-move invalidation.** If a whole library folder is moved/renamed, every entry's
  `Path` key goes stale. Three options were weighed (re-sample from scratch; remap paths by
  relative position; re-link by content hash) — **deferred: re-sample from scratch is v1
  behavior**, not designed further here.
- **No enforcement when a query's requested density exceeds what a database actually holds** — a
  caller asking for a wider/denser search than the scan's own `--edge-boundary`/intervals provided
  silently gets partial (sparse-only, or missing) coverage in that zone rather than an error or
  warning.
- **Tombstoning and its eventual consumers** (re-linking a reappeared file, pruning ghost entries)
  — see [ADR 0011](0011-cli-library-terminology.md); this ADR's database format carries the field,
  nothing reads it yet.
