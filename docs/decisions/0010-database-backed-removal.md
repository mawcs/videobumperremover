# ADR 0010: Database-backed bumper removal — `remove` reads from a scanned library and/or a bumper catalog

- **Status:** accepted, implemented and live-verified against real media (all four source
  combinations)
- **Date:** 2026-07-29 (retroactively promoted to an ADR 2026-07-30 — see the note on
  [ADR 0009](0009-library-scan-database.md), same situation)
- **Related:** [`0007-removal-command.md`](0007-removal-command.md) (the baseline `remove` command
  this directly extends — its Decision 1 explicitly named this "future work"),
  [`0009-library-scan-database.md`](0009-library-scan-database.md) (the database this reads),
  [`0004-bumper-catalog.md`](0004-bumper-catalog.md) (the catalog this reads),
  [`../iterativeplan.md`](../iterativeplan.md) → "Utilizing Databases" (full implementation
  write-up, timestamp-origin analysis, and live-verification numbers)

## Context

ADR 0007's `remove` always re-samples the reference clip from `--clip-from` and every candidate
file fresh, on every invocation — even once a library has already been scanned (ADR 0009) or a
bumper has already been enrolled in a catalog (ADR 0004). ADR 0007's Decision 1 explicitly flagged
this: *"Catalog-aware and index-aware variants ... are explicitly future work."* This ADR is that
work.

## Decision

1. **Two independent sources on each side of a `remove` invocation, freely mixable.**
   - **Reference bumper:** ad hoc (`--clip-from`/`--region`/`--clip-length`, sampled fresh — the
     ADR 0007 baseline, unchanged) **or** a named catalog entry (`--bumper-label`, optionally
     `--catalog-name`/`--catalog-db-folder`) — its stored fingerprints and audio fingerprint are
     reused as-is, never re-extracted; its own `Region`/`Duration` are used instead of
     `--region`/`--clip-length`, which are invalid together with `--bumper-label`.
   - **Candidates:** an ad hoc `--library`/`--file` folder walk (the ADR 0007 baseline, unchanged)
     **or** a scanned library database (`--library-name`, optionally `--library-db-folder`) — every
     entry's stored fingerprints and audio fingerprint are reused as-is, never re-scanned; missing
     (tombstoned, see ADR 0011) entries are skipped rather than erroring the whole run.

   This is a genuine 2×2: ad hoc/ad hoc (today's original behavior), ad hoc library + catalog
   bumper, database library + ad hoc bumper, and database library + catalog bumper (the fully
   cached case). All four were built and live-verified against the same real Netflix-card test
   case.

2. **Mutual exclusivity, enforced explicitly (not via the CLI parser's declarative
   `Required`).** `--bumper-label` is invalid together with `--clip-from`/`--region`/
   `--clip-length`; `--library-name` is invalid together with `--library`; `--catalog-db-folder`
   requires both `--bumper-label` and `--catalog-name`; `--library-db-folder` requires
   `--library-name`. Exactly one reference source and exactly one candidate source must be given.
   `--clip-from`/`--region`/`--clip-length` lost their shared `Option.Required = true` (a single
   `Option` instance is shared with `match`/`add-bumper`, so a per-command "sometimes optional"
   rule can't live on the option itself) — replaced with an explicit check in each of the three
   commands' own actions, preserving `match`/`add-bumper`'s original unconditional requiredness.

3. **No re-extraction, no re-scan — the actual guarantee, not just a naming convention.** When the
   reference is a catalog entry, its `Fingerprints`/`AudioFingerprint` are mapped directly into the
   matcher's input types, no ffmpeg/ONNX call. When candidates come from a database, each entry's
   `Fingerprints` are filtered to the requested search window by **absolute** timestamp (using the
   entry's own already-probed `Duration` — no ffprobe call needed either) and its
   `AudioFingerprint` is used directly, no decode. A fully-cached run (catalog bumper + database
   library) touches **zero** ffmpeg/ONNX work for matching — confirmed live by the complete absence
   of `[mixed-density]`/AI-download log lines in that combination, versus their presence in every
   combination with at least one ad hoc side.

4. **Mixing a window-relative reference against an absolute-timestamp candidate (or vice versa)
   is safe, and was reasoned through explicitly rather than silently assumed.** A catalog entry's
   `Fingerprints` (ADR 0004) are window-relative (seconds from the start of the sampled region,
   the same convention ad hoc `MixedDensitySampler` uses); a database entry's `Fingerprints`
   (ADR 0009) are absolute (seconds from true BOF, since `WholeFileSampler` always knows the real
   file duration). Presence matching (`VisualBumperMatcher.MatchMixedDensity`/
   `MatchMixedDensityPHash`) never requires temporal alignment between the two sides being compared
   — it only asks whether a reference position's content appears *somewhere* in the candidate — so
   mixing origins is exactly as valid as any other pairing. The one place the distinction matters
   is filtering a database entry down to "the requested search window," which has to reason in
   that entry's own absolute-from-BOF terms.

5. **Audio fingerprinting was restructured to compute the reference's fingerprint once per run,
   not once per candidate.** The pre-existing `AudioBumperMatcher.Match` re-extracted the
   reference clip's own Chromaprint fingerprint on every single candidate call — harmless waste at
   ad hoc scale, but exactly backwards for this ADR's "don't redo cached work" goal. Added a static
   `AudioBumperMatcher.MatchFingerprints(clip, file, region, minSimilarity)` taking two
   already-computed fingerprints (same relationship `VisualBumperMatcher.MatchMixedDensity` already
   has to its own instance `Match` method); the reference's fingerprint is now extracted/reused
   exactly once regardless of which of the four combinations is active.

## Consequences

Positive: a scanned-and-cataloged workflow makes matching across a whole library near-instant,
independent of file count or length — only files that actually match pay any decode cost at all
(for the removal cut itself, which is unavoidable). The audio restructuring is a strict efficiency
win even for the original ad hoc/ad hoc combination, not just the new modes.

Negative / watch-outs: two more persisted-state freshness assumptions now feed an operation that,
while still non-destructive itself (ADR 0007's `.vbr.` sibling-output guarantee is unchanged), acts
on stale data with no re-validation — a database entry for a file that's changed on disk since its
last scan is used exactly as cached, with no check against the current file. `--no-recurse` and
most sampling-density flags become inert (silently, with a one-time note) once a given side is
database/catalog-sourced, since there's nothing left to sample.

## Open questions

- **`--file` + `--library-name` (look up one specific file's cached database entry) was
  deliberately not built.** The plan text only forbids `--library-name` with `--library`, not with
  `--file`; a single-entry-lookup mode would be a reasonable extension but is a distinct code path
  nothing in the current request actually asked for. Scoped out as speculative surface, not
  designed.
- **No warning when a database's own scan density is narrower than the requested
  `--search-length`** — same open gap ADR 0009 already flags for query-side callers generally.
