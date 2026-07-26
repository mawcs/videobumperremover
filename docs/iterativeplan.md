# Iterative Plan Document

This document catalogs planning concepts as we iterate in development. Newest plan goes at the
top, under its own second-level heading; older plans stay below under theirs, kept for historical
reference rather than deleted or overwritten.

## Library scan — implemented and validated (2026-07-26)

**Status: implemented and validated.** Built exactly to the plan below (all 14 decisions), then
verified live against real media, not just unit tests. One real bug found and fixed along the way
(details below) — otherwise built clean on the first compile pass per component.

**New (`VBR.Core`):** `Fingerprinting/TimedFingerprint.cs` (the merged embedding+pHash point type),
`Fingerprinting/WholeFileSampler.cs` (Step 1's three-pass merge), a `SampleKeyframes` overload on
`Fingerprinting/DenseFrameSampler.cs` (keyframe-only decode, sharing that class's existing
process-orchestration code rather than duplicating it), `Index/LibraryIndex.cs`/
`Index/LibraryIndexEntry.cs` (MemoryPack `VersionTolerant` classes, matching VDF's own
`FileEntry`/`MediaInfo` convention exactly), `Index/LibraryIndexStore.cs` (path resolution +
magic-header-checked load/save with atomic temp-file-then-move), `Index/LibraryScanner.cs` (Step 3's
orchestration), `Index/MemoryPackRegistration.cs` (AOT-safe formatter registration, mirroring VDF's
own). **New (`VBR.CLI`):** `Commands/ScanCommand.cs`, registered in `Program.cs`.

**A real bug, caught by writing the checkpoint test, not by inspection:** the first implementation
put the checkpoint-save check textually after the per-file `try`/`catch`, but the skip-unchanged and
file-vanished paths both `continue` from inside the `try` — which jumps straight to the next loop
iteration, never reaching code placed after the block. Checkpointing silently never fired for
skipped-unchanged files — the *common* case on any re-scan, exactly backwards from decision 7's
intent (interrupt-safety matters most on long, mostly-cached re-scans). Fixed by moving the check
into a `finally` (always runs, `continue` or not). A `LibraryScanner.Scan` unit test with
`checkpointInterval: TimeSpan.Zero` over two cache-hit files caught this immediately (asserted 3
checkpoint calls, got 1) — real content/AI-model-free, so it ran in milliseconds.

**Tests:** 19 new, all passing — `LibraryIndexStoreTests` (round-trip serialization including
`TimedFingerprint[]`/`AudioFingerprint`/full header, atomic-save, wrong-magic rejection, name/path
derivation), `LibraryScannerTests` (skip-unchanged, OsHash-verified timestamp-only reuse,
force-rescan bypass, size-change triggers resample, missing-file drop, one-failure-doesn't-stop-
the-run, checkpoint cadence), `DenseFrameSamplerKeyframeTests` (keyframe-only decode plumbing
against a synthetic clip — see that file's comment on why `-g 10` was needed: a solid-color lavfi
source has no scene changes, so libx264's default GOP would otherwise place a single keyframe for
the whole clip). Plus `LibraryScannerEquivalenceTests` (env-var-gated, real media — verification
item 7): scans a real library, pulls the *scanned, persisted* fingerprints back out filtered to the
edge window, and confirms they reproduce live `vbr match`-quality presence numbers.

**Live-verified through the built `vbr scan` CLI** against real media (`test_materials/`):

| Verification (plan item) | Result |
|---|---|
| 1. Real episode end to end | 49-minute Caprica episode → 738 raw sparse samples (120 usable) + 100 begin-edge (62 usable) + 100 end-edge (15 usable) = 197 merged fingerprints; duration probed correctly (00:49:14); ~21s first sample |
| 2. Re-scan, nothing changed | 0.16s (vs. 21s) — no decode, no AI-model reload |
| 3. Touched mtime, same content | Still 0.16s — `OsHash` correctly proves the bytes are unchanged |
| 4. Genuine content change (1-byte append) | Re-samples in full (~21s), same as a fresh file |
| 5. File far shorter than 2×edge-boundary | A real 5.256s clip → 35 fingerprints (1 sparse + 18 begin + 16 end), no crash from the fully-overlapping zones; a no-audio clip in the same run confirmed `ChromaprintEngine` returning null gracefully too |
| 6. `.vbr.` exclusion | Default: 1/2 files candidates (excluded); `--include-vbr-outputs`: 2/2, with the original correctly still cache-hit and only the new `.vbr.` path freshly sampled |
| 8. Adaptive frame cap | The 49-minute episode's 738-sample sparse pass alone proves this — the old fixed 400 cap would have silently truncated it |
| 9. Checkpointing (unit-level; see the bug above) | `onCheckpoint` fires once per file plus a final save, confirmed via the fixed test |
| 10. `--rescan`/`--force` | Re-sampled an already-cached, unchanged file on demand |
| 11. Progress UX | Confirmed both: `--verbose` per-file `Logger` lines + `SAMPLED`/`SKIPPED` rows; default a single in-place `\r`-updated counter line |
| 12. Per-library index isolation | Two libraries, default (non-explicit) `--library-name`-derived paths, resolved to `LibA.vbridx`/`LibB.vbridx` under the same dedicated folder — distinct sizes, no cross-talk |
| 7. Equivalence (scanned fingerprints vs. live match) | **Confirmed.** Full Caprica corpus (19 files) scanned via `LibraryScanner`; the clip episode's *persisted* end-edge fingerprints (15, pulled back out of the index) fed into `VisualBumperMatcher.MatchMixedDensity`/`MatchMixedDensityPHash` against every other scanned entry's own persisted edge fingerprints — **18/18 MATCH, bestCos 92–100%** (dino), reproducing (and on the floor, slightly beating) the primitive-level `vbr match`/`MixedDensitySampler` numbers recorded earlier this session (93–100%). pHash: present=0/15 on 17/18 (bestSim 62–72%), present=10/15 on the one literal duplicate episode (E01 pt2, 100%) — consistent with pHash's already-established weak standalone performance on this exact bumper, not a new finding. 8m41s total (19 real decodes, no shortcuts) — `dotnet test VBR.Tests --filter "FullyQualifiedName~LibraryScannerEquivalenceTests"`. |

**Not yet re-verified live in this pass:** true crash-and-resume (unit-tested via the `onCheckpoint`
hook; a real multi-minute Ctrl+C-mid-scan trial is a manual follow-up, not required to trust the
mechanism given the unit coverage).

### Spike plan (2026-07-24, for reference — see above for what actually shipped)

Written up before writing any code, per the maintainer's request, and iterated across several
rounds of open questions before implementation started — mirrors how the mixed-density spike was
planned before being built.

### The problem, precisely

`PROGRESS.md`'s open item is specifically "Edge-focused scan + a **cached** fingerprint/embedding
index (scan once, compare cheaply)" — separate from the still-untouched "**Catalog**" item. Today,
`vbr match`/`vbr remove` re-extract and re-sample every candidate file from scratch on every
invocation; nothing persists across runs. This spike builds the persisted, per-file fingerprint
cache — **not** the bumper catalog, not `vbr enroll`, not automatic bumper identification. Those
stay separate, already-tracked, still-open items (see "Explicitly out of scope" below).

### Decisions (maintainer, 2026-07-24)

1. **Scope: index only.** This pass builds the cache; it does not build a catalog or teach the
   scan to identify *which* bumper it found. A future catalog-apply pass reads this cache to avoid
   re-decoding; that's a separate effort.
2. **Storage: a separate VBR-side store**, not an extension of VDF's `FileEntry`/`ScannedFiles.db`.
   ADR 0006 decision 5 originally assumed reusing `FileEntry.grayBytes`/`PHashes` for our pHash
   data "no new structure needed" — checking the actual consumer
   (`ScanEngine.TryBuildCompareSnapshot`/`CheckIfDuplicateClassic`) found that assumption doesn't
   hold cleanly: those dictionaries are read by VDF's own dedup feature keyed to *VDF's own*
   uniform `positionList`; our non-uniform edge/sparse samples would sit in the same dictionary
   without actually feeding VDF's dedup scan (its own "incomplete data for current scan settings"
   fallback would just still re-decode at its own positions). A separate store avoids that
   confusion. Change detection is our own, but can and should reuse `VDF.Core.Utils.OsHashUtils`
   (the same content-hash primitive VDF's own incremental rescan uses) rather than reinventing it.
3. **Middle is sampled by default, not deferred to an on-demand pass.** Supersedes the standing
   two-tier assumption in `AGENTS.md` ("fast edge path... heavier mid-video interstitial path on
   demand") for the *scan* specifically — every scanned file gets edge (dense) + middle (sparse)
   coverage up front, because a 4s sparse interval is dense enough to actually catch short
   interstitials, and deferring middle coverage to a separate pass would mean interstitials are
   never found unless someone explicitly asks. (The `vbr match`/`vbr remove` two-tier CLI flags
   are unaffected — this only changes what the *scan* does by default.)
4. **Sampling defaults for the scan** (distinct from `vbr match`/`vbr remove`'s existing
   `--edge-boundary`/`--sample-interval`/`--sparse-interval`, which are relative to a *known*
   `--clip-length` and default to "whole window dense"; the scan has no known bumper length, so its
   defaults describe presumptive ident-length coverage instead):
   - **Edge boundary: 20s** — how deep from the true BOF/EOF each file is sampled densely.
     CLI-configurable.
   - **Dense interval: 0.2s** — sampling interval within the 20s edge zones.
   - **Sparse/middle interval: 4s** — sampling interval for everything between the two edge zones.
5. **`.vbr.` outputs are excluded from the scan by default, with a switch to include them.**
   `vbr remove` already excludes `.vbr.` files, but as a hard, unconditional rule ("must never be
   re-matched/re-cut") justified by a real correctness risk — re-cutting an already-cut file with
   the same arithmetic cut-point. Scanning carries no such risk (nothing is mutated), so this isn't
   that kind of rule — it's a "usually wasteful" default, not a "would cause harm" one: `.vbr.`
   files are transitional staging artifacts (ADR 0008 — a review window before `cleanup` promotes
   or discards them) and typically near-duplicate the original minus one trimmed bumper, so indexing
   them is often redundant work on something likely to be replaced soon. But a `.vbr.` file having
   had *one* bumper removed doesn't mean it's bumper-free — it could still carry a different bumper
   at the other edge, or one not yet cataloged — so blanket exclusion isn't always correct either.
   Resolution: exclude by default, `--include-vbr-outputs` to opt back in.
6. **Index location: a dedicated VBR-specific folder**, not co-located with VDF's
   `ScannedFiles.db`/state folder — a clean split matching decision 2's "fully separate store"
   choice all the way down to where the bytes live on disk, not just the schema. Exact default path
   (e.g. mirroring VDF's own state-folder resolution algorithm, `CoreUtils.ResolveDatabaseFolder`,
   but rooted at a VBR-specific base) is an implementation detail, not a scoping decision.
7. **Checkpointing: incremental saves during a scan**, mirroring VDF's own
   `Settings.DatabaseCheckpointIntervalMinutes` — a scan over a large real library can be safely
   interrupted (Ctrl+C, crash, or just stopped) and resumed without redoing already-completed work,
   rather than only writing the store once at the end of a full run.
8. **Concurrency: sequential for v1.** One file at a time, matching `match`/`remove`'s existing
   simple per-file loop. VDF's own scan has per-drive-aware `MaxDegreeOfParallelism` for exactly
   this kind of work, but adding equivalent concurrency here (ffmpeg process limits, thread-safe
   ONNX inference batching) is real complexity better justified once sequential throughput is
   actually measured against a real library, not assumed upfront.
9. **Frame cap: adaptive per file**, computed from each file's probed duration
   (`ceil(duration / sparseInterval) + margin`) rather than one large fixed constant. This confirms
   Step 1's whole-file sparse pass needs the probed duration for real (not just as a "would be nice
   to have anyway" aside) — the 400-frame-per-zone cap `MixedDensitySampler` uses elsewhere would
   silently truncate a long movie's middle coverage (400 frames at 4s ≈ 27 minutes) if reused as-is.
10. **`--file` single-target support: not in v1.** `--library` only; a single-file rescan is just a
    library scan over its containing folder for now. Revisit if it turns out to matter in practice —
    `SharedOptions.ResolveCandidates` already supports both, so adding it later is cheap.
11. **`--rescan`/`--force`: included in v1.** Bypasses change detection entirely, forcing every
    candidate to be re-sampled regardless of the cached `OsHash`/timestamps. Needed in practice once
    sampling parameters change (e.g. a different default later) — without it, invalidating the whole
    cache means deleting the index file by hand.
12. **Progress UX: a running counter by default, per-file detail under `--verbose`.** Printing one
    result line per file unconditionally (today's `match`/`remove` convention) would be overwhelming
    for a multi-thousand-file library scan, where each file doesn't carry an individual "result" the
    way a match row does. Default output is a live scanned/skipped/failed tally; `--verbose` adds
    the same kind of per-file sampled/usable/cache-hit detail `match`/`remove` already log.
13. **Index location and name are user-configurable — one physical file per named library (amends
    decision 6).** Decision 6 said the index lives in "a dedicated VBR-specific folder," which read
    alone implies one fixed, un-addressable default. Refined: `vbr scan` takes an optional
    `--library-name <name>` (default: derived from the `--library` folder's own name, e.g.
    `Path.GetFileName`) and `--index <path>` (default: decision 6's dedicated folder,
    `<name>.<ext>`) — each named library gets its **own independent index file**, not one shared
    blob covering every library a user might have. Two reasons: (a) a user with libraries on
    different physical drives/shares may want each library's index to live wherever makes sense for
    *that* library (e.g. beside the media itself, or on faster local storage); (b) any future
    command that reads the index (a `vbr match`/`vbr remove` cache-read mode, a future listing/export
    command, catalog-apply) needs the same location (and/or name) to know *which* library's index to
    open — captured now so the addressing scheme doesn't need retrofitting once real index files
    already exist at a hardcoded default.
14. **File format version number, from the start.** The persisted store's serialization includes an
    explicit format-version field even though there's nothing to migrate yet — mirrors VDF's own
    `DatabaseUtils.cs` precedent (`VDFDB001`→`VDFDB002` format history, migrated forward on save,
    older formats read but not written). Costs nothing to include now; costs a painful blind
    compatibility guess to retrofit later once real index files already exist in the wild.

### Plan

#### Step 1 — a whole-file sampling entry point (`VBR.Core`, extends `Fingerprinting/`)

**Revised 2026-07-24, after discussion** — the first draft below planned three independently
region-bounded fingerprint sets (`BeginEdge`/`EndEdge`/`Middle`, the middle needing its interior
bounds computed and clamped for files shorter than 2×edge-boundary). Simpler alternative, adopted:
sample in two passes with no region-bounds arithmetic at all, and merge into **one combined,
timestamp-sorted per-file fingerprint set**:

1. **Whole-file sparse pass** — one decode covering the *entire* file (0 → probed duration) at the
   sparse interval (4s default), using **keyframe-only decode** (mirroring VDF's own
   `FfmpegEngine.GetDenseAiFrames`/`-skip_frame nokey`) — **not** `DenseFrameSampler`'s full decode.
   Duration is probed once per file (`FFProbeEngine.GetMediaInfo`) and used for two things: bounding
   the decode itself, and sizing this pass's frame cap adaptively (decision 9) — a fixed cap sized
   for a typical episode would silently truncate a feature-length file's middle coverage.
   This is a real correction, not just a simplification: `DenseFrameSampler` fully decodes every
   frame in its window, which is fine for a 20s edge but would full-decode a 45-minute episode's
   entire middle just to emit ~660 sparse samples — exactly the cost ADR 0006 built edge-focused
   sampling to avoid in the first place, and a gap in this plan's original draft (the "middle only"
   version had the identical problem — "the middle" of a long file is still most of it). At a 4s
   interval, keyframe-only decode is an acceptable trade-off — unlike the dense-edge case, where
   keyframe-only decode was the *original black-frame bug* (fine content lived between keyframes
   there; a 4s cadence has no such fine content to lose). Needs a small new primitive: keyframe-only
   decode + the region-aware direct-from-source seek `DenseFrameSampler`'s dense overload already
   has (VDF's `GetDenseAiFrames` doesn't take a `ClipRegion` seek the way that overload does).
2. **Dense edge passes** — unchanged from the first draft: `BeginEdge`/`EndEdge`, each the true
   first/last 20s, full-decoded via the existing `GatherFrames`/`DenseFrameSampler` region-aware
   decode (no `ClipExtractor` involved, same as the match/remove path).
3. **Merge** — every surviving (post-`FrameQuality`) frame from all three decode calls goes into one
   combined, timestamp-sorted collection per file. No "compute the interior region, clamp for short
   files" step: the sparse pass always covers the whole file unconditionally, so a file shorter than
   2×edge-boundary just means the dense edges overlap each other and the sparse coverage — harmless,
   confirmed below. Each surviving frame carries both signals (embedding + pHash) via the same
   one-decode-both-signals pattern `SampleWithPHash` already established.

**Why merging densities is safe** (the question that prompted this revision): presence matching
(`ComparePresence`) is timestamp-tagged and never requires uniform density or alignment between the
two sides being compared — that's the exact principle that already lets dense-near-edge and
sparse-beyond coexist in `MixedDensitySampler` today. A merged collection with a cluster of dense
points sitting near a sparse point changes nothing structurally. Cost, not correctness: the sparse
pass redundantly (but cheaply, keyframe-only) re-covers the edge zones, and `FrameQuality`'s
duplicate filter runs per decode call — it won't dedupe near-identical frames *across* the sparse
and dense passes — so a few redundant near-duplicates near each edge can survive into the merged
set. Minor storage/compute overhead, not a matching-correctness risk.

#### Step 2 — the persisted store (`VBR.Core`, new `Fingerprinting/` or new `Index/` namespace)

A **header** — format version number (decision 14, unconditionally, even though nothing reads it
yet), library name (decision 13, default derived from `--library`'s folder name), and the sampling
parameters (edge-boundary/dense/sparse) the whole file was scanned under, so a future default change
can be recognized as making the file stale rather than silently comparing incompatible fingerprints
— followed by **one entry per video file**: path, `FileSize`, `DateModified`, an `OsHash` (via
`VDF.Core.Utils.OsHashUtils`, reused not reinvented) for change detection; the merged,
timestamp-sorted fingerprint set from Step 1 (each point carrying both an embedding and a pHash);
a whole-file audio fingerprint (self-contained — this store doesn't read or write VDF's `FileEntry`,
so it carries its own audio fingerprint rather than depending on a VDF scan having also been run
over the same files). Serialization format not yet chosen — leaning `MemoryPack` (VDF.Core already
depends on it; binary suits quantized embedding arrays better than JSON) but this is an
implementation detail, not a scoping decision, and can be settled while building rather than
blocking this plan.

**One file per named library** (decision 13) — a `vbr scan --library <folder>` invocation reads and
writes exactly one index file for that library, not a shared store spanning every library a user has
ever scanned. Lives in its own, VBR-specific folder by default (decision 6), at a location and under
a name both overridable via `--index`/`--library-name` (Step 4). Written incrementally during a scan
(decision 7), not just once at the end: a checkpoint save every N files (or N seconds, whichever the
implementation finds simpler to reason about) so an interrupted run loses at most the work since the
last checkpoint, not the whole run.

#### Step 3 — change detection / incremental rescan (`VBR.Core`)

Mirrors the *logic* of VDF's own `ScanEngine.RefreshExistingEntry` without touching VDF's code or
data: size changed → re-sample. Same size, timestamps moved → compute `OsHash`, compare to stored;
match → keep cached fingerprints, just refresh timestamps (no re-decode); mismatch → re-sample.
New file → sample fresh. Missing-on-disk → tombstone or drop (decide during implementation; VDF's
own `RememberDeletedContent` precedent exists if we want the same behavior). `--rescan`/`--force`
(decision 11) short-circuits this whole step per candidate — every file is treated as needing
re-sample regardless of what the cache says.

Files are visited and (re-)sampled one at a time (decision 8) — the candidate list itself still
comes from a single directory enumeration up front (mirroring
`SharedOptions.ResolveCandidates`/`ClipExtractor.VideoExtensions`, filtered per decision 5), only
the per-file sample/compare/persist work is sequential, not the enumeration.

#### Step 4 — `vbr scan` CLI command (`VBR.CLI`)

New command, per `matcher-spec.md`'s "leave room for `vbr scan`" note. `--library <folder>`
(required — no `--file` in v1, decision 10; recursive by default, `--no-recurse` to disable,
matching `match`/`remove`'s existing convention), its own `--edge-boundary` (default 20s)/
`--sample-interval` (default 0.2s)/`--sparse-interval` (default 4s) options — new
`Option<TimeSpan>` instances scoped to this command, not reused from `match`/`remove`'s
`SharedOptions` ones, since the defaults and semantics genuinely differ (absolute depth-from-edge
vs. relative-to-`--clip-length`) even though the flag *names* stay consistent for muscle memory.
`--library-name <name>` (decision 13, default: `--library`'s own folder name) and `--index <path>`
(decision 13, default: the dedicated VBR folder from decision 6, filed under the library name) —
together they address exactly one library's index file; a future cache-reading `vbr match`/
`vbr remove` or a listing/export command takes the same two flags to pick which one to open.
`--include-vbr-outputs` (default off — see decision 5 above): without it, candidate enumeration
drops any file whose name matches `*.vbr.<ext>`, the same test `RemoveCommand` already uses
(`Path.GetFileNameWithoutExtension(f).EndsWith(".vbr", ...)`). `--rescan`/`--force` (decision 11)
to bypass Step 3's change detection and re-sample everything. `--verbose` (reuse
`SharedOptions.SubscribeVerboseLogging`).

Progress reporting (decision 12): a live scanned/skipped-unchanged/failed counter by default; full
per-file detail (sampled/usable frame counts, cache hit vs. re-sample, checkpoint saves) only under
`--verbose` — a per-file `MATCH`-style result line for every candidate, unconditionally, would be
noise at library scale (thousands of files) in a way it isn't for `match`/`remove` (dozens of
candidates against one known bumper, where each line *is* the point). Ends with a summary count.

Does **not** take `--clip-from`, `--region`, `--detection-mode`, or any presence-threshold flag —
it has nothing to match against yet, it only fingerprints.

### Explicitly out of scope for this spike

- The bumper catalog, `vbr enroll`, or teaching `vbr scan` to identify a bumper — separate,
  already-tracked open item (`PROGRESS.md` "Catalog").
- Wiring `vbr match`/`vbr remove` to *read* this cache instead of re-sampling every run — a natural
  follow-up once this exists, but not required for the index itself to be complete and testable.
  Would take the same `--library-name`/`--index` addressing (decision 13) to pick which library's
  file to read.
- Portable path-remapping, export/import, catalog-scale ANN matching — pre-existing open items,
  unaffected by this.
- Reusing VDF's `FileEntry`/`ScannedFiles.db` in any way (superseded by decision 2 above).
- **Listing/viewing an index's contents** (CLI now, GUI eventually) — captured for future reference
  per the maintainer (2026-07-24), not designed or built this spike. A plain console listing is fine
  for a small library but unwieldy at real scale (thousands of entries); a human+machine-readable
  JSON export is the more likely right shape for a large one, and would give the index a natural
  counterpart to the *catalog's* own already-tracked export/import (`bumper-catalog.md`) — a
  distinct need, since this is about the fingerprint index, not the bumper catalog. No design work
  done beyond capturing the need; tracked in `PROGRESS.md` alongside this item.

### Verification plan

1. `vbr scan --library <folder>` over a real mixed corpus (e.g. the existing Avatar/Daredevil/
   Caprica test media) visits every file and produces a cached entry with a non-empty merged
   fingerprint set — dense clusters near both true edges, sparse coverage in between — for each.
2. A second run with no files changed skips re-sampling entirely for every file (fast, no decode).
3. Touching one file's mtime without changing its content (copy/restore-style) still skips
   re-sampling, via the `OsHash` check — same behavior VDF's own rescan relies on.
4. A genuinely modified file gets re-sampled and its cached entry updated.
5. A file shorter than 2×edge-boundary (edges overlap each other and the whole-file sparse
   coverage) still produces a sane merged set — the case Step 1's "Merge" step above claims needs
   no special handling.
6. A `name.vbr.ext` file in the library is skipped (not indexed) by default, and included when
   `--include-vbr-outputs` is passed — decision 5 above, actually enforced, not just documented.
7. **Equivalence check**: filter a cached entry's merged set down to just its dense, edge-zone
   points and feed those into `VisualBumperMatcher.MatchMixedDensity`/`MatchMixedDensityPHash`
   against a live `--clip-from` sample, confirming it reproduces the same numbers a fresh
   `vbr match` run gets on that file — proof the cache is equivalent to sampling live, not just
   present.
8. A feature-length file (well over the old 400-frame-per-zone cap's ~27-minute ceiling at 4s) gets
   whole-file sparse coverage all the way to its real end, not silently truncated — proof the
   adaptive cap (decision 9) actually sizes itself from probed duration rather than a stale constant.
9. Interrupting a scan partway through a multi-file run (Ctrl+C after a checkpoint but before
   completion) and re-running resumes correctly: already-checkpointed files are skipped, not
   re-sampled, and the run picks up where it left off rather than restarting from scratch.
10. `--rescan`/`--force` re-samples every candidate even when nothing changed, overriding what
    Step 3's change detection alone would have skipped.
11. Default output is a running counter (not one line per file); `--verbose` shows per-file detail
    including cache hit/miss and checkpoint saves.
12. Two different `--library` folders, scanned with distinct `--library-name`/`--index` values,
    produce two fully independent index files — scanning one never touches or is visible from the
    other. Omitting `--library-name`/`--index` derives a sensible default from the library folder's
    own name and lands in decision 6's dedicated folder, without requiring the flags to be typed
    every time for the common single-library case.

## Wiring pHash + mixed-density sampling into `vbr match`/`vbr remove` (2026-07-24)

**Status: implemented and validated.** Follow-up to the direct-from-source decode fix below —
both pHash and `MixedDensitySampler` existed only in `VBR.Core`/`VBR.Tests` (zero references from
`VBR.CLI`, confirmed by grep before starting). Planned in plan mode first (scope approved by the
maintainer, including two explicit calls: pHash is a genuinely selectable **alternate/primary**
detection mode, not just report-only corroboration; and mixed-density wiring is bundled into this
same pass rather than deferred).

**Design, in one sentence each:**

- `DetectionMode` gained `phash` (pHash alone, sole decision-maker) and `all` (visual+audio+phash,
  visual still wins when it ran); `visual`/`audio`/`both` keep their exact original meaning.
- `--edge-boundary`/`--sparse-interval` (new) default to "the whole clip/search window is dense" —
  implemented as clamping to whatever `totalLength` each `MixedDensitySampler` call uses (its own
  existing clamp, no new logic), so **not touching these flags reproduces today's single-density
  behavior exactly** — mixed-density is additive, not a separate mode to opt into via some other switch.
- `MixedDensitySampler.SamplePHash` (new) never touches `AiComponents`/ONNX at all — the point of
  offering pHash as a lightweight alternate: `--detection-mode phash` doesn't download or load the
  model.
- `match`/`remove`'s per-file orchestration (construct matcher(s) → sample the clip once → compare
  each candidate) was growing a third signal and a sampler lifecycle on top of code the two
  commands already duplicated verbatim (a deliberate choice at the time — see `RemoveCommand`'s own
  doc comment). Extracted into `VBR.CLI.Commands.MatchingSession` — both commands call it; each
  keeps its own row type/report/removal step, which genuinely differ.
- **Rigid-matcher corroboration (`--rigid-hit-threshold`, the `[rigid …]` report line) is removed.**
  It was already reported as absent from the mixed-density path when that path was first built
  (`ScanEngine.TryMatchDenseFrames` assumes one uniform interval, never adapted); switching the CLI
  onto mixed-density surfaces that gap in real output. Never gated the decision, so no correctness
  change — a visible line disappearing, flagged explicitly and signed off on rather than dropped
  silently in the diff (maintainer: "I'm good with losing the rigid output").
- Audio is untouched — still needs `ClipExtractor.ExtractToTemp` for its reference clip
  (Chromaprint fingerprints a file, not a frame stream); `MatchingSession` only runs that
  extraction when audio is actually requested.

**Re-validated live through the built CLI** (`VBR.CLI/bin/Debug/net10.0/VBR.CLI.exe`, not just unit
tests), reproducing/confirming every number already recorded at the primitive level:

| Case | Command shape | Result |
|---|---|---|
| Daredevil end-stack (default edge-boundary/sparse — single-density) | `match --region end --clip-length 10s --sample-interval 0.2s --library ...` | **13/13 @ 99–100%** vs. Doctor Who **0/13 @ ≤49%** |
| Avatar 47s intro (mixed-density) | `match --region begin --clip-length 47s --edge-boundary 20s --sample-interval 0.5s --sparse-interval 4s --library ...` | **20/20 @ 96–100%** |
| Caprica 5s end-card (default = single-density) | `match --region end --clip-length 5s --sample-interval 0.2s --library ...` | **19/19 @ 93–100%** |
| `--detection-mode phash` alone, Caprica corpus | same as above + `--detection-mode phash --verbose` | no ONNX/model-load lines logged; **2/19** matched (expected — matches the earlier primitive-level finding that pHash under-performs badly standalone on this bumper) |
| `--detection-mode all`, one file | adds `--detection-mode all` | all three details (`visual: … \| audio: … \| phash: …`) printed on one row; decision follows visual |
| `--output`/`--dump-frames` | both flags together | report header shows new fields correctly; `clip-dense/`/per-candidate PNG dumps written |
| `vbr remove --file`, stream-copy | smoke test only | `REMOVED` row printed, sibling `.vbr.mkv` + manifest written correctly; cleaned up after (non-destructive, original untouched) |

**Files:** new — `VBR.CLI/Commands/MatchingSession.cs`. Modified — `VBR.Core/Fingerprinting/MixedDensitySampler.cs`
(`SamplePHash`, verbose logging), `VBR.CLI/Commands/SharedOptions.cs` (`DetectionMode` extended,
`EdgeBoundary`/`SparseInterval`/`PHashPresenceThreshold` options, `RigidHitThreshold` removed),
`VBR.CLI/Commands/MatchCommand.cs`/`RemoveCommand.cs` (rewritten onto `MatchingSession`, `MatchRow`/
`RemoveRow` gained `PHashDetail`, report headers updated). No changes to `VisualBumperMatcher`,
`AudioBumperMatcher`, `ClipRemover`, or `ClipExtractor` itself (beyond what the prior entry below
already made).

---

## MixedDensitySampler: direct-from-source decode, no chained extraction (2026-07-24)

**Status: implemented and validated.** Follow-up to the pHash addition below — testing a real 5s
Caprica end-card (`VisualBumperMatcherMixedDensityTests`, short-bumper case) surfaced a real bug,
and the maintainer's own manual `ffmpeg` investigation correctly identified the architecture as the
root cause, not a one-off corrupt file.

**The bug:** `GatherFrames` extracted the whole edge region to a temp file
(`ClipExtractor.ExtractToTemp`), then extracted each dense/sparse zone as a *second* stream-copy
hop out of *that* temp file (`AppendZone`) — up to three chained ffmpeg processes per candidate
zone. On real media this produced two distinct, real failure modes: (1) a `-sseof` seek landing on
non-monotonic DTS in the source silently produced a duration-inflated, duplicate-padded first-stage
extract (a 5s request came out 14+s); (2) stream-copying *again* out of a file whose first
extraction needed the re-encode fallback could itself produce an outright-corrupt Matroska remux
(`Marvel's Daredevil S01E03`: "Duplicate element" / invalid EBML, ffprobe couldn't read a duration
back at all) — this one crashed the whole comparison run before a per-file try/catch was added.

**The maintainer's question, and the answer:** "why cut a clip first instead of extracting frames
directly from the source, the way a plain `ffmpeg -sseof -N -i source -vf fps=...` command does?"
— correct, and it's what `VisualBumperMatcher`'s validated single-density path was closer to
already (one `ExtractToTemp` + one `DenseFrameSampler` decode, not three hops).
`ClipExtractor.ExtractToTemp` exists because it's shared with `AudioBumperMatcher`, which needs a
real playable file (Chromaprint fingerprints a file, not a frame stream) — that requirement doesn't
apply to visual/pHash frame sampling at all.

**Fix:** `DenseFrameSampler` gained a region-aware overload —
`SampleFrames(sourcePath, ClipRegion, interval, maxFrames, ct)` — that seeks (`-ss`/`-sseof` + `-t`,
via a new `ClipExtractor.AppendSeekArgs` shared with `ClipExtractor.RunFfmpegExtract` so both build
identical seek args) and decodes directly from the original source in **one** ffmpeg process, no
intermediate file. `ClipRegion` gained `BeforeEnd(endOffset, duration)` (a duration-long window
starting `endOffset` before EOF, not necessarily reaching it) — the one zone shape (the "end"
region's sparse zone) that wasn't already expressible via the existing `Head`/`Tail`/`At`. `Tail`
now sets a `EndOffset` field equal to its duration internally; behavior for every existing
`Head`/`Tail`/`At` caller (the validated single-density `VisualBumperMatcher.Match`,
`AudioBumperMatcher`, both CLI commands) is unchanged — confirmed by inspection, not just intent:
`EndOffset == Duration` for `Tail` reproduces the exact same `-sseof` argument as before.
`MixedDensitySampler.GatherFrames`/`AppendZone` now compute each zone directly against
`sourcePath` (no `whole` intermediate at all).

**Re-validated (2026-07-24), same clip/library as the bug report:** Caprica 5s end-card, 18
episodes — floor improved slightly, **93–100% bestCos** (was 91–99% through the old double-hop
path) — the extra re-mux hop was quietly costing quality on top of being a corruption risk.
Daredevil Season 01 as negative corpus, 13 episodes plus the previously-crashing `S01E03` — **no
crash**; `S01E03` now resolves cleanly to "no usable frames in the search window" (its literal last
5s has no real video content at all — see below), FP ceiling **56–68%** (was 62–72%), zero
false positives. Whole run **23s** (was 60–70s) — one decode per zone instead of up to three.

**A second, separate finding along the way (not yet fixed, not a code bug):** several Daredevil
episodes' *video* stream ends measurably before the *container*'s reported duration — confirmed via
`ffprobe`, episode `S01E01`: last video frame at `3172.440s`, last audio frame at `3175.210s`,
container duration `3175.231s` — a **2.8s video/audio gap** (audio-only tail, e.g. trailing
network-outro music/silence with no matching picture). `-sseof` seeks against the *container*
duration, so a request for "the last N seconds" can land partly or entirely in that video-less gap;
`S01E03`'s gap is apparently even larger (a 5s request found zero video frames at all, even via a
direct single-hop re-encode). This is unrelated to the chained-extraction bug above — it would
affect a single-hop direct decode identically — and doesn't affect this project's actual target
(Daredevil's own bumper, the Netflix end-card, sits well inside the file, not in this trailing gap;
this only came up because Daredevil was being used as *unrelated negative content* for the Caprica
bumper). Logged in `docs/PROGRESS.md`'s open items as a real, general "video ends before container
EOF" resilience gap worth a deliberate fix (e.g. a safety margin on EOF-relative seeks) rather than
a blind guess, since it trades off how close to the true edge a request can land.

**Files:** modified — `VBR.Core/Extraction/ClipExtractor.cs` (`ClipRegion.EndOffset`/`BeforeEnd`,
`AppendSeekArgs`), `VBR.Core/Fingerprinting/DenseFrameSampler.cs` (region-aware overload),
`VBR.Core/Fingerprinting/MixedDensitySampler.cs` (`GatherFrames`/`AppendZone` rewritten, no `whole`
extraction). No changes to `VisualBumperMatcher`, `AudioBumperMatcher`, or any CLI command.

---

## Mixed-density edge/middle fingerprinting — spike plan (2026-07-21)

**Status: implemented and validated (2026-07-21), same day.** Written up per the maintainer's
request after an earlier same-day test (`VisualBumperMatcherOffsetTests`, kept in the repo — see
below) turned out to validate a different claim than the one in question. Built exactly as planned
below, with one deliberate accommodation: the maintainer asked to leave room for pHash as a second
per-position signal "very soon," so `MixedDensitySampler` factors frame-gathering (extract →
full-decode → low-information filter → timestamp) into its own signal-agnostic internal step
(`GatherFrames`, returning plain timestamped RGB24 frames) separate from embedding (`Sample`) — a
future pHash addition consumes the same gathered frames rather than triggering a second decode
pass. No pHash code was written; this is structure only.

**Result — real media, both directions:**

| Test | Corpus | Expectation | Result |
|---|---|---|---|
| `MatchMixedDensity_FindsAnEdgeBumperLongerThanTheBoundary` (positive) | Avatar: The Last Airbender S01, 20 episodes, 47s true-begin intro, profile = 20s dense @ 0.5s / 27s sparse @ 4s | most/all episodes match | ✅ **19/19 other episodes MATCH**, present 21–25/40 usable clip frames, bestCos 96–99% |
| Same clip vs. Doctor Who (2005) S01, 13 episodes (negative control) | unrelated content | zero false positives | ✅ **0/13**, present=0/40 on every file, bestCos 23–49% |

~50-point gap between the true-positive floor (96%) and the false-positive ceiling (49%), zero
false positives — the mechanism works cleanly on the actual scenario: one 47s bumper, genuinely
mixed density on both the reference clip and every candidate, matched via
`VisualBumperMatcher.MatchMixedDensity` with no temporal alignment between the two sides.

**Regression check (constraint a):** `VisualBumperMatcherOffsetTests` was re-run byte-for-byte
identical before and after the `VisualBumperMatcher.Match` refactor (same `present`/`bestCos`/
`rigid`/`win` numbers on every one of the 12 Daredevil episodes) — the existing single-interval
path is provably unaffected.

**Files:** new — `VBR.Core/Fingerprinting/EdgeDensityProfile.cs`, `TimedFrame.cs`,
`MixedDensitySampler.cs`; `VBR.Tests/Matching/VisualBumperMatcherMixedDensityTests.cs`. Modified —
`VBR.Core/Matching/VisualBumperMatcher.cs` (`Match` refactored onto a shared `ComparePresence`
helper via a new `ToTimedFrames` conversion; added `MatchMixedDensity`). No changes to
`DenseFrameSampler`, `FrameQuality`, `ClipExtractor`, or any VDF.Core file.

**Related:** [`decisions/0006-edge-focused-fingerprinting.md`](decisions/0006-edge-focused-fingerprinting.md)
(decisions 1/4/5 — the density profile and the non-uniform `(timestamp, value)` data model this
spike needs a minimal slice of), [`PROGRESS.md`](PROGRESS.md) ("Edge-focused scan + a cached
fingerprint/embedding index," the still-open item this spike de-risks).

### The problem, precisely

A bumper can touch the true edge of a file and still be longer than `edge-boundary` (the
ultra-dense sampling window) — e.g. a 47s title sequence at the true beginning against a 20s
boundary. That single bumper's fingerprint then needs **two densities inside one record**: dense
samples from the true edge out to `edge-boundary`, sparse samples the rest of the way. Today's
sampling (`VBR.Core.Fingerprinting.DenseFrameSampler`) and frame record
(`VDF.Core.AI.DenseEmbeddingStore.DenseRecord`) both assume **one** interval for an entire region
and infer each frame's time as `index × interval` — a formula that breaks the moment two
intervals coexist. This was misdiagnosed once already this session as an *offset/alignment*
problem (does a clip extracted away from the true edge still match?) — already tested and
confirmed fine, but a different question from this one, which is about **density mixing within a
single edge-anchored fingerprint**, not extraction offset.

**Test corpus:** *Avatar: The Last Airbender* Season 1 (`test_materials/Avatar/Season 01`, 20 real
episodes) — every episode opens with the same ~47s title sequence at the true beginning, long
enough to genuinely exceed a 20s `edge-boundary`. Negative control: an existing unrelated corpus
already validated as sharing no content with Avatar (Doctor Who or Daredevil).

### Constraints (maintainer, 2026-07-21)

Modifying `VBR.Core` is in bounds, provided the change: (a) doesn't lose existing functionality,
(b) improves our existing matching, (c) isn't a huge new stack of code, (d) doesn't fundamentally
change the existing strategy/architecture. Each step below is sized against these explicitly.

### Step 1 — two small new types, additive only (`VBR.Core/Fingerprinting/`, new files)

`EdgeDensityProfile.cs` — the three knobs the maintainer asked to expose, bundled as one value so
they thread through signatures together instead of as three loose primitives:

```csharp
public readonly record struct EdgeDensityProfile(
    TimeSpan EdgeBoundary, TimeSpan DenseInterval, TimeSpan SparseInterval);
```

`TimedFrame.cs` — an explicit timestamp per embedded frame, replacing the implicit
`index × interval` that breaks under mixed density. This is the minimal slice of ADR 0006
decision 4/5's non-uniform `(timestamp, value)` model needed to represent mixed-density data at
all — not the full persistent sidecar record, just the in-memory shape:

```csharp
public readonly record struct TimedFrame(double TimestampSeconds, byte[] Embedding);
```

Neither type touches `DenseEmbeddingStore`/`DenseRecord` — those stay exactly as they are, still
used by VDF's own whole-file AI-partial pass. This is new, additive surface area, not a
replacement (constraint a).

### Step 2 — the sampler (`VBR.Core/Fingerprinting/MixedDensitySampler.cs`, new)

A small class (owns an `OnnxEmbedder`, same lifetime pattern as `VisualBumperMatcher`) with one
method:

```csharp
public IReadOnlyList<TimedFrame> Sample(
    string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
    CancellationToken ct = default);
```

Algorithm — entirely composed from existing primitives, nothing new at the ffmpeg/decode level:

1. Extract the **whole** requested region once: `ClipExtractor.ExtractToTemp(sourcePath,
   ClipRegion.For(region, totalLength))` — identical to what `VisualBumperMatcher` already does.
2. Within that temp file, carve the dense and sparse sub-regions as two further temp files via
   `ClipRegion.At(...)` (already public, already used for the offset spike) — for `begin`: dense
   = `At(0, edgeBoundary)`, sparse = `At(edgeBoundary, totalLength - edgeBoundary)`; for `end`,
   mirrored: dense = `At(totalLength - edgeBoundary, edgeBoundary)`, sparse =
   `At(0, totalLength - edgeBoundary)`.
3. Run `DenseFrameSampler.SampleFrames` on each sub-region at its own interval — unchanged, reused
   as-is.
4. Run `FrameQuality.SelectUsable` on each — unchanged, reused as-is, applied consistently to both
   densities.
5. Embed the usable frames via `OnnxEmbedder.EmbedBatchQuantized`, batched exactly like
   `VisualBumperMatcher.Embed` already does (same `OnnxEmbedder.MaxBatch` chunking loop).
6. Assign each surviving frame its real timestamp (`zoneStart + index × zoneInterval`) and emit a
   `TimedFrame`. Unlike `DenseRecord`, filtered-out frames are simply **omitted** rather than kept
   as empty placeholder slots — the index↔time trick existed only to preserve an implicit time
   formula that explicit timestamps no longer need. One small simplification, not a new concept.

No changes to `ClipExtractor`, `DenseFrameSampler`, or `FrameQuality` — all three are reused
verbatim (constraint c: this is composition, not a new stack).

### Step 3 — teach `VisualBumperMatcher` to compare `TimedFrame` lists (modify existing file)

`VisualBumperMatcher.Match` ([`VisualBumperMatcher.cs:147-185`](../VBR.Core/Matching/VisualBumperMatcher.cs#L147-185))
currently inlines the presence loop directly over two `DenseEmbeddingStore.DenseRecord`s. Extract
that loop (lines 161–176) into a small private static helper over the general shape both callers
actually need:

```csharp
static (bool present, float best, double? bestTime, int hits) ComparePresence(
    IReadOnlyList<TimedFrame> clip, IReadOnlyList<TimedFrame> candidate, float presenceThreshold);
```

Then:

- The **existing** `Match(string referenceClipPath, string candidatePath, ClipRegion, ...)` path
  converts its `DenseRecord` frames to `TimedFrame`s inline (`frame[i]` + `i × interval`, skipping
  empty slots — a few lines) and calls the shared helper. This must produce **byte-identical
  results** to today's behavior — same thresholds, same math, purely reorganized — and is the
  concrete check for constraint (a).
- A **new** public method, `MatchMixedDensity(IReadOnlyList<TimedFrame> clip, IReadOnlyList<TimedFrame> candidate)`,
  calls the same shared helper directly with sampler-supplied frames. This is the literal answer
  to "can `VisualBumperMatcher` handle this data" — yes, through this entry point, with zero
  duplicated matching logic (constraint b: the matcher genuinely gains a capability, not a
  bolted-on parallel path).

**Explicitly not attempted here:** adapting the "rigid" ≥4-consistent-offset corroboration matcher
(`ScanEngine.TryMatchDenseFrames`) to mixed-density data. It's corroboration-only, never gates a
decision, and its `DenseRecord` input assumes a single interval — forcing it to accept
`TimedFrame`s means touching upstream `VDF.Core` for no matching-correctness benefit. The
mixed-density path reports presence-only results; the rigid number is simply absent for it.

### Step 4 — the configurable test (`VBR.Tests/Matching/VisualBumperMatcherMixedDensityTests.cs`, new)

A new file, not a modification of `VisualBumperMatcherOffsetTests.cs` — that test stays as
committed, under its current name, and may get reused/tweaked for interstitial matching later per
the maintainer's own call. Same env-var-gated, skip-cleanly convention as the existing real-media
tests. Parameters, matching the maintainer's own worked example exactly:

- `BUMPER_CLIP_EPISODE`, `BUMPER_EPISODES_DIR`, `BUMPER_REGION` — reused as-is from the existing
  tests.
- `BUMPER_MIXED_TOTAL_LENGTH_SECONDS` (e.g. `47`) — the full known bumper length.
- `BUMPER_MIXED_EDGE_BOUNDARY_SECONDS` (e.g. `20`) — the ultra-dense zone length.
- `BUMPER_MIXED_DENSE_INTERVAL_SECONDS` (e.g. `0.5`) — sampling interval inside the boundary.
- `BUMPER_MIXED_SPARSE_INTERVAL_SECONDS` (e.g. `4`) — sampling interval beyond it.
- Optional `BUMPER_MIXED_NEGATIVE_DIR` — an unrelated-content folder; when set, asserts **zero**
  matches, alongside the positive assertion (at least one match) against `BUMPER_EPISODES_DIR`.

Both the clip and every candidate get sampled through the **same** `MixedDensitySampler` call with
the **same** `EdgeDensityProfile` before `VisualBumperMatcher.MatchMixedDensity` compares them —
proving the actual scenario: one bumper, two densities, matched correctly end to end.

### Explicitly out of scope for this spike

- **Persistence.** No serialization of `TimedFrame` records to disk. This spike only needs
  in-memory data for one test run; the real sidecar format is separate, already tracked (ADR 0006
  decision 5, `PROGRESS.md`).
- **The library-scan CLI.** This proves the sampling+matching primitive works. Wiring it into a
  `vbr scan`-style command that walks a tree and builds a persistent index is the next, separate
  task — this spike is a prerequisite for it, not a first draft of it.
- **Middle-region/interstitial matching.** Likely served by the same primitives eventually (why
  `VisualBumperMatcherOffsetTests` was kept), but a distinct effort from this one.

### Verification plan

1. Build clean.
2. Re-run `VisualBumperMatcherOffsetTests` and confirm identical output to before the Step 3
   refactor — the concrete proof that existing functionality survived (constraint a).
3. Run the new mixed-density test live against Avatar with the maintainer's own numbers
   (47 / 20 / 0.5 / 4) — confirm a positive match across the 20 episodes.
4. Run again with a negative corpus set — confirm zero false matches.
5. Only then treat the mixed-density mechanism as validated and ready to inform the real
   `edge-boundary` default and the library-scan design.

### Open questions, deliberately deferred until after the spike

- Production defaults for `edge-boundary`/dense/sparse intervals — this spike's numbers are for
  exercising the mechanism, not necessarily what ships.
- Whether `MixedDensitySampler` becomes the *only* sampling path for `VisualBumperMatcher`
  (retiring the single-interval path in `Embed`) or coexists as a special case for regions longer
  than `edge-boundary`.

---

## Fixing the visual matcher's black-frame false positives

**Status (final, 2026-07-18):** **all sections implemented and validated.** §B (CLI features)
and §D (doc updates) landed first; §A (correctness fixes) followed on maintainer approval, and
the full §C re-validation matrix passed — perfect separation (begin: TP 12/12 @ 99–100% with
present=18/18 vs FP 0/33 files with bestCos ≤56%; end regression: TP 12/12 @ 99–100% vs FP 0/20
@ ≤71%; see §C below for the recorded numbers). This doc captures the diagnosis of the bad-match
results reported during begin-region (Netflix ident) testing, the fix plan, and the outcome.

**Related:** [`design/matcher-spec.md`](design/matcher-spec.md) (the "definition of done" this
restores), [`decisions/0006-edge-focused-fingerprinting.md`](decisions/0006-edge-focused-fingerprinting.md)
(sampling), [`research/vdf-evaluation.md`](research/vdf-evaluation.md) (validation log to update).

---

### The reported problems

1. `match` should traverse subtrees by default; a switch to *not* traverse would be prudent.
2. A 5s Netflix bumper from the **begin** of Daredevil scored 99% — but validating against Doctor
   Who (which does not contain that bumper) produced **matches**. Something is badly broken.
3. Matching that same 5s begin clip against Avatar gave `bestCos` in the high-80s across the board,
   plus **four** matches for a Netflix bumper that does not appear in those videos.
4. We need a switch to write match results to a file.

Symptoms 2 and 3 are one bug. Symptoms 1 and 4 are missing CLI features.

---

### Root cause: the matcher is comparing black frames to black frames

The CLI faithfully reproduces the validated probe — this is **not** a mis-port. The problem is two
latent defects in the shared decode/sample pipeline that the begin-region / Netflix-ident scenario
exposes, plus the fact that the spec's "do not match on black" rule was never actually implemented.

The extraction + decode chain was replicated on the real test files
([`VisualBumperMatcher.cs:121`](../VBR.Core/Matching/VisualBumperMatcher.cs#L121) →
`GetDenseAiFrames` → [`FfmpegEngine.cs:1009`](../VDF.Core/FFTools/FfmpegEngine.cs#L1009)) and the
frames the matcher actually sees were dumped to PNG and inspected.

#### Finding 1 — "14 frames" is really 3 distinct images, only one of them distinctive

*(Corrected 2026-07-18, second pass — the first write-up of this finding said "13 of 14 frames
are pure black," an interpolation from viewing only 3 dump frames. Ground-truth verification
below fixed the composition; the mechanism is unchanged.)*

`GetDenseAiFrames` decodes **keyframes only** (`-skip_frame nokey`, inherited from VDF's whole-file
dedup scan), then the `fps=1/0.2` filter fills the 0.2s grid by **duplicating** each keyframe.
Daredevil S01E01's first 5s has exactly three keyframes (full ffprobe frame map verified —
everything between them is P/B):

- I-frame at 0.021s — black (the file genuinely opens on black)
- I-frame at 1.022s — **blank white** (the ident background flashing on — the scene cut that
  earned the I-frame)
- I-frame at 2.607s — the red NETFLIX card

The fps grid turns that into **6 copies of black + 7 copies of blank white + 1 red card** —
14 frames total, exactly the `present=…/14` denominator in the reported output. The letters
animation (~1.4–2.5s, the most distinctive content in the ident) sits **entirely mid-GOP and is
never sampled at all**; the card's ~2.2s on-screen hold is represented **once**.

**Ground-truth verification (maintainer challenge, same day):** the maintainer exported a
per-frame 0.2s reference grid from DaVinci Resolve
(`test_materials/dd_netflix_bumper_davinci_export/24Frames/`) that looked nothing like the
pipeline dump — and follow-up checks confirmed why, while validating the mechanism:

- A **full-decode** `fps=1/0.2` dump of the same 5s yields **25 frames matching the DaVinci
  reference frame-for-frame** (black → blank white → letters flying in → 3D shadow → red card).
  ffmpeg has no problem producing the right frames — the defect is the pipeline's *frame
  selection*, not decode.
- The `-skip_frame nokey` decode itself is **pixel-correct** (the 3 keyframes decode identical
  to full decode — no corruption). Every sampled frame is a *genuine* frame from its timestamp;
  the pathology is which timestamps get represented and how many times.
- The maintainer's separate keyframe dump (`.../Keyframes/`, more visual variety) is from
  `Bumper.mkv` — a DaVinci **re-encode** with a fresh GOP (I-frames at exactly
  0/1.001/2.002/3.003/4.004/5.005) — not the original's keyframe structure, which resolves that
  apparent contradiction.
- The `present=6/14` hits are **precisely the six black duplicates** (the blank-white frames sat
  in the high-80s against these libraries, just under the 0.90 threshold — part of the
  suspicious bestCos floor).
- **Not begin-specific:** the same episode's *end* region keyframes every ~1.4–3s (scene-cut
  driven, bright distinctive cards) — which is exactly why the end-region validation passed.
  Severity is **keyframe-cadence + content dependent, on both the clip and candidate sides**;
  the begin edge just happened to expose it first.

#### Finding 2 — the search windows are black too

Doctor Who's mp4s have keyframes every ~6s (0, 6, 12, 18, 24), and the keyframes at 0s and 6s are
pure black (verified visually). So each candidate's search window is also mostly duplicated black
frames.

#### Finding 3 — there is no black-frame filter anywhere

The spec's "skip empty/black frames" step ([`matcher-spec.md`](design/matcher-spec.md), §2 step 3)
is implemented in both the probe and the port as "skip zero-length buffers" — but
`GetDenseAiFrames` never emits a zero-length frame (it slices fixed-size chunks or fails the whole
call). The guard is **dead code**; nothing has ever filtered black frames. The end-region
validation passed anyway because the Daredevil end-stack clip is a long run of distinctive bright
cards landing on scene-cut keyframes — the pathological all-dark-keyframes case simply never came
up until begin-region testing.

#### Why this explains every symptom

- **DINOv2 embeddings of near-black frames cluster tightly** — cosine 0.87–0.97 against other
  near-black frames (compression noise keeps them just off 1.0). Episodes where the noise happened
  to land ≥0.90 became "MATCH"; the rest produced the suspicious 87–89% `bestCos` floor. That is
  the Avatar high-80s and the four Avatar false matches — sampling luck, nothing more.
- **`present=6/14` almost everywhere** is six duplicated black frames crossing the threshold — one
  degenerate image masquerading as six pieces of corroborating evidence.
- **Rigid corroboration is fooled by the same duplicates**: ≥4 "temporally-consistent" hits are
  trivially satisfied when both sides repeat identical frames (e.g. rigid@10s in Doctor Who = the
  black keyframe at 6.0s smeared across ticks 6–11.8s).
- **Audio behaved correctly** throughout (45–73%, all below the 0.80 threshold) — no action needed
  on the audio path.

**Caveat worth internalizing:** the Daredevil-vs-Daredevil 99% "success" was **inflated by the same
defect** — some of those hits were black-on-black too. The real end-region validation still holds
(distinctive cards genuinely matched), but the exact numbers are not trustworthy and must be
re-recorded after the fix.

---

### Plan

#### A. Correctness fixes (both needed — either alone still fails) — IMPLEMENTED (2026-07-18)

1. **Low-information frame filter (implements the spec's existing rule).** ✅ Implemented as
   `VBR.Core.Fingerprinting.FrameQuality`: reuses VDF's own AI-partial-scan guards
   (`ScanEngine.SelectUsableDenseFrames` — the ≥80%-dark-pixels rejection and the
   byte-identical-duplicate drop, which the probe/port had bypassed all along) and adds the
   near-uniform rejection those guards lack: mean absolute horizontal luma delta ≥ **1.0**
   (`FrameQuality.MinDetail`). Calibrated on real frames (0.2s full-decode grids of the DD
   ident, DW/Avatar begin windows, DD end credits): blank-white ident background 0.55–0.68 and
   fades ≤0.95, versus letter animation 1.33–1.97, dark-but-real scene content 1.46+, bright
   cards ≥3 — 1.0 sits mid-gap. Applied to **both** sides in `VisualBumperMatcher.Embed`; an
   all-filtered clip **fails loudly** via the new `PrepareClip` (which also caches the clip's
   embeddings per run — the port had been re-embedding the clip for every candidate). Upstream
   `GetDenseAiFrames` untouched.

2. **Decode all frames in edge windows, not just keyframes.** ✅ Implemented as
   `VBR.Core.Fingerprinting.DenseFrameSampler`: the identical ffmpeg recipe minus
   `-skip_frame nokey` (the exact full-decode chain verified frame-for-frame against the
   maintainer's DaVinci reference export). The 5s test clip now yields 26 sampled / 18 usable
   distinct frames where the old path produced 14 fps-duplicates of 3 keyframes with a single
   distinctive image among them.

3. **Defer threshold tuning until after re-validation.** ✅ Resolved — no tuning proved
   necessary: the §C matrix passed with the spec's original presence rule (≥1 distinctive frame
   at ≥0.90 cosine) and every default untouched.

#### B. CLI features requested — IMPLEMENTED (2026-07-18)

4. **Recursive library traversal by default.**
   [`MatchCommand.cs:198`](../VBR.CLI/Commands/MatchCommand.cs#L198) currently enumerates a single
   folder. Switch to `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }`,
   add a `--no-recurse` switch, update the `--library` help text (it currently says
   "non-recursive"), and print **library-relative paths** so same-named files in different
   subfolders remain distinguishable.

5. **`--output <file>`.**
   Write the same per-file lines + summary to a file. The probe already did this (its
   `visual-tail-results-*.txt`); the feature was lost during productionization. Restructure each row
   into a small record while doing this so a later `--output-format json` follows cheaply
   (`VDF.CLI` already has a JSON-output precedent).

6. **Optional but recommended: `--dump-frames <dir>` diagnostic.**
   Write the sampled clip/window frames as images. This diagnosis required rebuilding the pipeline
   by hand; this switch makes the next "why did this match?" a ten-second glance.

#### C. Re-validation matrix — PASSED (2026-07-18, all five runs; `--detection-mode visual`, 0.2s interval)

| Test | Expectation | Result |
|---|---|---|
| Daredevil begin clip (5s) vs Daredevil S01 (begin) | all episodes match, on the *card* frames (not black) | ✅ **12/12 MATCH, present=18/18, bestCos 99–100%, rigid 97–98%@0s** |
| Same clip vs Doctor Who S01 (begin) | **0** false positives | ✅ **0/13, bestCos 19–53%** (was 9 false MATCHes @ 87–97%) |
| Same clip vs Avatar S01 (begin) | **0** false positives | ✅ **0/20, bestCos 52–56%** (was 4 false MATCHes) |
| End-stack regression: last-10s clip vs Daredevil S01 (end) | still 12/12 | ✅ **12/12 MATCH, present=32–33/33, bestCos 99–100%, rigid 97%@16–20s** |
| End-stack regression: same clip vs Avatar S01 (end) | still 0 FP | ✅ **0/20, bestCos 62–71%** |

Re-recorded baselines and notes:

- **Begin-region separation: TP 99–100% (presence 18/18) vs FP ≤56% (presence 0/18)** — a
  ~44-point gap with full evidence counts, replacing the broken state's inverted picture
  (false MATCHes at 87–97% off six duplicated black frames).
- **End-region FP floor moved ≤33% → ≤71%** and is expected to: the old ≤33% was distinctive
  bright keyframe-cards compared against Avatar's few sampled keyframes; the honest comparison
  is 33 usable clip frames against ~150 usable real content frames per candidate. The gap to
  the 0.90 presence threshold (and to TP presence counts: 32–33/33 vs 0/33) remains wide.
- Presence denominators are now real evidence: every usable clip frame is a distinct image
  (duplicates dropped), so `present=18/18` means eighteen different pictures found, not one
  black frame counted six times.
- Doctor Who/Avatar library file counts differ from the first (broken) runs because the stray
  `intro*.mkv` clips are no longer in those folders.

#### D. Documentation debt this uncovered — DONE (2026-07-18)

- **`matcher-spec.md`:** "skip empty/black frames" must be specified as a real luma filter, and the
  keyframe-only-decode discovery recorded (it also colors ADR 0006's "dense sampling" framing —
  density past the keyframe cadence was previously an illusion).
- **`research/vdf-evaluation.md` / `PROGRESS.md`:** log this failure mode and the corrected
  begin-region results; annotate the earlier "~65-pt gap" claim as **end-region-specific**.

---

### Ordering & risk

Do **A1 + A2 together**, then re-validate (**C**), then **B4 / B5** (independent, can land anytime),
then docs (**D**).

**Flagged risk:** full-frame decode changes the validated pipeline, so the end-stack regression run
in C is **not optional** — it is the guard against trading one wrong thing for another.

**Progress note (2026-07-18):** §B and §D landed first (B4 recursive traversal + `--no-recurse` +
relative paths, B5 `--output` with structured `MatchRow` rows, B6 `--dump-frames` via
`VBR.Core.Diagnostics.FrameDump`; docs updated per §D plus `running_and_building.md`, `AGENTS.md`,
and `PROGRESS.md`).

**Final note (2026-07-18, same day):** on maintainer approval, §A landed
(`DenseFrameSampler` + `FrameQuality` + clip-embed caching/`PrepareClip`, with 5 unit tests) and
the full §C matrix ran clean — see the recorded results above. The flagged risk was handled as
planned: the end-stack regression re-ran and re-recorded (12/12 @ 99–100%; FP floor ≤71%,
explained above). The defect this doc diagnoses is **fixed and validated**; remaining follow-ups
live in `docs/PROGRESS.md` (cached index, catalog, removal engine — and note the index must be
built on this corrected sampling layer).
