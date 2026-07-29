# Iterative Plan Document

This document catalogs planning concepts as we iterate in development. Newest plan goes at the
top, under its own second-level heading; older plans stay below under theirs, kept for historical
reference rather than deleted or overwritten.

## CLI terminology & multi-folder libraries — sequencing plan (2026-07-28)

**Status: plan for discussion, not yet approved or built.** Grounded in [`docs/design/clarification-terms-cli.md`](design/clarification-terms-cli.md) (maintainer's UX terminology analysis, three rounds of review) plus a set of concrete decisions and open questions resolved in discussion afterward. This entry sequences the resulting work; it doesn't restate the terminology analysis itself — read that doc first.

### Definitions this plan builds on (recap, not a repeat of the source doc)

- **Library** — a user's conceptual media collection. Purely mental until named; a user may have several, and any one library may span multiple parent folders. Not itself a stored artifact — nothing persists "a library" independent of a database or of what the user retypes ad hoc.
- **Named Library** — internal-team term only, never shown to users. What you get once a user supplies a library name that correlates to a folder set. Exists so *we* can talk precisely about "the thing a database corresponds to" without confusing it with the nebulous, unnamed conceptual library from the paragraph above. Never appears in CLI/GUI copy.
- **Library Database** ("database"/"db" for short) — the persisted, named, single-file cache of sampled fingerprints. Today's `LibraryIndex`/`.vbridx`, renamed per the terminology work below. Correlates to exactly one *named* library, but a library's folder set itself is never stored anywhere except inside this file (and whatever the user retypes for an ad-hoc run) — see the "does a library persist without a database" thread, resolved below: it doesn't; the user re-supplies the folder list each time in ad-hoc mode.
- **Bumper** — one identified, removable segment. Individually addressable (list/add/edit/rename/duplicate/delete), most of which isn't built yet.
- **Bumper Catalog** ("catalog") — the persisted collection of bumpers. Deliberately uncorrelated to any library or database (already built this way — see the "Bumper catalog" plan/implementation entry below this one).

### Phase 1 — Terminology: "index" → "database"/"db" — implemented and validated (2026-07-29)

**Status: done.** Went with the full internal rename (the open question below was resolved in that direction, for consistency with `CatalogName`/`--library-db-folder`/`--catalog-db-folder`'s established practice of keeping internal vocabulary matching external vocabulary).

What changed: `VBR.Core.Index` → `VBR.Core.Database` (folder + namespace); `LibraryIndex` → `LibraryDatabase`, `LibraryIndexEntry` → `LibraryDatabaseEntry`, `LibraryIndexStore` → `LibraryDatabaseStore`, `LibraryIndexKey` → `LibraryDatabaseKey`; `ResolveIndexPath`/`GetDefaultIndexFolder` → `ResolveDatabasePath`/`GetDefaultDatabaseFolder`; `.vbridx` → `.vbrdb`; magic header `VBRIDX01` → `VBRDB001`; default state folder leaf `index` → `database`; `LibraryScanner`'s `ScanSummary.IndexSaveError` → `DatabaseSaveError`, and its `indexPath` parameters/locals → `databasePath`. `VBR.Tests/Index` → `VBR.Tests/Database`, with matching test-class/method renames (`LibraryIndexStoreTests` → `LibraryDatabaseStoreTests`, etc.). `ScanCommand`'s help text and console/log output ("Index: ..." → "Database: ...", "could not save the index" → "could not save the database", etc.) and `SharedOptions.LibraryName`'s help text updated to match. `VBR.Core.Catalog`'s doc comments (which cross-referenced the old `Index.*` types throughout) updated in step. `docs/running_and_building.md` and `docs/design/matcher-spec.md`'s amendment blocks updated to current terminology; `docs/PROGRESS.md`'s and `AGENTS.md`'s own "index" mentions were checked and left as-is — they're either inside historical dated entries (accurate records of what things were called at the time, same principle this document itself follows) or refer to something unrelated (VDF's own architecture, a generic English use of "index").

No behavior changed — purely a rename, confirmed by `dotnet build`/`dotnet test VBR.Tests` (67 passed, 0 failed, same as before) plus a live `vbr scan` smoke test against a throwaway file confirming the `.vbrdb` extension, the "Database: ..." console line, and correct default-folder resolution. The old `%LOCALAPPDATA%\VideoBumperRemover\index\` folder from prior real usage was left in place (real user data, not touched) — new scans now default under `...\database\` instead; nothing migrates the old folder's contents, since nothing in it needs migrating (a first scan of a "new" library under the new default location is not an error, same as any other fresh database).

### Phase 2 — Multi-folder libraries: `--library` becomes a delimited list, plus a new exclusion flag — decided, not yet built

**Status: fully decided (2026-07-29), implementation pending.** Real feature work, not a rename — touches `SharedOptions.ResolveCandidates` and every command that calls it (`match`/`remove`/`cleanup`/`scan`; `add-bumper` is unaffected, since it dropped `--library` entirely in the last round of changes).

Final shape, per the maintainer's direction and VDF's own "Where to look" UI (include/exclude folders as parallel, symmetric concepts):

- `--library` changes from one `DirectoryInfo` to a semicolon-delimited list of paths, parsed the same way `--tags` already splits on commas in `add-bumper` (a `CustomParser`, no new parsing infrastructure needed). Every path validated to exist, same as today's single-path check. **Decided: single flag, semicolon-delimited** (not a repeatable `--library A --library B` flag).
- New **`--exclude-folders`** (name decided, final) takes the same semicolon-delimited-list shape — paths to exclude from whatever `--library` resolved to.
- `--no-recurse` stays a single flag applying uniformly to every listed path — per-path recursion control isn't proposed and would be a finer-grained feature than this phase needs.
- Exclusion matching: filter candidates whose path falls under an excluded folder *after* enumeration, mirroring how `.vbr.`-output filtering already works, rather than skipping directory traversal during enumeration. Simpler, consistent with existing code; costs a little wasted enumeration on a large excluded subtree, which seems like the right trade for v1.
- Confirmed distinct from `.vbr.`-output filtering — that's a filename-pattern rule; this is a path/folder rule. Both stay, independently.
- **Decided: the same folder may belong to more than one library at once.** No dedup/exclusivity check across libraries. The maintainer's reasoning: separately-planned work on handling videos that change/move within a library will handle most or all of the consequences of this overlap, so it doesn't need its own guard here.

### Decided — `vbr cleanup` renamed to `vbr commit`

VDF's actual menu (screenshots reviewed 2026-07-28) has both **"Cleanup Database"** and **"Prune Ghost Entries"** — VDF's own terminology neighborhood for future database-maintenance commands (Phase 3 below). VBR's existing `vbr cleanup`/`clean` (promoting verified `.vbr.` outputs, deleting originals) occupied that vocabulary for something unrelated. **Decided (2026-07-29): `vbr cleanup` is renamed to `vbr commit`** — not `vbr promote` as earlier floated; the maintainer's own choice. Frees "cleanup"/"clean"/"prune" for Phase 3's database-maintenance commands. **Not yet implemented** — this is a real rename touching `VBR.CLI.Commands.CleanupCommand` (or its replacement), its `clean` alias, ADR 0008/`docs/decisions/0008-cleanup-command.md`'s references, `running_and_building.md`, and any other command help text that says "cleanup"/"`vbr cleanup`" today.

### Phase 3 (sketched, depends on `vbr commit` shipping) — database-maintenance commands

Mirrors VDF's Cleanup Database/Prune Ghost Entries split once `vbr commit` frees up the vocabulary. Depends on tombstoning (below) existing first, since "Prune Ghost Entries" is specifically about entries that tombstoning would create.

**Tombstoning — implemented (2026-07-29).** `LibraryScanner.Scan` no longer unconditionally drops an entry the moment its file goes missing; it now keeps the fingerprints (VDF-style) so content that reappears at a new path can eventually be re-linked rather than re-sampled from scratch — the same mechanism the deferred library-root-move re-linking idea would need. Retrofitted onto already-shipped Phase 1 code (the "resolve before Phase 1 lands" framing in this document's first draft didn't hold — Phase 1 was executed immediately while this decision was still pending — so this landed as a follow-up change, not a same-commit adjustment).

What changed: `LibraryDatabaseEntry` gained `TombstonedUtc` (`DateTime?`, `MemoryPackOrder(8)` — additive, safe under `VersionTolerant`). `LibraryScanner.Scan`'s top-of-method sweep now sets `TombstonedUtc` on any not-already-tombstoned entry whose file is gone, instead of removing it from `database.Entries`; the per-candidate "file vanished mid-run" branch does the same for whichever single entry that candidate maps to. Reappearance at the *same* path clears `TombstonedUtc` back to `null` the moment the cache-hit reuse path (`TryReuse`) accepts it; a full resample naturally starts from a fresh, non-tombstoned entry. Nothing reads `TombstonedUtc` yet — no re-linking, no CLI surfacing, no `ScanSummary` count — this is purely "stop discarding the data," matching the narrow scope decided. `VBR.Tests/Database/LibraryScannerTests.cs` gained two tests (sweep-path and per-candidate-path tombstoning) and a reappearance-clears-it test; `LibraryDatabaseStoreTests`'s round-trip test now also covers `TombstonedUtc`. 69 tests total (up from 67), all passing.

Still open, deliberately out of this retrofit's scope: actually *using* tombstones for re-linking (the library-root-move case, still deferred) and the "Prune Ghost Entries"-style command that would eventually clear out tombstones nobody ever re-links (Phase 3, below, still blocked on the `vbr commit` rename).

### Later, not sequenced in detail here

Bumper CRUD (list/edit/rename/duplicate/delete — `docs/design/clarification-terms-cli.md` §3), catalog-aware "apply" matching, GUI work. These depend on the foundation above but aren't concretely sequenced yet; will get their own planning pass closer to when they're actually next.

### Answered this round (2026-07-28 discussion)

- **Rename vs. edit for bumper labels** — not a structurally different operation given the current GUID-keyed design; the real question was label *uniqueness*, resolved below.
- **Ad-hoc library references don't require a new persisted artifact** — confirmed: *someone* (the user, retyping folder lists) remembers, not *something* (a new stored "library definition"). No new persistence layer needed for Phase 2.
- **Path exclusions are distinct from `.vbr.` filtering** — confirmed, both stay, independently.
- **Portability case 2/6 (library-root-move re-linking)** — still deferred, tentatively; maintainer will revisit closer to v1 rather than now.
- **"Duplicate bumper" is cheap once CRUD exists** — confirmed, pure data copy, no new design needed when the time comes.

### Decided this round (2026-07-29, via scratch notes)

- **`--exclude-folders`** — final name, no change.
- **Delimiter and flag shape** — semicolon-delimited, single flag (not repeatable).
- **Folder-in-multiple-libraries** — allowed; separately-planned change-handling work covers the consequences (see Phase 2 above).
- **`vbr cleanup` → `vbr commit`** — see the dedicated section above; not yet implemented.
- **Tombstoning** — adopt it; see Phase 3 above for the now-retroactive timing note; not yet implemented.
- **Bumper label uniqueness** — **unique within a catalog, not globally.** Two different catalogs may each have a bumper labeled e.g. "Studio ident" without conflict; `BumperCatalogBuilder.AddBumper`/`vbr add-bumper` need a duplicate-label check scoped to the target catalog's own `Entries` before insert. Not yet implemented — `add-bumper` currently allows silent duplicate labels within one catalog (each entry is independently GUID-keyed, so nothing collides at the storage layer; this would be a new CLI/builder-level validation, not a data-model change).
- **Orphaned bumpers (source doc Portability case/handling #9) — no surfacing needed.** `BumperCatalogEntry.SourceVideoPath` is informational provenance metadata only; it stays exactly as-is when the source file it names disappears, and its unresolvability has no effect on the bumper's validity or matching utility. This closes the "unresolved technical consequences" the source doc flagged — the resolution is that there *are* no technical consequences worth building for, given today's data model. No code change implied.

### Open questions

None outstanding as of 2026-07-29 — every item raised in this entry's planning pass has a maintainer decision recorded above. Four decided-but-unbuilt items remain: Phase 2 (multi-folder libraries), the `vbr cleanup` → `vbr commit` rename, tombstoning, and catalog-scoped label-uniqueness enforcement. These are independent of each other (none blocks another except Phase 3's dependency on tombstoning + the `commit` rename landing first) — sequencing among them is an implementation-order choice, not an open design question.

## Bumper catalog — implemented and validated (2026-07-27, planned; 2026-07-28, built)

**Status: implemented and validated.** Built to the plan below (three maintainer feedback rounds,
all open questions resolved) the day after it was written. See "Implementation (2026-07-28)" at the
end for what shipped, real numbers from a live run, and how it differs in small ways from the plan.
This is [ROADMAP.md](ROADMAP.md) Phase 3's remaining half — the fingerprinting/matching/removal side
of Phase 3 was already done (`vbr scan`/`match`/`remove`); the catalog's write side (`vbr
add-bumper`) now exists too. Design groundwork already existed — [ADR 0004](decisions/0004-bumper-catalog.md)
and [`design/bumper-catalog.md`](design/bumper-catalog.md), written 2026-07-15, before almost
everything that now exists to build on. The plan below doesn't replace that design, it grounds it in
what had been built since and proposes (and, as of 2026-07-28, delivers) a concrete, narrow v1.

### What's changed since the catalog was designed

The original design (bumper-catalog.md) treated several things as open implementation questions
that are now effectively pre-answered by precedent, because `vbr scan`'s `LibraryIndex` solved the
*same class* of problem for per-file fingerprints:

- **Storage format:** the design doc speculated "likely a dedicated SQLite DB." What actually got
  built for the library index is MemoryPack (`[MemoryPackable(GenerateType.VersionTolerant)]`,
  magic-header-checked, atomic temp-file-then-move save, per-OS default state folder) — no SQLite
  anywhere in this codebase. The catalog should follow the same pattern for consistency, not
  introduce a second persistence technology for a structurally similar problem.
- **Fingerprint representation:** `bumper-catalog.md` calls for "audio_fingerprint,
  visual_embedding, phash_sequence, duration" per entry — this is *exactly*
  `VBR.Core.Fingerprinting.TimedFingerprint[]` (embedding + pHash, timestamp-tagged) plus
  `AudioFingerprint`/`Duration`, the same types `LibraryIndexEntry` already carries. A catalog
  entry can reuse `TimedFingerprint` directly rather than inventing a parallel shape — which also
  means catalog-vs-index comparison later is just `VisualBumperMatcher.MatchMixedDensity`/
  `MatchMixedDensityPHash`, already built and validated, not new matching code.
- **Sampling mechanism:** adding a bumper needs to fingerprint one short clip (seconds to under a
  minute), not a whole episode — this is exactly what `MixedDensitySampler`/`EdgeDensityProfile`
  already do for `vbr match`/`vbr remove` today (an edge-boundary at or beyond the clip's own
  length makes the whole clip dense, no sparse-middle needed). No new sampling code.
- **Interface contract already exists and is already enforced elsewhere:** "every enrollment/
  matching entry point accepts a source video path + a time range, never a pre-cut clip file" is
  bumper-catalog.md's own rule, and it's exactly `--clip-from`/`--region`/`--clip-length`'s shape,
  already required on `match`/`remove`. Adding a bumper should take the identical three parameters.

None of the *open* questions in ADR 0004/bumper-catalog.md (variants vs. sub-bumpers, auto-queue
confidence threshold, community sharing) are resolved by any of this — those are still open, see
below. What's resolved is the *storage/sampling mechanism* question, by precedent rather than by
new decision.

### Proposed v1 scope: the catalog store + `vbr add-bumper` only

Mirroring how `vbr scan` shipped (the index/write side first; "wire `match`/`remove` to *read* the
cache" stayed a separate, later step) — propose the same split here:

- **In scope:** a persisted, per-library catalog store, and a new `vbr add-bumper` command that
  adds one entry to it from a source video + region + length, exactly like `match`/`remove` extract
  a reference clip today — plus a thumbnail (maintainer request, this revision).
- **Explicitly out of scope for this pass, deferred to a later plan:**
  - **Apply / catalog-aware scanning** — matching a video or library against *every* catalog
    entry (bumper-catalog.md's "video → catalog" direction). This is the actual payoff, but it's a
    second, separable piece of work once entries exist to match against.
  - **Curation** (rename, edit boundaries, merge/split, retire) — no catalog contents exist yet to
    curate; premature before `add-bumper` ships.
  - **Duplicate/variant detection at add time** (maintainer decision, this revision): deferred to a
    future version. If a user adds the same bumper twice, or two near-identical variants, the
    consequences are catalog size and disk space only — not a correctness problem worth guarding
    against in v1.
  - **Sub-bumper parent/child relationships and variant grouping** — real per bumper-catalog.md,
    but adding unused schema fields now (before any code exercises them) is exactly the kind of
    speculative design this project avoids elsewhere. MemoryPack's `VersionTolerant` mode exists
    specifically so these can be added later without a migration; v1 stores each entry
    independently, full extent, no relationships.
  - **Auto-discovery, on-ingest automation, export/import (either mode), community sharing** — all
    explicitly later-phase per ROADMAP.md (Phase 7) already; nothing here changes that.

### Proposed data model

New `VBR.Core.Catalog` namespace, mirroring `VBR.Core.Index`'s existing shape (`LibraryIndex`/
`LibraryIndexEntry`/`LibraryIndexStore`) closely enough to reuse the same patterns wholesale —
MemoryPack `VersionTolerant`, `FormatVersion` carried from entry one, magic-header-checked
load, atomic save:

```csharp
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class BumperCatalog {
    public int FormatVersion { get; set; }
    public string LibraryName { get; set; }          // mirrors LibraryIndex.LibraryName -- catalogs are per-library now
    public Dictionary<string, BumperCatalogEntry> Entries { get; set; } = new();  // keyed by Id
}

[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class BumperCatalogEntry {
    public string Id { get; set; }                  // stable identifier -- a GUID; not meant to be typed by a human, see Label
    public string Label { get; set; }                // required, short, human-facing lookup key -- e.g. "Disney FBI warning 2003"; length-limited, see open questions
    public string? Description { get; set; }         // optional, longer free text for curation context; length-limited, see open questions
    public string[] Tags { get; set; }
    public ClipEdge Region { get; set; }             // reuses VBR.Core.Extraction.ClipEdge (begin|end) directly -- see note below
    public string Status { get; set; }               // "active" | "retired"
    public TimeSpan Duration { get; set; }           // precisely-measured bumper length (ADR 0007's arithmetic-cut assumption)
    public TimedFingerprint[] Fingerprints { get; set; }   // same type LibraryIndexEntry already uses
    public uint[]? AudioFingerprint { get; set; }
    public string ReferenceClipPath { get; set; }    // relative to the catalog's own folder, see Storage
    public byte[] Thumbnail { get; set; }             // embedded directly in the catalog, unlike the clip -- see Thumbnail capture
    public string SourceVideoPath { get; set; }      // provenance
    public DateTime DateAdded { get; set; }
    public int OccurrenceCount { get; set; }         // 0 until "apply"/removal integration exists to update it
}
```

Changes from the first draft, per maintainer feedback:

- **`Category` renamed to `Region`**, and per the maintainer's own reasoning for the rename (it
  should mirror the existing `--region` CLI concept), it's proposed as a direct reuse of
  `VBR.Core.Extraction.ClipEdge` rather than a free string — the stored value *is* whatever
  `--region` was passed as, no separate inference or mapping step needed, which is simpler than
  the first draft's "category defaults to inferring from region." Consequence, raised and
  confirmed acceptable: `ClipEdge` today is only `{ begin, end }` — no way to represent an
  interstitial (mid-video) bumper without extending `ClipEdge` itself later (a broader,
  cross-cutting change — `match`/`remove`/`scan` all use it — beyond just this catalog's schema),
  deferred until interstitial removal itself is built (ROADMAP Phase 5, still later work). **The
  maintainer's caveat on deferring this, resolved, not a gap:** deferring the catalog's
  interstitial *category* must not imply the underlying *library index* only covers edges — it
  doesn't, already. `WholeFileSampler` (built for `vbr scan`, shipped) already merges a
  keyframe-only *whole-file sparse* pass with the dense-edge pass into one fingerprint set per
  file — every `vbr scan`'d library already has sparse coverage of its middles today, independent
  of and unaffected by the catalog's current edge-only `Region`. So when interstitial support is
  eventually built, existing scanned libraries already carry the data it would need — no rescan/
  rebuild required. Nothing to change here; confirming this was already true, not new work.
- **`Notes` replaced with `Description`** — confirmed as a straight replacement (the maintainer
  had missed the earlier `Notes` field in the first draft; `Description` covers the same purpose).
- **`Thumbnail` added, embedded as bytes** (maintainer request) — see "Thumbnail capture" below.
- **`Id` stays a GUID, not a slug** — the maintainer's own reasoning ("if Id is a GUID, then label
  is the only user-friendly way of specifying which bumper") settles the first draft's open
  question: `Id` is internal/stable, `Label` is the human-facing handle a future lookup command
  (e.g. an eventual `--retire <label>`) would key off — which is *why* `Label` needs a sane length
  limit and `Id` doesn't.

### Thumbnail capture

Extract one representative still frame from the reference clip. Proposed mechanism: reuse
`VBR.Core.Fingerprinting.FrameQuality`'s existing "most detailed" heuristic (already built and
used to filter low-information frames out of matching, so it already knows how to avoid picking a
near-black or blank frame) to pick which frame, rather than a fixed offset like the clip's
midpoint, which could land on a fade or a blank moment depending on the bumper. Low-stakes
mechanism choice, easy to change later if it doesn't work well in practice.

**Stored as embedded bytes in the catalog entry (`Thumbnail`, `byte[]`), not a separate file**
(maintainer request) — asymmetric with the reference clip, which stays a separate file on disk.
The asymmetry is deliberate, not an inconsistency: a single still frame is small (tens of KB as a
JPEG) where a video clip is not, so embedding the thumbnail buys single-file portability and no
orphan-file bookkeeping (delete an entry, its thumbnail is gone automatically) at negligible cost,
while doing the same for the clip would make every catalog save rewrite megabytes of video through
`LibraryIndexStore`-style atomic saves' whole-file-rewrite pattern for no real benefit.

**Stored at original/native decoded dimensions, no resize (maintainer decision) — resizing before
storage explicitly deferred**, not rejected: revisit later if catalog size in practice makes it
worth doing.

### Should `add-bumper` reuse fingerprints from an already-`vbr scan`'d file? — analysis, not adopted for v1

Raised by the maintainer: if the source video is already in a library's `LibraryIndex` (a prior
`vbr scan` ran over it), can/should `add-bumper` read its cached fingerprints instead of
re-sampling from scratch?

It's a real idea with a real gap, not a clear win:

- **The speed advantage is smaller than it looks.** A `LibraryIndexEntry`'s dense-edge fingerprints
  would often already cover a short edge bumper — but `add-bumper` still needs to extract an actual
  playable reference clip and a thumbnail image, neither of which the index stores (it's
  fingerprints only, no media). So reusing cached fingerprints would skip the embedding/decode
  step but *not* the ffmpeg extraction step — the more expensive of the two is the one that still
  has to happen either way.
- **Density mismatch risk.** The scan's `--edge-boundary`/`--sample-interval` were chosen for
  whole-library scanning, not tuned per bumper — if a scan's edge-boundary is shorter than this
  specific bumper (Avatar's 47s intro against a 20s default boundary is exactly this case,
  encountered earlier this session), only part of the cached fingerprints would be dense; the rest
  would only have sparse coverage, a lower-quality reference than dedicated fresh sampling.
- **Adds real complexity for a rarely-hot path.** Adding a bumper is a deliberate, infrequent,
  curated action — not a batch operation where shaving decode time matters the way it does for
  scanning hundreds of files.

**Recommendation: always sample fresh in v1**, same as `match`/`remove` do today — simple,
guaranteed-consistent quality, no dependency on whether a scan happened to run first or with
compatible settings. Worth revisiting as a real optimization if a future workflow needs to
*batch*-add many bumpers from an already-scanned library, where the calculus would be different.
Flagged for the maintainer's agreement, not silently decided.

### Proposed storage

- **Per-library, not global** (maintainer correction) — "independent of any particular video"
  (bumper-catalog.md's own framing) means a bumper isn't tied to one *file*, not that the catalog
  should be global across every library a user has. Each library gets its own catalog, mirroring
  `vbr scan`'s own per-library index.
- **Dedicated `--catalog-db-folder`, not shared with `vbr scan`'s `--library-db-folder`**
  (resolved, maintainer decision): `add-bumper` mirrors `vbr scan`'s *pattern* (a name + a folder
  override, sensible defaults if omitted) rather than reusing its exact storage location. Default
  location mirrors `LibraryIndexStore.GetDefaultIndexFolder`'s per-OS pattern at its own dedicated
  path (e.g. `%LOCALAPPDATA%\VideoBumperRemover\catalog\` on Windows, a sibling to `index\`, not
  inside it), filename `{library-name}.vbrcat` — same mechanism as the index, different folder.
- Reference clips live in a `clips/` subfolder next to the catalog file, named by `Id` (e.g.
  `clips/{id}.mkv`) — thumbnails are embedded in the catalog file itself, not stored here (see
  Thumbnail capture above).
- Extraction of the reference clip reuses `ClipExtractor`'s existing seek+extract mechanism,
  writing to the catalog's `clips/` folder instead of a temp path.

### Proposed CLI: `vbr add-bumper`

Renamed from the first draft's `vbr enroll` (maintainer preference). Dropping `--category`
entirely versus the first draft — `Region` is now set directly from `--region`, so there's nothing
left to separately specify or infer.

```sh
vbr add-bumper --clip-from <file> --region begin|end --clip-length <duration> --label <text>
               --library <folder> [--library-name <name>]
               [--description <text>] [--tags <text>]
               [--catalog-db-folder <folder>] [--verbose]
```

- `--clip-from`/`--region`/`--clip-length` — identical meaning and requiredness to `match`/
  `remove`'s existing options (reused, not reinvented).
- `--label` — **required, no auto-suggestion** (maintainer decision, after working through and
  rejecting inferring one from the source filename/folder structure — too many edge cases:
  parent-folder-vs-grandparent show naming, episode codes to strip, no clean general rule). Length
  limit **30 characters, confirmed** (the maintainer's own number, "fine for now").
- `--description` — optional, length limit **255 characters, confirmed**.
- **Both limits enforced at the CLI layer, not the data model** (maintainer decision) —
  `BumperCatalogEntry.Label`/`Description` stay plain, unconstrained strings in `VBR.Core`;
  `VBR.CLI`'s `add-bumper` command validates length and rejects with a clear error before ever
  constructing an entry, the same layering `--index-folder`/`--log-file`'s existing shape checks
  already use (validate in the CLI command, keep the Core model trusting/simple). Rejected, not
  silently truncated, either way.
- `--library`/`--library-name` — identifies which library's catalog to add to, mirroring `vbr
  scan`'s exact option pair and default-derivation behavior (`--library-name` defaults from
  `--library`'s own folder name if omitted). `--library` is not otherwise used by `add-bumper` (no
  enumeration/scanning happens) — it's accepted purely so the name-derivation convenience matches
  `vbr scan`'s, rather than forcing `--library-name` to always be typed explicitly.
- Samples the clip via the existing `MixedDensitySampler` (all-dense, same mechanism `match`/
  `remove` already use), extracts and stores the reference clip and a thumbnail (see above),
  measures `Duration` from the same `ffprobe` call already used elsewhere, generates an `Id`,
  writes the entry.
- **Reporting: plain `--verbose` bool, confirmed** (maintainer decision, this revision) — not
  `vbr scan`'s five-tier `--console-info`/`--log-file`/`--log-level` scheme. Reasoning (maintainer):
  that scheme exists specifically because a library scan can produce enough console output to
  overflow a terminal's scrollback (hit directly this session, 102 files), and `add-bumper` — one
  entry per invocation — will never approach that.
- **No confirmation step, confirmed** (maintainer decision, this revision) — capture and add
  directly, report the result afterward, matching `vbr scan`'s own "just do it, report" style.

### Open questions

None outstanding — every question raised across both feedback rounds is resolved (storage
location, `Region`/`ClipEdge`/interstitial handling, `Notes`→`Description`, the 30/255 length
numbers and where they're enforced, reuse-scanned-fingerprints, the `--library`/`--library-name`
pairing, and the thumbnail's storage shape/dimensions — see "Revision log" for the full history).
Nothing here currently blocks moving from plan to implementation, when that's wanted.

### Explicitly not decided by this plan

Everything under "Explicitly out of scope for this pass" above, plus: the exact shape of "apply"
(catalog-aware `vbr scan`/`match`/`remove`), the removal manifest's catalog-entry link (ADR 0007
already reserves a `MatchDetail` field but no `CatalogEntryId` yet), and how `OccurrenceCount` gets
updated (presumably by a future "apply" pass, not `add-bumper` itself, since adding a bumper
doesn't remove anything).

### Revision log

**2026-07-27, same day, first maintainer feedback round.** Renamed `vbr enroll` → `vbr add-bumper`;
`Category` → `Region` (now a direct `ClipEdge` reuse, dropping the `--category` CLI flag entirely);
catalog changed from one-global-by-default to per-library (a correction, not a refinement — the
first draft misread "independent of any particular video" as "independent of any particular
library"); added a thumbnail field + a proposed capture mechanism; resolved four of the first
draft's six open questions (label auto-suggestion → rejected, explicit required field; `Id` shape
→ GUID, settled; duplicate/variant detection → deferred to a future version; reporting scheme →
plain `--verbose`; confirmation step → none) — leaving two carried forward in modified form and
introducing three new ones (storage-location sharing, `Notes`/`Description` split, the
length-limit numbers) from decisions made this round.

**2026-07-27, same day, second maintainer feedback round.** All six open questions from the first
round resolved: catalog storage gets its own dedicated `--catalog-db-folder` (not shared with
`vbr scan`'s `--library-db-folder`); `--library`/`--library-name` pairing confirmed as proposed;
deferring the `Region` interstitial value confirmed acceptable, with the caveat that `vbr scan`'s
existing whole-file sparse-middle sampling already covers the groundwork independently — no future
library rebuild needed when interstitial support is eventually built; `Notes`→`Description`
confirmed as a straight replacement; 30/255 length limits confirmed, with enforcement explicitly
placed at the CLI layer, not the `VBR.Core` data model; sample-fresh (not reusing scanned
fingerprints) confirmed. New this round: the thumbnail is stored as embedded bytes (`Thumbnail`,
`byte[]`) directly in the catalog entry rather than a sibling file like the reference clip — which
introduces the one new open question (bounding the thumbnail's size before embedding).

**2026-07-27, same day, third maintainer feedback round.** Last open question resolved: the
embedded thumbnail is stored at original/native decoded dimensions, no resize — deferred, not
rejected; revisit if catalog size becomes a real problem in practice. No open questions remain.

### Implementation (2026-07-28)

Built exactly to the plan above — `VBR.Core.Catalog` (`BumperCatalog`/`BumperCatalogEntry`/
`BumperCatalogStore`/`BumperCatalogBuilder`, mirroring `VBR.Core.Index`'s shape precisely: same
MemoryPack `VersionTolerant` convention, same magic-header-checked atomic-save-with-retry, own
dedicated `VBRCAT01` magic and `.vbrcat` extension so an index file can never load as a catalog or
vice versa) and `VBR.CLI.Commands.AddBumperCommand` (`vbr add-bumper`), registered in `Program.cs`.
`--library-name` was promoted from `ScanCommand`-local into `SharedOptions` rather than duplicated
a second time — a direct, mechanical consequence of the plan's own "mirror `vbr scan`'s pair"
decision, not a deviation from it.

**One real refinement the plan didn't spell out:** "reuse `FrameQuality`'s most-detailed heuristic"
for the thumbnail turned out to need two decode passes, not one. The AI-pipeline frames that
heuristic scores are already downscaled to the ONNX model's fixed 224×224 input size — fine for
*picking which timestamp* is most detailed, wrong resolution for the actual stored thumbnail (the
plan's "original/native dimensions" decision). `BumperCatalogBuilder.CaptureThumbnail` scores the
AI-pipeline frames to find the best timestamp, then re-extracts *that* frame from the original
source at native resolution as a single-frame JPEG (`ffmpeg -frames:v 1 -f image2pipe -c:v mjpeg`)
— a second, cheap, best-effort decode, consistent with the plan's "never blocks adding the bumper"
framing for thumbnail failures. The audio fingerprint (Chromaprint) is computed from the
*extracted reference clip*, not the source region directly — simpler than region-aware chromaprint
extraction would have been, and the clip is already being created for the reference-clip field
regardless.

**Live-verified** against a real Daredevil episode's Netflix end-card (`--region end --clip-length
8s`, the exact length ADR 0007 independently measured for this same card): 27 frames sampled dense
@ 0.2s, 17 usable after low-information filtering, real reference clip extracted (369,882 bytes),
real audio fingerprint (9 Chromaprint blocks), real thumbnail (30,266 bytes, native resolution, via
the two-pass mechanism above), duration measured at exactly 8s from the extracted clip. A second
`add-bumper` call against the same library (a different episode's card) correctly accumulated as a
second entry — catalog file grew from 37,773 to 74,976 bytes, both reference clips intact, nothing
overwritten. Both CLI-layer validations (label over 30 chars, `--catalog-db-folder` pointing at an
existing file) fail fast with clear errors, mirroring `--library-db-folder`/`--log-file`'s
established shape.

**Tests:** 14 new (67 total, up from 53), all passing — `BumperCatalogStoreTests` (round-trip
serialization including `Thumbnail`/`AudioFingerprint`/full header, atomic-save, wrong-magic
rejection including cross-store confusion, retry-then-throw, path derivation, default-folder
sibling-not-inside-the-index-folder check) and `BumperCatalogBuilderTests` (the three validation
paths that throw before ever touching the ONNX model — nonexistent source, non-positive clip
length, clip length exceeding a real probed source duration — kept AI-model-free and fast, same
philosophy as the rest of this project's unit suite; full pipeline correctness is the live
verification above, same division as `LibraryScannerEquivalenceTests`).

**Unchanged from the plan, still explicitly out of scope:** apply/catalog-aware scanning,
curation, sub-bumper relationships, auto-discovery, on-ingest automation, export/import, community
sharing. `vbr add-bumper` only ever writes one new entry; nothing reads the catalog back yet.

### Post-ship simplification — `--library`/`--library-name` → `--catalog-name` (2026-07-28)

Found by the maintainer re-reading the shipped `running_and_building.md` doc, as both maintainer
*and* co-author of the original plan: the option names were confusing even with full context.
Tracing the actual code confirmed why. This project now has (at least) three different things that
"library" could mean:

1. **A media library** — a user's mental model of their own video collection, potentially spanning
   multiple parent folders, potentially several distinct collections kept deliberately separate
   (e.g. "ripped discs" vs. "downloaded clips"). `match`/`remove`/`cleanup`'s ad-hoc `--library
   <folder>` is a thin, single-folder approximation of this.
2. **A `vbr scan`-persisted library** — one named, cached fingerprint index tied to exactly one
   folder tree, via `--library`/`--library-name`.
3. **A bumper catalog** — tied to concept 1 in the sense that a bumper is *found* within some
   media collection, but not necessarily to concept 2 (nothing requires scanning first), and not
   even durably to concept 1 either: a catalog built from one collection can legitimately be
   applied to a different one later.

`add-bumper` originally reused concept 2's exact `--library`/`--library-name` option pair — which
implied a catalog belongs to one scanned library (concept 2), collapsing it into a narrower box
than concept 3 actually needs. Tracing the code confirmed the implementation had already drifted
past what that pairing even bought: `--library`'s value was read in exactly one place (as a
fallback source for deriving a name when `--library-name` was omitted) and used for nothing
else — never enumerated, never validated to contain `--clip-from`, not read at all once
`--library-name` was given. It was `Required = true` for no functional reason.

**Fix:** removed `--library` from `add-bumper` entirely; `--library-name` renamed to
`--catalog-name` and made unconditionally required (no folder left to derive a default from, and a
catalog's name should be as deliberately chosen as `--label` already is — no auto-suggestion
there either). `BumperCatalog.LibraryName` renamed to `CatalogName` for the same reason internally,
not just at the CLI surface. Net effect: `add-bumper` drops from three storage-identity flags to
two (`--catalog-name`, `--catalog-db-folder`), and a catalog's identity is now fully independent of
any media folder — matching concept 3 above, not concept 2.

**Not done here, explicitly deferred to a separate, larger effort:** whether `vbr scan`'s own
`--library`/`--library-name` naming deserves the same reconsideration (concept 1 vs. concept 2 is
blurry there too, by the maintainer's own read of that section's docs), and whether "a scanned
library" should eventually support multiple parent folders rather than one tree (raised by the
maintainer, not designed). The maintainer is assembling a comprehensive cross-command terminology
plan separately; this fix is scoped to `add-bumper` only, per explicit instruction not to touch
`scan` in the meantime.

**Live-verified**: `--help` no longer shows `--library`/`--library-name`; a full run with
`--catalog-name` reproduces the same real numbers as the original implementation (17 fingerprints,
real clip/thumbnail/audio-fingerprint); omitting `--catalog-name` fails with `System.CommandLine`'s
own clear "Option '--catalog-name' is required" error. Tests updated for the field rename (67
still passing, no count change — this was a rename, not new coverage).

## Removal re-encode defaults — codec-matched output, decided but not yet built (2026-07-27)

**Status: documented design decision, not yet implemented.** The maintainer asked to discuss and
document only — no code changes follow from this entry. Prompted by the maintainer's own manual
`vbr remove` testing: default re-encode output was 2-3x the source file size, large enough on a
big library to matter.

**Root cause.** `VBR.Core.Removal.ClipRemover` forces every source to a fixed
`libx264 CRF 18 preset medium`, regardless of what the source actually is — already flagged in
that file's own doc comment as "not a considered choice," and listed verbatim as an open item in
[ADR 0007](decisions/0007-removal-command.md)'s "Open questions" ("real codec choice... matching
the source codec instead of a fixed one?"). This entry resolves that item. CRF 18 alone is
"visually near-lossless" — deliberately large. But the bigger factor for this maintainer's library
specifically: a real `ffprobe` check (this session) against files that surfaced earlier bugs
confirmed a genuine codec mix — Daredevil is H.264 (Main profile, 8-bit, confirmed directly),
while the maintainer's broader library skews H.265/AV1, some from 10-bit/HDR sources. Re-encoding
an HEVC or AV1 source to H.264 at a near-lossless CRF stacks a codec-family bitrate penalty (HEVC
needs roughly half AVC's bitrate for equivalent quality; AV1 typically needs less again) on top of
an already-generous quality target — a fully expected, non-buggy cause of 2-3x growth.

**Decision: match output codec/bit-depth to source; don't build a transcoder.** Per the
maintainer's framing: either the library owner already tuned their rip's format carefully, or it's
as-extracted and this project's job is not to replace HandBrake — matching what's already there is
the sensible default either way, for both codec and bit depth.

Defaults table (source codec detected via the `ffprobe` call `ClipRemover` already makes for
duration probing):

| Source codec | Output encoder | CRF | Confidence |
|---|---|---|---|
| H.264 | `libx264` | 22 | Solid — matches HandBrake's own default |
| H.265/HEVC | `libx265` | 24 | Solid — matches HandBrake's own default |
| VP9 | `libvpx-vp9` | 31 | First-class, at the maintainer's request (a friend's library is VP9-heavy from YouTube downloads) — less standardized than x264/x265 but common enough to support properly rather than fall back |
| AV1 | *(deferred — see below)* | — | Not built this pass |
| Anything else (MPEG-2, VC-1, XviD, etc.) | `libx264` | 22 | Universal fallback — matches today's existing default, so an old/unrecognized source doesn't break |

**The CRF-scale trap, worth recording explicitly** (this cost real re-encodes before being
caught): CRF is not a shared scale across encoders — a given CRF number means a different quality
target in different encoders. HandBrake itself uses CRF 22 for its H.264 preset and CRF 24 for its
HEVC preset, not the same number, for exactly this reason; the table above mirrors that precedent
rather than inventing new numbers. The same trap applies to **presets**: x264/x265 use named
presets (`slow`, `medium`...) where "slower" is unambiguous, but SVT-AV1 uses numeric presets
0-13 where *lower* is slower/higher-quality — the opposite direction from what "slow" suggests.
Any future AV1 work needs its own preset value, not a reused string or an assumed-equivalent
number.

**No user-facing configuration for any of this in v1** — no CLI flags, no config file, no named
presets (`slow`/`fast`/`HQ` etc. were considered and explicitly rejected). The only escape hatch
remains `--re-encode false` (existing stream-copy mode) — a different trade-off entirely
(keyframe-bound cuts, no frame accuracy), not a size/quality knob. Accepted as a real v1 gap, not
an oversight: revisit if it becomes a practical pain point rather than a theoretical one. A
config file for user overrides (codec/container/CRF) was discussed and explicitly deferred for the
same "not replacing HandBrake" reasoning — a real future direction, not designed.

**Preset for the baked-in defaults — recommended, not yet a confirmed decision:** re-encode is
already the deliberately-slow, frame-accurate path (chosen over stream-copy specifically for
that), and with no user override, the one preset value picked matters more than a "default among
options" normally would. Leaning toward `slow` over `medium` for the x264/x265 rows (better
compression at the same CRF) — not yet finalized, and VP9's own preset/speed mechanism
(`-deadline`/`-cpu-used`) still needs its own value picked separately.

### AV1 — explicitly deferred, not "unsupported"

The maintainer's own library is mostly AV1/H.265; 2 AV1 shows are currently held back from
processing specifically because re-encoding is slow and AV1 hardware support isn't mature yet on
their end. Given that, AV1 support is deferred rather than rushed:

- AV1's CRF scale (0-63) is genuinely less standardized than x264/x265's — `libaom-av1` and
  `libsvtav1` don't agree with each other at the same CRF number, unlike the well-established
  x264/x265 relationship. Recommended when this is picked back up: empirically test 2-3 real AV1
  samples at candidate CRF/preset values (size + eyeballed quality) rather than trust a number
  from general lore, given how much of the maintainer's library would depend on it.
- **Encoder availability is a real risk, not just a quality question.** `libsvtav1` (the fast,
  modern encoder most tools now prefer) must be compiled into the user's ffmpeg build
  (`--enable-libsvtav1`) — not guaranteed present. The maintainer believes `libaom-av1` (the
  reference encoder) has been in default ffmpeg builds for a while, which would make it a safer
  universal baseline; `libsvtav1` would need a runtime check (`ffmpeg -encoders`) with fallback to
  `libaom-av1`, and if neither exists, fall back to `libx264` with a warning rather than fail deep
  into a multi-hour encode.
- **Until AV1 support exists, an AV1 source falls through to the generic fallback row**
  (`libx264` CRF 22). Worth flagging plainly: re-encoding AV1 source through that fallback will
  very likely bloat the file even more than the original H.265→H.264 problem, since AV1 is
  typically more efficient than HEVC too. Not yet decided whether this should print an explicit
  warning when it happens (recommended) or proceed silently like any other unmatched-codec
  fallback — open, pending the maintainer's call whenever AV1 support is actually built.

**Open question, explicitly parked (maintainer, 2026-07-27): should VBR bundle its own ffmpeg**
(guaranteeing `libsvtav1` and known-good encoder availability) **rather than relying on whatever
the user's system ffmpeg provides?** Not investigated or decided — noted here so it isn't lost.

### HDR — more than bit depth, and the failure mode is worse than oversized files

Matching `pix_fmt` (8-bit vs. 10-bit) is straightforward — same mechanism as codec-matching,
probe and mirror. But bit depth alone doesn't preserve HDR:

- **Color metadata** (`color_primaries`, `color_trc`, `colorspace`) needs to be explicitly carried
  through as output flags — skipping this can produce a technically-10-bit output that a player
  displays as SDR (washed out, wrong contrast), arguably worse than a clean 8-bit SDR encode, not
  just "not as good."
- **HDR10's mastering-display and content-light-level metadata** (`-master_display`, `-max_cll`)
  is extractable via ffprobe's `side_data_list` and re-injectable via ffmpeg — needed for correct
  player tone-mapping.
- **Dolby Vision is a separate, harder case** — its RPU metadata isn't preserved by a standard
  re-encode pipeline at all; proper handling needs external tooling (e.g. `dovi_tool`) to extract
  and reinject it around the encode. Out of scope for this pass.

**Decision (maintainer, 2026-07-27): detect what we can, preserve what we can confidently
preserve (HDR10-style color + mastering-display metadata), and refuse or warn rather than silently
downgrade anything we can't** (Dolby Vision specifically). The maintainer doesn't know whether
their own 4K HDR Blu-ray rips carry this metadata correctly today, but the design goal is
explicitly protective: don't risk a correctly-authored HDR library on an unverified assumption.
Needs real empirical verification (encode a real HDR sample, inspect the output's metadata via
ffprobe, confirm a player actually reads it as HDR) before it ships — not just flag-passing that
looks right on paper.

### Not built yet

Everything above is a documented decision, not a code change. `ClipRemover.cs`'s existing fixed
placeholder (`libx264 CRF 18 preset medium`, always) is unchanged for now. See also: [ADR
0007](decisions/0007-removal-command.md)'s updated "Open questions" and
[`removal-pipeline.md`](design/removal-pipeline.md)'s updated encoding-defaults section.

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

**Post-ship fix — index-save resilience (2026-07-26):** the maintainer's own testing of per-file
failure handling hit an unhandled `UnauthorizedAccessException` that crashed the whole scan. Root
cause was *not* a video file — the per-file `try`/`catch` around sampling already handled that
correctly — but `LibraryIndexStore.Save`'s atomic `File.Move`, called unprotected from both of
`LibraryScanner.Scan`'s save sites (the `finally`-block checkpoint and the final save). A transient
lock on the just-written index file (antivirus scanning it, or another `vbr scan` racing the same
index path) was enough to abort the entire run — exactly backwards from decision 7's resilience
intent. Fixed two ways: (1) `LibraryIndexStore.Save` now retries the rename itself (4 attempts,
150ms apart) before giving up, riding out the common transient case; (2) `LibraryScanner.Scan` now
catches a save failure that survives the retries the same way it already catches a per-file
failure — logs it (`Logger.Warn`, unconditionally, not gated on `--verbose`) and continues. The
index stays correct in memory regardless of a failed save, so the next successful save (a later
checkpoint, or the final one) persists everything accumulated since. `ScanSummary` gained
`IndexSaveError` so the *final* save's outcome specifically is never silently lost —
`ScanCommand` reports it as a loud, distinct error (exit code 1) separate from the normal per-file
failure tally, since "the scan ran but nothing was persisted" is a materially different problem
than "one file couldn't be read." Two new tests:
`LibraryIndexStoreTests.Save_RetriesThenThrows_WhenTheDestinationIsAnExistingDirectory` (Save's own
retry-then-surface contract, using an existing directory at the destination path to force the same
`UnauthorizedAccessException` deterministically) and
`LibraryScannerTests.IndexSaveFailure_DoesNotStopTheScan_AndIsReportedOnTheSummary` (every
checkpoint attempt fails, the scan still processes every file and reports `IndexSaveError`).

**Post-ship fix #2 — directory-valued `--index` fails fast instead of silently never saving
(2026-07-27):** found immediately after the fix above, on a real large scan
(`--index "...\test_materials\"`, trailing separator). `FileInfo.FullName` preserves a trailing
separator verbatim, so `ResolveIndexPath` passed the *directory itself* through as `path` — `Save`'s
temp file resolved to `"{that directory}\.tmp"` (a real file with no other name, which is what
tipped this off — not `library.vbridx.tmp`, just `.tmp`, sitting directly in the folder), and the
final rename's destination was the directory itself. Confirmed via a standalone repro (`File.Move`
of a temp file onto a real directory, 6 attempts back to back): **every single attempt fails**, not
intermittently — so the retry logic added in fix #1 was working exactly as designed, it just had
nothing transient to ride out. Net effect: a scan in this state runs to completion (doesn't crash)
but persists *nothing*, for however long the run takes — the failure mode fix #1 was built for
(save hiccups, keep going, catch up later) doesn't apply when the destination can never work at all.
Fixed by rejecting this case up front: `LibraryIndexStore.IsDirectoryLikePath` (trailing separator,
or an already-existing directory) is checked in `ScanCommand` right after resolving `indexPath` —
before the AI-component download, before `Load`, before any candidate is touched — printing a clear
error with a suggested corrected filename and exiting, rather than letting an entire run's sampling
work be silently unpersisted. Deliberately a hard error, not an auto-detect-and-use-this-folder
fallback: `--index`'s contract stays "a file path, used verbatim" (unchanged from decision 13)
rather than growing implicit directory-vs-file inference. New tests:
`IsDirectoryLikePath_TrailingSeparator_IsTrue`, `IsDirectoryLikePath_ExistingDirectory_NoTrailingSeparator_IsTrue`,
`IsDirectoryLikePath_OrdinaryFilePath_IsFalse`. Live-verified against the exact reported command —
exits in ~3s with the corrected-filename suggestion, versus running indefinitely and saving
nothing.

**Post-ship simplification #3 — `--index` → `--index-folder`, file name no longer independently
settable (2026-07-27):** after hitting fix #2 live, the maintainer's read was that `--index` (a
full file path) coexisting with `--library-name` (which *also* implicitly named the default file)
was the actually-confusing part — two inputs that could each look like they controlled the file's
name, only one of which really did. Resolved by removing the ambiguity rather than only guarding
against it: `--index` is renamed to `--index-folder` and is unambiguously a folder now —
`LibraryIndexStore.ResolveIndexPath` always derives the file name from `--library-name`
(`{sanitized library name}.vbridx`); `--index-folder` only ever names the containing folder, and
doesn't need to exist yet (created on first save, same as before). This retires fix #2's
directory-vs-file confusion as a *class*, not just this one instance — a folder is all
`--index-folder` can mean, so there is no longer a "did the user mean a file or a folder" question
to get wrong. Considered and rejected: keeping one flag and auto-detecting file-vs-folder from
what's already on disk — that just trades one implicit-inference bug for another. The one new
failure mode — `--index-folder` pointing at a path where a *file* already sits — is checked up
front the same way fix #2's was (before any scanning starts), and reported clearly rather than
attempted; `IsDirectoryLikePath` (fix #2's validator) is removed as dead code, since nothing needs
it once `--index-folder` can no longer be misread as a file path. Tests updated:
`ResolveIndexPath_ExplicitFolder_FileNameAlwaysDerivedFromLibraryName` (three separator variants —
`Path.Combine` avoids doubling a trailing separator but doesn't normalize *which* character it is,
so a trailing `/` legitimately survives into the result) replaces the old `..._UsedVerbatim` test.

**Post-ship feature — `--console-info`/`--log-file`/`--log-level` (2026-07-27):** maintainer
request, made while running a real large scan and finding the existing plain/`--verbose` split too
coarse. New `ScanReportLevel` enum (`quiet < info < debug < verbose < trace`), independently applied
to two destinations: `--console-info` (console; default `info`, `--verbose` remains as shorthand for
`--console-info verbose`, an explicit `--console-info` wins if both are given) and `--log-file`/
`--log-level` (an appended-to file; default level `verbose`, default path sibling to the index file,
same library name with a `.log` extension). `quiet` is nothing; `info` is today's single updating
`x/total` counter (console only — a file can't usefully self-overwrite, so at `info` the file just
carries the start/end lines); `debug` is a name+result line and an `x/total` line per file, no
`Logger`-sourced detail; `verbose` is `debug`'s lines plus the underlying model-load/frame-count/
checkpoint-save(-failure) detail that `Logger` raises; `trace` is reserved — no statement in the
codebase logs at that granularity yet, so it behaves identically to `verbose` today, called out
explicitly rather than invented for the occasion. The final summary and any error always print
(and get mirrored into the log file) regardless of level, on the reasoning that "quiet" should mean
no *progress* noise, not "might hide a real error."

Two things had to be correct for the independence to actually work, not just look like it: (1)
`Logger`-sourced detail (model load, checkpoint saves/failures) is only *raised* as an event at all
when `LibraryScanner`/`WholeFileSampler` are constructed with logging enabled — so `ScanCommand` now
passes `consoleLevel >= verbose || fileLevel >= verbose`, not the console level alone, or a verbose
file level with a quiet console would silently lose everything `Logger` would have carried into the
file. (2) the file's own `Logger` echo needed its own independent subscription rather than reusing
the console's — `SharedOptions.SubscribeVerboseLogging` was generalized into a
`SubscribeLogging(bool, Action<string>)` it now wraps, so both destinations share the same
event-formatting logic without duplicating it.

`--log-file` gets the same up-front directory-shape validation `--index-folder` had before fix #3
retired the need for it there (trailing separator or an existing directory at that path) — the same
class of mistake, just on a new option, so worth guarding proactively rather than waiting to
rediscover it. Log-file writes mirror `Logger.Add`'s own resilience pattern exactly: open-append-
write-close per line (not one held-open handle for the whole run) with `IOException`/
`UnauthorizedAccessException` swallowed — a log write must never be allowed to crash the scan, given
the whole point of the last two fixes was making saves resilient in the first place.

Live-verified (Release build, isolated scratch library — the maintainer's own real scan was running
against `test_materials` at the time under the old Debug build, so verification deliberately avoided
both that data and that binary): default/`quiet`/`debug`/`verbose` console output all match spec
exactly; `--log-level quiet` produces no file; an explicit `--log-file` is honored over the default
path; `--log-file` pointed at an existing directory fails fast with a clear error; a `debug`-level
console run alongside the (default) `verbose`-level file confirmed the two destinations really are
independent — the file captured full `Logger` detail the console never showed. No `VBR.Core` changes
were needed — this is entirely a `VBR.CLI` reporting change, so no new unit tests (consistent with
this project's existing CLI-command-wiring coverage, which is verified live rather than unit tested).

**Post-ship rename #4 — `--index-folder` → `--library-db-folder` (2026-07-27):** maintainer
request, no functional change — `ScanCommand`'s `IndexFolder`/`indexFolderArg` identifiers renamed
to `LibraryDbFolder`/`libraryDbFolderArg` to match. `LibraryIndexStore.ResolveIndexPath`'s own
parameter/doc-comment naming (`explicitFolder`, "decision 13") is unchanged — that's `VBR.Core`
describing the general concept, not the CLI's specific spelling of it, and the two are allowed to
differ. Live reference docs (`running_and_building.md`, `matcher-spec.md`) and the `LibraryIndexStore`
doc comment updated to the new name; the *historical* narrative entries above ("Post-ship
simplification #3" and its `PROGRESS.md` counterpart) describing the original `--index` →
`--index-folder` rename are left as written, since they accurately record what the flag was called
at the time each entry was made — not revised into a name that didn't exist yet when that decision
happened.

**Manual verification pass (2026-07-27):** maintainer ran `vbr scan` by hand against real conditions
beyond the automated suite's coverage. All seven confirm existing behavior works as designed or
land on an explicit, recorded decision (see below) — nothing outstanding from this pass.

- **A file moves within an unchanged library root** — handled correctly.
  `LibraryIndexKey.Normalize` keys entries by absolute path; a moved file's old key is dropped by
  `Scan`'s stale-entry cleanup (`!File.Exists(kv.Value.Path)`), and the new path has no cache hit,
  so it's sampled fresh. This is the documented v1 design (`LibraryScanner`'s own class doc comment:
  "a moved file just re-samples as if new, a conscious v1 simplification, not an oversight") —
  confirmed against a real move, not just reasoned about.
- **The library root itself also moves** — every video is treated as new (full re-sample), since
  every file's absolute path changes at once instead of just one file's. Same mechanism as above,
  just triggered library-wide simultaneously. **Deferred, by maintainer decision** — see "Open:
  library-root moves
  invalidate the whole cache" below.
- **Non-video files in the library path** (text/JSON/audio/images) — correctly ignored.
  `SharedOptions.ResolveCandidates` filters every candidate through `ClipExtractor.VideoExtensions`
  before anything else ever sees it.
- **`.vbr.` outputs** — correctly excluded by default and correctly included with
  `--include-vbr-outputs`, confirming both directions of the `.vbr.`-exclusion design (added
  mid-planning at the maintainer's request) against real `.vbr.` files, not just synthetic ones.
- **Corrupt files** — logged as a per-file error, scan continues. The per-file `try`/`catch` in
  `LibraryScanner.Scan` (already unit-tested against synthetic non-video content) holds up against a
  real corrupt file too.
- **Subtree traversal** — works. `ResolveCandidates`'s `EnumerationOptions { RecurseSubdirectories =
  recurse }` (on by default; `--no-recurse` opts out).
- **File permission failures** — confirms the fix from earlier this session holds, and reveals it's
  actually two layers deep: `IgnoreInaccessible = true` on `ResolveCandidates`'s own enumeration (a
  file/folder that can't even be *listed* is silently skipped, never an error) sits in front of the
  per-file `try`/`catch` plus index-save resilience (`ScanSummary.IndexSaveError`) fixed after the
  maintainer's original `UnauthorizedAccessException` report.

**Open: library-root moves invalidate the whole cache — deferred (maintainer decision, 2026-07-27).**
The gap: `LibraryIndexKey`/`LibraryIndexEntry.Path` are absolute paths with no move/rename relinking
(by original v1 design, see above) — fine for a single file, but a library-root move (new drive
letter, reorganizing a folder tree) invalidates *every* entry at once, forcing a full re-scan of a
library whose content hasn't actually changed at all.

Three options were weighed: (1) a content-hash relink — when a scan finds an unresolved candidate,
match it against orphaned entries by `FileSize`+`OsHash` (already stored per entry, no new fields
needed) and relink instead of re-sampling, mirroring VDF's own deferred `TryRelinkMovedFile`; (2)
store paths relative to `--library` instead of absolute, so a root move is a non-event as long as
internal structure is unchanged, at the cost of a bigger change to the persisted key/format
semantics; (3) defer — keep today's full-re-sample behavior, document it as an accepted v1
limitation. **Decision: (3), defer.** Full re-sample after a library-root move remains today's
behavior; revisit if it becomes a real practical pain point rather than a theoretical one.

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
