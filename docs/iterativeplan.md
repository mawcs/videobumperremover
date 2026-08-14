# Iterative Plan Document

This document catalogs planning concepts as we iterate in development. Newest plan goes at the
top, under its own second-level heading; older plans stay below under theirs, kept for historical
reference rather than deleted or overwritten.

## Per-bumper matching strategy — concrete design (2026-08-13, revised, built)

**Status: built and live-verified (2026-08-13).** Revised twice per the maintainer's own
read-throughs before implementation started: the first pass reshaped all four changes (below); a
second pass corrected two things in that draft — `Trim` moved from a `remove` mode to its own
standalone top-level command, and Group B of the threshold-override change turned out to be a
metadata/provenance request that had been misread as a live matching override, fixed to the simpler
shape actually built below. `--dump-frames` for library-backed candidates was raised and explicitly
shelved in the same conversation ("doesn't feel that much better... let's put that on the shelf for
now") — not part of this entry, not forgotten, just deliberately parked.

All nine TODO items below are done: schema, `MatchingStrategy` resolution, Group A overrides,
`RemovalLength`, Group B capture + per-entry staleness, the standalone `trim` command, `add-bumper`
CLI options for all of the above, a full solution build (0 errors), and `VBR.Tests` (98 passed, 5
pre-existing environment-gated skips, 0 failures). One schema correction made *during* build, not
part of the original design: `BumperCatalogEntry` briefly grew three redundant `double?` fields
(`MinDetail`/`DarkOverrideDetail`/`DarkRejectPercent`) alongside the real Group B mechanism
(`FrameQualitySnapshot`) — leftover from before the Group B correction, never wired into anything,
doc comment even mislabeled them "Group A." Removed before anything shipped; `FrameQualitySnapshot`
alone is Group B, per the design below.

Live-verified end to end (real media, this session): `add-bumper` with every new flag
(`--matching-strategy audioonly --removal-length 3s --presence-threshold-override 0.85
--audio-min-similarity-override 0.7`) correctly stored and echoed the entry; `remove --bumper-label`
against that same entry correctly ran audio-only (no visual/pHash sampling at all, confirmed via
`--verbose`); `trim --length --region --paths` correctly cut both begin- and end-region files via
the same `ClipRemover.Remove` mechanism `remove` uses, including its existing keyframe-bound
stream-copy behavior and CPU/GPU re-encode paths, with results consistent with `remove`'s own
already-established behavior on the same file.

### Motivation (see the Analysis entry below for the full evidence; summarized here)

1. **Thin/flowing-text bumpers**: dumped-frame analysis on every candidate concluded, with real
   evidence, that current visual detection (DINOv2 frame embeddings) cannot identify this content —
   confirmed, not assumed. Audio identifies it cleanly (100%/98% similarity). Visual/pHash need to
   be *excludable* per bumper, not just corroborators that sometimes lose a vote.
2. **All-black/near-black trailing segments** (the 14.7s case): nothing to fingerprint at all —
   this was never a matching problem.
3. **Cross-fades**: the region needed to *identify* a bumper reliably (13s) can differ from the
   region that needs to be *removed* to fully strip a transition (15s) — one `Duration` field
   serving both purposes today is the gap.
4. **"Dark Motion End Bumpers"/"Content Music"/"Content Audio"**: overwhelming visual+pHash
   agreement vetoed by a single global `audioMinSimilarity` that doesn't fit every bumper's real
   audio characteristics — motivates per-bumper threshold overrides, not just strategy selection.

### Change 1 — `Trim` is a standalone top-level command, not a `remove` mode or a catalog concept

**Revised again per the maintainer: "The trim functionality should probably be its own top-level
command instead of a subset of `remove`. There are enough differences in the command structure for
the user to see it as a separate command."** Correct, and consistent with how this project already
treats genuinely different operations (`match`/`remove`/`commit`/`scan`/`add-bumper`/`list-bumpers`
are all separate commands, not modes bolted onto one another) — `remove`'s existing option surface
(`--detection-mode`, `--presence-threshold`, `--dump-frames`, `--catalog-db`, ...) is entirely
irrelevant to an unconditional trim, and a dedicated command means the `--help` output for this
feature only ever shows options that actually apply.

**New top-level command, working name `vbr trim`:**

- `--length <duration>` (required) — how much to cut.
- `--region begin|end` (required) — which edge.
- `--paths <semicolon-delimited list>` (required) — **replaces `--library`/`--file`/`--library-db`
  entirely for this command**, per the maintainer's own simplification: "instead of supporting
  'library' or anything like that, it should just support a semicolon-delimited list of paths of
  either files, or parent folders." One option, mixed freely — each entry is checked for
  `File.Exists` (used directly, no extension filter, same trust convention `--file` already uses
  elsewhere) vs. `Directory.Exists` (walked for recognized video files, same as `--library` does
  today) — needs a new parser (not a reuse of `SharedOptions.ParseFolderListArg`, which only
  understands folders), deduplicated across overlapping entries the same way `ResolveCandidates`
  already does.
- `--no-recurse` — applies to any folder entries in `--paths`.
- `--exclude-folders` — still useful if a `--paths` folder entry has a subfolder to skip; reuses the
  existing shared option/`IsUnderAny` mechanism.
- `--re-encode`/`--output`/`--verbose`/`--hardware-accel`/`--no-native-ffmpeg-binding` — reused as-is,
  since a real cut still happens via `ClipRemover.Remove`.
- **Not present at all**: `--clip-from`, `--bumper-label`, `--catalog-db`, `--library-db`,
  `--detection-mode`, `--dump-frames`, `--sample-interval`, and every presence-threshold option —
  none of the matching surface exists for this command, by design.

Execution is simple by construction: resolve `--paths` to a candidate file list, and for every one
of them, call `ClipRemover.Remove` directly with `--length`/`--region` — no `MatchingSession`, no
fingerprints, no ONNX/ffmpeg decode beyond the cut itself. The same real trade-off from the first
draft still applies and is still worth being deliberate about, just now scoped to whatever
`--paths` names rather than `--library`: every resolved candidate is trimmed unconditionally, with
zero content verification, so a folder entry containing files that don't actually have the
described segment would be silently truncated too. That's inherent to what "no matching, on
purpose" means, not a flaw to design around.

### Change 2 — `BumperMatchingStrategy` expanded to seven values

```csharp
public enum BumperMatchingStrategy {
    Corroborated,  // default: every signal that ran and applies must agree (today's behavior)
    VisualOnly,
    AudioOnly,
    PhashOnly,
    NoVisual,
    NoAudio,
    NoPhash,
}
```

Implementation note: rather than a seven-way switch spreading through matching code, this maps
cleanly onto three independent internal flags — `UseVisual`/`UseAudio`/`UsePHash`, each defaulting
`true` for `Corroborated` — resolved once, where `MatchingSession.PrepareFromCatalogEntry` reads the
entry, into the same `Wants*` overrides the first draft's `AudioOnly` already used:

| Strategy | UseVisual | UseAudio | UsePHash |
| --- | --- | --- | --- |
| `Corroborated` | ✓ | ✓ | ✓ |
| `VisualOnly` | ✓ | ✗ | ✗ |
| `AudioOnly` | ✗ | ✓ | ✗ |
| `PhashOnly` | ✗ | ✗ | ✓ |
| `NoVisual` | ✗ | ✓ | ✓ |
| `NoAudio` | ✓ | ✗ | ✓ |
| `NoPhash` | ✓ | ✓ | ✗ |

Every one of the seven leaves at least one signal active by construction, so no "excluded
everything" validation is needed for this specific value set — worth a defensive comment if an
eighth value is ever added carelessly. **Overrides `--detection-mode` outright for this bumper**,
same as the first draft's `AudioOnly` already did (e.g. a `VisualOnly` bumper samples/consults only
visual even under `--detection-mode all`) — kept consistent across all seven rather than trying to
*intersect* with whatever `--detection-mode` separately requested, which would create dead
combinations (e.g. `--detection-mode visual` + a bumper strategy of `AudioOnly` would leave nothing
able to decide at all if the two didn't override cleanly).

### Change 3 — per-bumper threshold overrides: two genuinely different groups, one with a real catch

**"I want all the parameters and thresholds stored in the bumper... If we're doing this, we're doing
this."** Seven new nullable fields on `BumperCatalogEntry`, each `null` = inherit
`VbrConfig.Current`'s global value, non-null = override for this bumper specifically: `MinDetail`,
`DarkOverrideDetail`, `DarkRejectPercent` (`double?`), `PresenceThreshold`, `RigidHitThreshold`,
`PHashPresenceThreshold`, `AudioMinSimilarity` (`float?`). These split into two groups that behave
very differently, and the difference matters enough to call out explicitly rather than build both
the same way:

**Group A — comparison-time thresholds (`PresenceThreshold`, `RigidHitThreshold`,
`PHashPresenceThreshold`, `AudioMinSimilarity`): clean, no caveats.** These only decide whether an
*already-computed* similarity score counts as "present" — they never affect what gets sampled or
stored. `entry.PresenceThreshold ?? VbrConfig.Current.Matching.PresenceThreshold` (etc.) at the
point `MatchingSession.PrepareFromCatalogEntry` builds its comparison thresholds is the whole
implementation. Works identically for ad hoc and database-backed candidates, since nothing about how
candidate fingerprints were *computed* changes — directly fixes the Finding-3-style "audio veto"
false negatives with no structural downside.

**Group B — `frameQuality` values (`MinDetail`, `DarkOverrideDetail`, `DarkRejectPercent`): corrected
— this is a provenance/metadata request, not a live override, and I initially misread it.** The
maintainer's own clarification: *"These are 'knobs' I played with during `--add-bumper` and they had
an effect. If they are modified for the purpose of 1 bumper within a catalog, those values need to
be recorded in the bumper... I get that they don't change how the detection works, but they do
affect how the bumper is created, and therefore, how they are stored. They absolutely must be in the
bumper file, if for nothing else, but to serve as metadata. I was not suggesting that they somehow
magically become part of detection."*

This is a materially simpler ask than a live per-bumper override, and it's **not new scope** — it's
the exact same mechanism as `FrameQualitySnapshot` (2026-08-12/13 entries), just recorded per-*entry*
instead of only per-*file*. `add-bumper` already knows the effective `frameQuality` values at the
moment it samples a bumper's reference clip; the fix is simply to also capture and store them:

- `BumperCatalogEntry` gains a `FrameQualitySnapshot? FrameQualitySnapshot` field, reusing the
  existing `Configuration.FrameQualitySnapshot` type verbatim — no new type needed.
- `BumperCatalogBuilder.AddBumper` calls `FrameQualitySnapshot.CaptureCurrent()` once, alongside
  building the rest of the entry, and stores it — purely descriptive, never read back to influence
  matching in any way, exactly as specified.
- This closes a gap the `FrameQualitySnapshot` entry already named and deliberately deferred at the
  time: *"a catalog with ten bumpers added under old settings and one just added under current
  settings reports 'current' for the whole file — the ten old entries' staleness isn't individually
  caught... deferred as real additional scope beyond this minimum bar."* That scope is now in play.
- **Confirmed by the maintainer ("comparing staleness on a per-bumper basis is a great idea"):**
  since `remove`/`match` already resolve one specific `BumperCatalogEntry` per run
  (`RemoveCommand.cs`/`MatchCommand.cs` pick a single entry, per the 2026-08-13 entries), the
  staleness warning switches from comparing the whole catalog's `FrameQualitySnapshot` to comparing
  the *resolved entry's own* — strictly more precise, and directly closes the deferred gap quoted
  above. `BumperCatalog.FrameQualitySnapshot` (the whole-file stamp) stays as-is for now — nothing
  currently reads it once the per-entry comparison lands, but removing it isn't part of this change
  (harmless, and a future need for a whole-catalog-level check isn't ruled out).

No blocking decision remains here — Group A and Group B are both fully specified now.

### Change 4 — `RemovalLength` default, made explicit

**"If the user doesn't specify `RemovalLength` it should default to the `clipLength` like it
currently does. I think this might be the intention, I just want to call it out explicitly."**
Confirmed: this was already the design (`RemovalLength` nullable, `null` → `entry.Duration`, i.e.
today's exact single-length behavior) — restated here in plain terms since it's easy to read past a
`?? Duration` in code and not register it as *the* default, not just *a* fallback. No change from
the first draft; only Change 1 (removing `Trim`) affects `RemovalLength`'s scope, and it doesn't —
`RemovalLength` still applies to any catalog-matched bumper (`Corroborated` and all six new
strategies alike), independent of which strategy decides presence.

### Open questions before/while building

- ~~Exact CLI flag names/shapes~~ — **resolved during build** (item 7 below): `--matching-strategy`,
  `--removal-length`, `--presence-threshold-override`/`--rigid-hit-threshold-override`/
  `--phash-presence-threshold-override`/`--audio-min-similarity-override` on `add-bumper`; the `trim`
  command itself uses `--length`/`--region`/`--paths`, per Change 1.
- **Still open:** does `list-bumpers` need new output for `MatchingStrategy`/the four Group A
  override fields/`RemovalLength`/the per-entry `FrameQualitySnapshot`, or is that a fast-follow once
  the fields exist? Not addressed by this build.

### TODO — suggested build order (all done, 2026-08-13)

1. **Done.** `BumperMatchingStrategy` enum (seven values) + `RemovalLength` + the four Group A
   override fields + the per-entry `FrameQualitySnapshot` field on `BumperCatalogEntry` (additive,
   version-tolerant). Three redundant `double?` frameQuality fields briefly existed alongside
   `FrameQualitySnapshot` from before the Group B correction — removed during build, never wired
   into anything (see this entry's Status note above).
2. **Done.** `MatchingStrategy`: `MatchingSession.ApplyMatchingStrategy` resolves the entry's
   strategy onto three nullable override flags (`strategyUseVisual`/`Audio`/`UsePHash`), consulted
   by `WantsVisual`/`WantsAudio`/`WantsPHash` ahead of the `--detection-mode`-derived fallback —
   `PrepareFromCatalogEntry` calls it right after construction; `PrepareAsync` (ad hoc, no entry)
   never touches it, so ad hoc sessions are unchanged. Live-verified: an `AudioOnly` catalog bumper
   ran audio only under `remove --bumper-label` (`--verbose` showed zero visual/pHash sampling).
3. **Done.** Group A threshold overrides: `PrepareFromCatalogEntry` resolves
   `entry.X ?? <caller's already-resolved value>` (`VbrConfig.Current.Matching.RigidHitThreshold`
   directly, for the one that had no caller-side parameter before) into the session's own instance
   fields, so `Compare`/`CompareUsingDatabase` pick up the override for free — no changes needed
   there.
4. **Done.** `RemovalLength`: `RemoveCommand` now resolves a `removalLength` local
   (`catalogEntry.RemovalLength ?? clipLength` / `clipLength` for ad hoc) separately from `clipLength`
   (which still drives matching/search-window sizing), and passes it to `ClipRemover.Remove`.
5. **Done.** Group B: `BumperCatalogBuilder.AddBumper` calls `FrameQualitySnapshot.CaptureCurrent()`
   onto the new entry; `RemoveCommand.cs`/`MatchCommand.cs`'s staleness-warning call site now reads
   `catalogEntry.FrameQualitySnapshot` (moved to after the entry is resolved, since it wasn't
   available at the old call site).
6. **Done.** `TrimCommand.cs` — new standalone `vbr trim` command, registered in `Program.cs`.
   `--re-encode` moved from `RemoveCommand`-private to `SharedOptions` (identical semantics, now
   shared by `remove` and `trim`, same rationale ADR 0007 already applied to `match`/`remove`'s own
   shared options). Live-verified: both begin- and end-region cuts, stream-copy and CPU re-encode,
   against real media, consistent with `remove`'s own already-established `ClipRemover.Remove`
   behavior on the same files (GPU/NVENC re-encode failed in this specific dev environment — a
   pre-existing local hardware/driver issue unrelated to this change, not a `trim`-specific bug).
7. **Done.** `add-bumper` gained six new options (`--matching-strategy`, `--removal-length`,
   `--presence-threshold-override`, `--rigid-hit-threshold-override`,
   `--phash-presence-threshold-override`, `--audio-min-similarity-override`), all optional/
   null-default, applied onto the entry after `BumperCatalogBuilder.AddBumper` returns (a
   catalog/CLI-level concern, not part of clip sampling) — resolves the "exact CLI flag names" open
   question below with distinctly-named, `-override`-suffixed flags (deliberately not reusing
   `match`/`remove`'s own same-concept-but-different-scope option names).
8. **Done.** `VBR.Tests` still can't reach `MatchingSession`/`RemoveCommand`/`TrimCommand` (the
   pre-existing, already-tracked `VBR.Tests`→`VBR.CLI` project-reference gap) — verified via full
   solution build (0 errors) + `dotnet test VBR.Tests` (98 passed, 5 pre-existing environment-gated
   skips, 0 failures) + live smoke-testing of every new CLI surface, consistent with how this
   project has verified CLI changes all along.
9. **Done.** Docs updated: this entry's status, and `running_and_building.md` gained a `vbr trim`
   section plus an `add-bumper` "Per-bumper overrides" bullet list.

`list-bumpers` output for the new fields (the other open question below) was **not** added — still a
fast-follow, not part of this build.

## Analysis: real-library testing findings, `testing_202608131708.md` (2026-08-13)

**Status: analysis only — no code changes made against this entry yet.** The maintainer ran the
multi-signal gating change (previous entry) against their real library: config tuned to
`presenceThreshold=0.96`, `audioMinSimilarity=0.90`, `phashPresenceThreshold=0.96`,
`darkOverrideDetail=1.0`, `darkRejectPercent=99.0`, and denser sampling throughout
(`--sample-interval 0.1s`/`scanDenseIntervalSeconds=0.1`/`scanSparseIntervalSeconds=2.0`) — full
values in the repo's `vbr.config.json`. **Final, precise tally (superseding two earlier rounds of
rougher estimates in this same entry):**

- **101 bumpers attempted.** 2 failed at `add-bumper` creation outright — reason given: "either
  complete blackness, or there was too much dark, despite some text" (98.0% creation success, 99/101).
- **Of the 99 successfully created:** 2 failed to match *at all*, even tested against multiple
  files; 2 matched their own origin video but failed to match on *other* files, root-caused by the
  maintainer themselves to audio differences; the remaining 95 worked cleanly (95/99 = 96.0% clean
  among created bumpers; 95/101 = 94.1% fully clean end to end).
- One further bumper, outside this tally, was deliberately created purely as a diagnostic aid to
  help investigate the non-matching ones — not a production bumper being scored pass/fail.

The 2 creation failures and the 2-plus-2 post-creation failures are now cleanly, explicitly
categorized by the maintainer — Findings 2-4 below are revised to match this categorization rather
than the earlier, less precise merge. This entry works through `testing_202608131708.md`'s findings
one at a time, root-causing each against the actual code rather than the symptom alone, then
synthesizes a prioritized path forward. Nothing here is decided yet — it's the analysis the
maintainer asked for before deciding what to build next.

### The config change itself is a real, coherent finding, not just tuning

`darkOverrideDetail` dropped from 2.0 to **1.0** — the exact same value as `minDetail`. Combined
with `darkRejectPercent` raised from 80 to **99**, this doesn't just loosen the dark-pixel veto, it
nearly **neutralizes** it: a frame now only gets the stricter "majority-dark" treatment at all if
≥99% of its pixels are dark (vs. 80% before), and even then, the bar it has to clear
(`darkOverrideDetail=1.0`) is now identical to what a normal, non-dark frame needs
(`minDetail=1.0`). The two code paths in `FrameQuality.SelectUsable` now produce the same verdict
for almost every real frame regardless of darkness. This is consistent with, and further validates,
the finding from the bumper-#13 investigation two entries back: darkness-specific filtering wasn't
the real mechanism behind the remaining false positives, so loosening it and leaning on multi-signal
corroboration (previous entry) to control false positives instead is a coherent strategy, not an
accident. Worth stating plainly since it wasn't called out in the maintainer's own notes.

### Finding 1 — multi-signal gating is validated by the maintainer's own data

"Adding the additional dimensions fixe[d] the previous false positives... pushing
`audioMinSimilarity` to 0.90 eliminated the false positive while keeping the matches." This is
real, positive confirmation that the mandatory-pHash/conditionally-mandatory-audio design (previous
entry) does what it was built for. The one remaining case in this section (7.1s, "actually two
bumpers together") is a different, narrower problem — adjacent/back-to-back bumpers where the
search window catches part of a second, different bumper — and the maintainer's own hypothesis
("might be mitigated by `remove` of the first bumper") is plausible: once the first bumper is
physically cut, the leftover file no longer contains the confusing adjacent content. Worth
confirming empirically once there's time, not urgent — this is a candidate-sequencing quirk, not a
detection defect.

### Finding 2 — two distinct dark-content problems, not one: creation failures vs. total match failures

The maintainer's precise recount (above) splits what this entry originally treated as one
continuous story into **two separate categories**, worth analyzing separately:

**Category A — 2 pure creation failures** ("either complete blackness, or too much dark, despite
some text"): `add-bumper` itself never produces a catalog entry — the "every sampled frame was
filtered out" error. This is `FrameQuality.SelectUsable` correctly doing its job on genuinely
degenerate content (true blackness has nothing to fingerprint, full stop) for at least one of the
two, and possibly still-too-strict filtering on the other ("despite some text" suggests real,
if faint, content that still isn't clearing `minDetail`/`darkOverrideDetail` even at today's loosened
values). Not urgent to chase further without knowing which of the two this is — genuine blackness
isn't fixable by loosening thresholds further (there's nothing there to detect, and admitting truly
content-free frames reopens the original 2026-07-18 aliasing bug this whole filter exists to
prevent); a real-but-faint case might warrant a further look at the specific frame data
(`--dump-frames`) if it recurs.

**Category B — 2 bumpers that failed to match *at all*, even against multiple candidate files**
(distinct from Category A: these *did* get created successfully). The thin-red/white-text-on-black
8s bumper is one of these — tested against its own source video, it produced
`visual: present=0/63 bestCos=6% win=125`; the second dark-motion case shows the same shape
(`present=0/42 bestCos=55%`, milder but still a real miss), and per the maintainer's new count,
both failed uniformly across multiple files, not just the origin video specifically.

`bestCos=6%` against the literal file the bumper was extracted from is a strong, unusual signal —
not "borderline," closer to "these two frame sets share almost nothing."

**Staleness ruled out, directly, by the maintainer: "There is no stale library. I deleted all of my
libraries and catalogs and started fresh."** Both sides of every comparison in this round were built
under today's exact `frameQuality` settings — the leading hypothesis from this entry's first draft
is wrong and is struck here rather than left to mislead a future read. What's left, grounded in what
*is* known rather than a fresh guess:

- **Presence matching is alignment-insensitive by design** (`VisualBumperMatcher.ComparePresence`
  is all-pairs — clip frame vs. every candidate frame, no temporal correspondence required), so a
  coarse timing/window offset can't produce a result this low on its own; if the true occurrence is
  anywhere inside the searched window, presence matching finds it regardless of exact alignment.
  `audio=100%@0s` confirms the search window does cover the right position.
- **The window sizes argue against a coverage gap too:** `win=125`/`win=148` (roughly 12.5s/14.8s at
  today's 0.1s interval) are dense-looking counts, not the handful of samples a sparse-only pass
  would produce — so both sides plausibly *did* densely sample the right region. Ruling out
  staleness and coverage gaps narrows this to two live hypotheses:
  1. **The content itself may be intrinsically hard for frame-level embedding matching** — "thin"
     red/white text "moving and flowing" over black means most individual frames carry very little
     stable structure (a thin colored streak on a black field). DINOv2 built its whole reputation on
     shape/object structure; a frame with almost none may not embed into a stable, recognizable
     point at all, so consecutive real occurrences of the *same* animation could legitimately embed
     far apart from each other — not an aliasing problem (two different things looking similar, the
     failure mode this project has chased all along) but its mirror image: one real thing failing to
     look consistently like itself. This would be a first-of-its-kind finding for this project if
     confirmed, worth treating as a real hypothesis, not an assumption.
  2. **A genuine per-file decode/mastering difference** — if the candidate file(s) tested are a
     different release/master than the file the bumper was extracted from (different color grading,
     re-encode, broadcast vs. disc capture), the pixels themselves could differ enough to matter even
     though the ident is conceptually "the same."
- **`--dump-frames` on both the catalog entry and a failing candidate's search window, compared side
  by side, is the direct way to tell these apart** — this project's established method (used
  repeatedly and effectively earlier this session) for distinguishing "wrong/different content" from
  "right content, an embedding that doesn't capture it well." This is now the single most useful next
  step for Category B, not one option among several.

### Finding 3 — "Dark Motion End Bumpers": audio is vetoing matches visual and pHash overwhelmingly agree on

```text
visual: present=61/66  bestCos=100%  win=191  |  audio: audio=70%@486s  |  phash: present=63/66  bestSim=100%  win=191
visual: present=62/66  bestCos=99%   win=171  |  audio: audio=68%@486s  |  phash: present=63/66  bestSim=100%  win=171
```

61-63 of 66 reference frames present, `bestCos`/`bestSim` at 99-100% on both visual and pHash — about
as strong as evidence gets — and the match still fails, because `audioMinSimilarity=0.90` rejects a
genuine 68-70% audio similarity. This is a **real cost of the current design**, not a bug: audio was
made mandatory whenever it's "applicable" (`MatchingSession.ReferenceHasUsableAudio`), but
"applicable" today only means "has ≥2 real Chromaprint blocks" — it says nothing about whether the
*threshold* chosen for standalone audio matching is the right bar for audio acting as a
*corroborator* of an already-overwhelming visual/pHash signal. 68-70% is plausibly genuine,
legitimate variance (different broadcast masters, loudness normalization, slightly different audio
mixes across episodes of the same series) rather than evidence the bumper is actually absent — real
audio similarity has more natural spread than a pristine same-file comparison would suggest. (The
third line in the maintainer's notes, `present=0/66 bestCos=11%`, is a genuine visual miss and a
different situation — worth the maintainer double-checking that file actually contains the bumper
before assuming it should have matched, the same "verify, don't assume" lesson from the earlier
Bumper 2 miscounting incident this session.)

### Finding 4 — "Content Music"/"Content Audio": audio is sometimes structurally not the bumper's own signal at all

This is the maintainer's own clearest diagnosis, and the numbers back it up cleanly:

```text
Match:        visual: present=23/34 bestCos=100%  |  audio: audio=100%@0s   |  phash: present=21/34 bestSim=100%
Didn't match: visual: present=19/34 bestCos=99%    |  audio: audio=54%@15s  |  phash: present=19/34 bestSim=100%
Didn't match: visual: present=18/34 bestCos=99%    |  audio: audio=62%@19s  |  phash: present=19/34 bestSim=100%
```

Visual and pHash are consistently near-perfect across *all three* occurrences; only audio swings
wildly (54-100%). "The reason these don't match is that the music for the main content is used
during the bumper" — some idents are overlaid on continuing film/show score rather than carrying
their own fixed sting, so the "audio" isn't the bumper's signature at all, it's whatever happens to
be playing in that particular scene of that particular piece of content. This is a genuinely
**different** failure mode from Finding 3 (natural variance around a real signal) and from the
silent-bumper case `ReferenceHasUsableAudio` already handles (no signal at all) — this is a *strong,
real, but structurally meaningless* signal. No threshold adjustment fixes this: a bumper in this
class needs audio excluded from gating entirely, the same way a silent bumper already is, but
`ReferenceHasUsableAudio`'s "≥2 blocks" check cannot detect it, because the audio content is real.
The "Content Audio" section (`98%@2587s` matched vs. `60%@692s`/`72%@594s` missed, again with
uniformly-strong 95/105-present visual across all three) is the same mechanism, not a separate one.

### Finding 5 — synthesis: per-bumper matching profiles are now evidenced, not just sketched

Findings 2-4 are three genuinely different failure modes that all point the same direction:

1. A bumper's own recipe can go stale relative to a library's cached fingerprints (Finding 2,
   pending confirmation) — a per-*store* problem, already tracked (2026-08-12 entry).
2. A bumper's genuine audio can have more natural variance than a fixed 0.90 bar tolerates
   (Finding 3) — arguably wants a *different*, more forgiving bar for audio-as-corroborator than for
   audio-as-sole-decider, not a global threshold change (raising the standalone `--min-similarity`
   default would hurt `--detection-mode audio` alone; lowering the *corroboration* bar specifically
   wouldn't).
3. A bumper's audio can be real but structurally meaningless (content-borrowed score) and needs to
   be excluded from gating entirely, the same way silence already is (Finding 4) — but this can't be
   detected from the fingerprint alone the way silence can.

None of these three is solvable with one global config number, because they're properties of
*individual bumpers*, not of the matching algorithm as a whole. This directly confirms the
"Per-bumper matching profiles" design sketch (2026-08-12 entry) was pointed the right direction, and
the maintainer's own framing — "putting the parameters in the bumper," and explicit willingness to
have "the user involved in making some of these decisions" rather than waiting for full automatic
detection — matches that entry's own scoping almost exactly. Recommended shape for a first, useful
slice (deliberately smaller than the full 2026-08-12 sketch, which also covered visual risk-flagging
and temporal clustering that nothing in this round of testing motivates yet):

- A per-bumper, **manually-set** `TrustAudio` (or inverse `AudioUnreliable`) flag on
  `BumperCatalogEntry`, settable via a new `add-bumper` option (e.g. `--audio-unreliable`), honestly
  scoped as manual because reliably *auto-detecting* "is this audio the bumper's own signal or
  borrowed content audio" is real, unsolved scope on its own (a plausible future heuristic: compare
  the bumper region's audio fingerprint against the fingerprint of the content immediately
  surrounding it — suspiciously continuous audio across the bumper boundary would suggest borrowed
  score — but that needs its own validation before trusting it, not a first-pass build). When set,
  `MatchingSession.ReferenceHasUsableAudio`-equivalent logic returns false for that entry regardless
  of fingerprint quality, exactly like the existing silent-bumper exemption.
- Separately, a **lower corroboration-specific audio bar** (e.g. `matching.audioCorroborationMinSimilarity`,
  distinct from `matching.audioMinSimilarity`, defaulting lower) addresses Finding 3 without needing
  per-bumper intervention at all — plausibly enough on its own for that finding, worth trying before
  reaching for a per-bumper override there too.

### Finding 6 — cross-fades need a separate match length and removal length

"There are some bumpers with cross-fades that are almost 2 seconds long. The length for identifying
the clip is 13s. But, to remove everything in the cross-fade, we'd want to remove 15s." Verified
against the code: `BumperCatalogEntry.Duration` today serves three purposes at once — the
fingerprinting region size at `add-bumper` time, the match search-window sizing, *and* the literal
removal arithmetic (`ClipRemover.Remove`'s `bumperLength` — begin-region cut point =
`bumperLength.TotalSeconds`; end-region cut point = `sourceDuration - bumperLength.TotalSeconds`).
The maintainer's own proposed fix is exactly right and cleanly scoped: add an optional
`RemovalLength` field (nullable, defaulting to `Duration` — today's exact behavior when unset), a
new `add-bumper` option (e.g. `--remove-length`) to set it, and thread it through `RemoveCommand`
as the value passed to `ClipRemover.Remove` specifically, leaving the *matching*-side length
(`Duration`) untouched. Small, additive, no risk to existing catalogs (new nullable field, old
entries just keep using `Duration` for both purposes as they do today).

### Finding 7 — 14.7s of trailing black isn't a matching problem at all

11 known files, each with a fixed 14.7s of pure black at the end — genuinely unmatchable by content
fingerprinting (there's nothing to fingerprint; this is exactly the class of content
`FrameQuality`'s filters correctly reject as low-information) and shouldn't be forced into the
bumper-catalog/matching machinery at all. The maintainer's own proposed fix is right: a distinct,
unconditional time-based removal — given a fixed duration + region + a file or list of files, cut
that duration with no fingerprint matching involved. Nearly all the needed machinery already
exists: `ClipRemover.Remove` takes an arbitrary `bumperLength`/`region`/`sourcePath` and needs no
fingerprints at all — what's missing is a CLI surface that skips `MatchingSession` entirely (a new
command, or a `remove --unconditional`-style mode) and just applies the cut to every file in a given
set. Genuinely separate feature from bumper-catalog matching, not an extension of it.

### Recommended prioritization (for the maintainer's call, not decided here)

Roughly cheapest/highest-confidence first:

1. **Finding 2 (Category B)** — staleness is ruled out (confirmed by the maintainer, see above);
   `--dump-frames` on the catalog entry and a failing candidate's window, compared side by side, is
   the direct next step to tell "hard-to-embed content" apart from "different master/mastering"
   before either becomes a code change.
2. **Finding 3** — try a separate, lower `audioCorroborationMinSimilarity` config value. Small,
   config-only-shaped change, directly testable against the exact data already gathered.
3. **Finding 6** — cross-fade `RemovalLength`. Small, additive, clearly scoped, no design ambiguity
   left to resolve.
4. **Finding 7** — unconditional time-based removal. Small, additive, genuinely separate feature,
   no interaction risk with the matching path.
5. **Finding 5 / per-bumper `TrustAudio`** — real but larger scope (new catalog field, new CLI
   option, `MatchingSession` wiring); do after 1-4 land, since some of what motivated it (Finding 3)
   may turn out to be addressed by item 2 alone, narrowing what per-bumper override is actually still
   needed.

## Multi-signal corroboration: pHash mandatory, audio conditionally mandatory — implemented (2026-08-13)

**Status: built, tested, live-smoke-tested.** Prompted by real dogfooding evidence (config file,
`--sample-interval 0.1s`, dense zone widened to 2s, `presenceThreshold` raised to 0.96): the first
12 bumpers added were 100% accurate, zero false positives. Bumper #13 (5.7s, high-motion) produced
2 false positives against unrelated dark-motion and full-color-motion content. Critically,
`minDetail`/`darkOverrideDetail` pushed to 50-55 and `darkRejectPercent` changes had **zero**
effect — ruling out the dark-content-aliasing mechanism from the 2026-08-07/2026-08-12 investigation
for this bumper specifically. Combined with `presenceThreshold` already at 0.96 not stopping it,
this reconfirmed the earlier finding (a false positive can outscore a true positive on the exact
visual signal a threshold would tighten) generalizes beyond dark content — visual alone, tuned as
hard as a single signal can be, isn't always enough. The maintainer asked to start using pHash (and
asked about audio) as real corroborating signals rather than fallback-only, deliberately setting
aside the rigid matcher (confirmed, this session, to not even be computed in the actual
`MatchMixedDensity` code path — a real engineering task, not a flag flip — see the code-verified
finding two exchanges earlier in the session transcript).

### Design

**Old decision rule** (`SignalResult.Present`, unchanged since this project's earliest matching
work): `Visual?.Present ?? Audio?.Present ?? PHash?.Present ?? false` — whichever signal ran decided
alone, in priority order; audio/pHash were corroboration in name only, never actually required.

**New decision rule:** when visual ran, it must still agree — same as before — but every *other*
signal that both ran (per `--detection-mode`) and is actually *meaningful for this specific bumper*
must now agree too:

- **pHash** — unconditionally mandatory whenever it ran (`--detection-mode all`). No carve-out
  needed: pHash and visual share the exact same upfront usable-frame gate (both come from one
  `MixedDensitySampler.GatherFrames` call), so if visual could run at all, pHash's clip-side data is
  equally real — there's no scenario where pHash's answer would be structurally meaningless while
  visual's isn't.
- **Audio** — mandatory whenever it ran (`--detection-mode both|all`) *and* the reference clip's own
  audio is real (`MatchingSession.ReferenceHasUsableAudio`, mirroring the exact `Length: >= 2`
  Chromaprint-block check `AudioBumperMatcher.MatchFingerprints` already used internally to
  short-circuit on "no usable audio fingerprint on the reference clip"). This is the direct answer
  to "can audio handle silent bumpers as well as bumpers with sound" — not a smarter fingerprint
  algorithm (Chromaprint is unchanged), a caller that knows when the algorithm's answer means
  anything. A silent/near-silent bumper's `Audio` result is still computed and still shown (for
  transparency) but never vetoes a match it structurally cannot judge; a bumper with real,
  distinguishing audio gets audio as a genuine, mandatory corroborator, same as pHash.
- When visual *didn't* run at all (`--detection-mode audio|phash`), the old priority-fallback
  behavior is unchanged — whichever single signal ran decides alone. This is a two-signal-minimum
  gate, not a re-architecture of every mode.

**Known, deliberately unaddressed limitation:** `ReferenceHasUsableAudio`'s `>= 2 blocks` predicate
is "has *some* audio content," not "has *distinguishing* audio content" — a generic room-tone/noise
bumper could pass this check yet be just as audio-indistinguishable as true silence, a possible
audio-side echo of the video dark-frame-aliasing problem this project has already hit twice. Not
built against without real evidence, per this project's own established methodology — flagged here
so a future false positive traced to "audio agreed, but only because both sides are generic noise"
isn't a surprise.

**Implementation:** `SignalResult` gained an `AudioApplicable` field (set from
`MatchingSession.ReferenceHasUsableAudio` at both `Compare`/`CompareUsingDatabase` construction
sites) and its `Present` property now runs the rule above instead of the old `??` chain. Added
`SignalResult.CombinedDetail` (every computed signal's detail string, concatenated — same shape
`MatchRow`/`RemoveRow.ToLine()` already built per-row) and used it in `RemoveCommand`'s "Match
found" progress line and the removal manifest's `MatchDetail`, replacing the old first-non-null
chain there too — showing only visual's detail on a match that now depends on multiple signals
agreeing would have been actively misleading. `DetectionMode`'s own doc comment and
`running_and_building.md`'s `vbr match` section updated to state the new rule plainly ("audio as an
opt-in accelerator" was no longer accurate). Build clean, full suite (98 tests) unaffected — no
existing test exercised `both`/`all` mode's gating specifically, so this is a real coverage gap
worth closing in a follow-up, not proof the new rule is correct beyond compiling and preserving
`visual`/`audio`/`phash`-alone behavior.

## Per-bumper matching profiles — design sketch (2026-08-12)

**Superseded/narrowed by "Per-bumper matching strategy — concrete design" above (2026-08-13):** the
`isHighRisk` auto-flagging and temporal-clustering ideas below were never built and aren't part of
the design that moved forward — real testing motivated a different, more direct need (audio-only and
trim-only matching strategies) instead. Kept below for the historical record of what was considered
and why it wasn't what got built.

**Status: design sketch for discussion, not approved or built — several sub-decisions below are
explicitly left open, not resolved by this entry.** Prompted by the maintainer noticing that the
settings needed for static-image bumpers to work at all (namely `DarkOverrideDetail`, see the
2026-08-07 dogfooding entry below) are the same settings producing confirmed false positives on
short (<5s), dark motion bumpers — while longer bumpers don't show the problem. Question raised:
instead of one global matching configuration, can matching parameters be tuned per bumper, detected
automatically at `add-bumper` time?

### Feasibility: yes, the plumbing is small

Code-verified, not assumed: `remove`/`match` already scope exactly one `MatchingSession` to exactly
one catalog entry per run (`RemoveCommand.cs`'s `catalogEntry = catalog.Entries.Values.FirstOrDefault(...)`
picks a single entry; `MatchingSession.PrepareFromCatalogEntry` takes that one entry). `VisualBumperMatcher`
already accepts `presenceThreshold`/`rigidHitThreshold` as per-instance constructor parameters, not
global constants baked in anywhere. So reading a per-entry profile at the point a session is prepared
from a catalog entry, instead of a single global default, is a localized change — not a restructuring
of the matching pipeline.

### The harder finding: re-examining the maintainer's own confirmed data changes what "tune per bumper" can mean

Before designing what a profile *controls*, its numbers were checked against the real, maintainer-
verified TP/FP data from the 150-video test (every entry independently confirmed correctly labeled —
see that exchange). For Bumper 1:

- The three confirmed **true** positives top out at `bestCos=92%` — even the one at full `present=11/11`
  presence.
- Numerous confirmed **false** positives reach `bestCos=96–100%`, several also at full `present=11/11`.

**A false positive can out-score the true positive on the exact signal (`bestCos`) any threshold-style
knob would tighten.** This rules out "a stricter per-bumper `presenceThreshold`" as a fix for this
failure mode — raising the bar would cost the true positive before it excludes the worse false
positives, since cosine similarity itself is not a reliable ranking signal for this content class
(consistent with the earlier hypothesis: generic dark/grainy texture can alias other unrelated
dark/grainy texture at least as strongly as a genuine, slightly-re-encoded copy of itself resembles
the reference).

This also means the real data actually contains **two different false-positive mechanisms**, which
need different treatment, not one shared knob:

1. **High-present, high-`bestCos` "generic content aliasing"** — the dominant mode by volume in the
   real data (most of Bumper 1's false positives are `present=9-11/11` at `92-100%`). A stricter
   threshold or a higher required-hit count does nothing here — these already look like maximally
   strong matches by both signals. Needs a signal *orthogonal* to cosine similarity.
2. **Low-present, borderline-`bestCos` "coincidental match"** — the `present=1-5` out of `11`/`16`
   entries at `90-92%`, appearing with the same shape in both the TP and FP lists. A required-hit
   floor, scaled to how many usable frames the bumper actually has (not a fixed number — a fixed
   floor was already rejected once this session for breaking static-bumper recall), can plausibly
   help here without threatening legitimate low-present matches like the confirmed `present=1/11` TP.

### Proposed shape: `BumperMatchingProfile`, computed once at `add-bumper` time, stored on the catalog entry

- A small record on `BumperCatalogEntry` (MemoryPack `VersionTolerant`, same convention as the rest
  of the format), computed by `BumperCatalogBuilder.AddBumper` from data it already has in scope by
  the time the entry is built: `Duration`, usable-vs-sampled frame counts, per-frame dark% (already
  computed inside `FrameQuality.SelectUsable`'s dark-veto check today — currently discarded after the
  usable/not-usable decision, just needs to be retained), and pairwise cosine variety among the
  clip's own frame embeddings (cheap — the embeddings are already computed for the entry's
  `Fingerprints`, and clip frame counts are small enough that an all-pairs comparison costs nothing
  measurable).
- **Two separate dials, not one, matching the two mechanisms above:**
  1. `RequiredHitFraction`-style floor (mechanism 2): `requiredHits = max(1, ceil(usableFrameCount *
     minPresenceFraction))`, `minPresenceFraction` itself a tunable (natural fit for the `matching`
     config section already planned above) rather than hardcoded here.
  2. A **mandatory corroboration requirement**, engaged only for bumpers flagged high-risk (mechanism
     1), using a signal other than a stricter `bestCos` bar. Candidates, roughly cheapest-first:
     - **Candidate-side temporal clustering** (raised earlier this session, not yet built or tested):
       require that the winning match isn't one isolated candidate frame — nearby candidate frames
       (within the sampling interval) should also score reasonably well, consistent with a bumper
       that persists on screen for multiple seconds rather than a one-frame coincidence. Cheapest to
       build (candidate frames are already timestamped); needs real validation against Bumper 1/2's
       actual false-positive files before being trusted, same evidence-first requirement this whole
       investigation has used.
     - **Mandatory audio corroboration for high-risk bumpers that have usable audio.** Code-confirmed
       this turn: today's `SignalResult.Present` (`MatchingSession.cs`) is a *priority fallback*, not
       an AND-gate — `Visual?.Present ?? Audio?.Present` only reaches `Audio` when `Visual` itself is
       `null` (visual didn't run at all); when visual *did* run, its `Present` decides alone, true or
       false, even in `--detection-mode both`. `MatchResult` is a `readonly record struct`, confirming
       this is really how the nullable short-circuit behaves, not a misreading. Making audio an AND
       requirement for flagged-high-risk bumpers specifically would be a real, if narrow, change to
       that decision rule — and only helps bumpers with distinguishing audio; silent idents gain
       nothing from it.
     - **A held-out background-content check at `add-bumper` time** — bigger lift, explicitly a
       stretch/future option, not part of this phase: embed a small fixed sample of generic,
       non-bumper content once, and check whether a *new* bumper's own clip frames already score
       suspiciously high against that generic background before it's ever saved. Would catch a
       Bumper-1/2-shaped problem at creation time rather than after a library-wide `remove` run finds
       it — but needs a maintained "known generic content" reference set, which is ongoing scope, not
       a one-time build.
  - **High-risk flag**, deliberately mirroring the maintainer's own empirical description rather than
    inventing new boundaries: `isHighRisk = duration < 5s && majorityDarkFraction >= DarkRejectPercent`.
    This is a starting point to validate against real bumpers, not a final formula — same "boundary
    cases can misfire" caveat already raised when this idea was first floated.

### Sequencing

Independent of the `--catalog-db`/config-file entry above, but shares infrastructure —
`minPresenceFraction` and the high-risk boundary constants are exactly the kind of values that
belong in the `matching` config section once it exists, rather than newly hardcoded. Recommend
building this *after* the config file lands. One addition owed back to that entry once this ships:
a per-bumper profile computed from `frameQuality` settings active at `add-bumper` time is itself
now part of what "staleness" means for a catalog entry — if `frameQuality` config changes, a
previously-computed profile (not just the fingerprints) is potentially stale too; fold into Phase
B's recipe-stamp work when it's actually implemented, don't let it get missed.

### Explicitly out of scope / not decided by this sketch

- The exact `isHighRisk` formula and `minPresenceFraction` value — needs calibration against real
  data (Bumper 1/2, already gathered this session, are the natural first test set), not guessed.
- Which mandatory-corroboration mechanism is right — temporal clustering, audio, both, or something
  else — needs its own prototype and validation before committing to one.
- Retrofitting Bumper 1/2 (and any other already-added catalog entries) with a computed profile —
  likely needs an `add-bumper` re-run or a new maintenance command; not designed here.
- **Evaluate all 3 signals (visual + audio + pHash) together, not just visual-with-optional-
  corroboration (raised by the maintainer, 2026-08-12) — captured as a pointer only, deliberately
  NOT designed or built against.** Broader than the "mandatory audio for flagged bumpers" candidate
  above: a real weighted evidence-combination architecture, motivated by visual's dark/grainy-content
  aliasing failure mode having no particular reason to also fool audio or pHash (different feature
  spaces). Two known constraints any future design has to satisfy, already established this session:
  audio contributes nothing for the many bumpers that are silent (`VisualBumperMatcher`'s own doc
  comment — "the only path validated on ... short, often silent studio/network idents, which audio
  cannot touch"), and pHash has already tested as weak standalone on these exact bumpers
  (`SharedOptions.PHashPresenceThreshold`'s description) — so it cannot be a flat 3-way majority
  vote; whatever combines them must degrade gracefully when a signal is absent or historically weak.

### TODO

**Phase 1 — data collection + plumbing:**

1. Extend `BumperCatalogBuilder.AddBumper` to retain per-frame dark%/detail already computed during
   quality filtering (currently discarded), plus pairwise clip-embedding variety, and compute+store a
   `BumperMatchingProfile` on the resulting `BumperCatalogEntry`.
2. Wire `MatchingSession.PrepareFromCatalogEntry` to read the entry's own profile for its
   required-hit floor, replacing the single global default for that call path only.
3. Tests: profile computation from synthetic and real-clip-derived inputs; confirm the required-hit
   floor scales sanely from a 1-usable-frame static bumper up through Bumper 1/2's actual usable
   counts.

**Phase 2 — high-risk corroboration (needs its own validation before committing to a mechanism):**

1. Prototype candidate-side temporal clustering as a standalone, testable unit, independent of the
   profile plumbing above; validate directly against Bumper 1/2's real confirmed TP/FP files.
2. If that under-delivers, prototype mandatory audio corroboration for high-risk bumpers as the
   fallback candidate — requires changing `SignalResult.Present`'s decision rule for the high-risk
   case only; normal-risk bumpers keep today's priority-fallback rule unchanged.
3. Calibrate `isHighRisk`'s boundary against Bumper 1/2 plus any additional real bumpers the
   maintainer can supply — a data-fitting step, not a from-first-principles guess.
4. Regression test: Bumper-1/2-shaped inputs (dark, short, high-present false match) are rejected
   post-fix while the confirmed low-present true positive (`present=1/11`) still passes.

## File-path DB options (`--catalog-db`/`--library-db`) + runtime config file — design & TODO (2026-08-11)

**Status: built and tested, 2026-08-12 — all three parts.** Three changes, one theme: fewer moving parts between a command line and its on-disk state. The two name+folder flag pairs (`--catalog-name`+`--catalog-db-folder`, `--library-name`+`--library-db-folder`) each collapsed into one explicit file-path flag (`--catalog-db`/`--library-db`), and the tunables previously compiled into the binaries moved into one inspectable, validated `vbr.config.json` with today's values as the defaults. Default behavior is unchanged: a run with no new flags and no config file resolves the same files and computes the same results as before — only the flag spellings changed. Deviations from the original design worth flagging: (1) the recipe-stamp mechanism (Part 3) ended up living on `BumperCatalog`/`LibraryDatabase` directly as a nullable `FrameQualitySnapshot` field, checked and warned on inside `remove` right after each load, rather than a separate `VbrConfig`-side comparison API — simpler given `FrameQuality` was the only section that needed it; (2) per-codec removal quality values (`H264Quality`/`HevcQuality`) turned out to be shared per codec *family* across CPU and every GPU vendor's own flag, exactly mirroring how the original hardcoded `"22"`/`"24"` strings were already structured, not one key per vendor; (3) the "Per-bumper matching profiles" entry above and the maintainer's later "evaluate all 3 signals together" idea are both explicitly NOT part of this build — see that entry's own TODO and the bare pointer below it. Full test coverage: `VBR.Tests/Catalog/BumperCatalogStoreTests.cs`, `VBR.Tests/Database/LibraryDatabaseStoreTests.cs`, `VBR.Tests/Configuration/VbrConfigLoaderTests.cs` (loader discovery/validation/precedence, plus a `FrameQuality.SelectUsable`-flips-on-a-lowered-`DarkOverrideDetail` flows-through test) — 98 tests passing, 0 failing.

### Part 1 — `--catalog-db <file>` replaces `--catalog-name` + `--catalog-db-folder`

Current state (code-verified): `add-bumper` (name **required**), `remove` and `list-bumpers` (name defaults to `'default'`) all resolve `{--catalog-db-folder || default state folder}/{sanitized name}.vbrcat` via `BumperCatalogStore.ResolveCatalogPath`. Design points:

- **One option, `--catalog-db`:** absolute or relative path to the catalog *file* itself, e.g. `--catalog-db "d:\data\my-catalog-db.db"`. Relative paths resolve against the invoking shell's current directory — plain `Path.GetFullPath`, no new resolution machinery.
- **Any extension, or none.** Safe because every load is already magic-header-checked (`VBRCAT01`): the extension never gated anything at load time — `.vbrcat` was only a naming convention `ResolveCatalogPath` imposed. Pointing the flag at a non-catalog file already errors loudly ("not a recognized bumper catalog file"), including the crossed-flags mistake (a `.vbrdb` handed to `--catalog-db`), since the two stores' magics differ.
- **Omitted ⇒ `{default catalog state folder}/default.vbrcat`.** This drops add-bumper's one Required catalog flag entirely and unifies all three commands on remove/list-bumpers' existing `'default'` convention — one less mandatory flag, per the automatic-over-manual principle.
- **`BumperCatalog.CatalogName` stays in the format** (MemoryPack `VersionTolerant` — keeping the field is free) but becomes display-only, set from the file name stem. Label uniqueness stays scoped to the catalog file, unchanged in practice.
- **Sidecar folders anchor to the file's own directory:** add-bumper's `clips/` and list-bumpers' `.vbrthumbs` land next to whatever file `--catalog-db` names — same relationship as today, just anchored to the user's chosen location.
- **Guards flip:** all three copies of "`--catalog-db-folder` must be a folder, but a file already exists there" die; the replacement pre-check is the inverse — the given path must not be an existing *directory*. Parent directory still auto-created on first save (unchanged).
- **Hard removal, no aliases or deprecation period:** no installed base, sole user — the old flags become unknown-option parse errors, with docs updated in the same change.

### Part 2 — `--library-db <file>` replaces `--library-name` + `--library-db-folder`

Current state (code-verified): `scan` (name defaults from the first `--library` folder via `DeriveLibraryName`), `remove`, and `commit` resolve `{--library-db-folder || default state folder}/{sanitized name}.vbrdb` via `LibraryDatabaseStore.ResolveDatabasePath`. Design points:

- Same option semantics, extension freedom, directory guard, and hard-removal stance as Part 1, applied to the library store.
- **`scan` with the flag omitted keeps today's fully-automatic default:** `{default database state folder}/{DeriveLibraryName(first --library folder)}.vbrdb` — a bare `vbr scan --library D:\Show` keeps finding and writing exactly the file it does today, so existing state in the default folder stays reachable with no flags at all.
- **Mode signaling on `remove`/`commit`:** presence of `--library-db` replaces presence of `--library-name` as the "use the scanned database" signal. The "`--library-db-folder` must be accompanied by `--library-name`" guards on both commands disappear — they only existed because a folder alone couldn't name a file.
- `LibraryDatabase.LibraryName` = the file name stem when the flag is given, the `DeriveLibraryName` result otherwise; display-only, field stays in the format (same rationale as Part 1).
- Explicit paths are used verbatim — `SanitizeFileName` retires from the explicit path flow and survives only inside scan's derived default (folder name → file name), where it is still doing its original job.

### Part 3 — runtime config file for the compiled-in tunables

- **Format & discovery:** JSON, parsed tolerant of comments and trailing commas (System.Text.Json reader options — zero new dependencies), so the file can document itself. Automatic discovery, no new required flag: `vbr.config.json` in the current directory (project-local override) wins over `{VBR state root}/vbr.config.json` (the same per-OS folder both stores already resolve); neither present ⇒ pure built-in defaults, zero friction.
- **Precedence: built-in default < config file < explicit CLI flag.** The config replaces the *default* a flag falls back to; it never overrides a value the user actually typed. `--help`'s printed defaults read the loaded config (via `DefaultValueFactory`), so help output never lies about what a run will do.
- **Validation at load, fail-fast:** typed schema; per-key range checks (thresholds in (0,1], intervals > 0, caps ≥ 1, ...); **unknown keys are hard errors** — a misspelled key that silently does nothing is the classic config-file trap. Every failure names the key, the offending value, and the accepted range.
- **Movable inventory (code-verified 2026-08-11), grouped by proposed config section:**
  - `frameQuality`: `MinDetail` 1.0, `DarkOverrideDetail` 2.0, `DarkRejectPercent` 80 (FrameQuality.cs); the dark-pixel byte ceiling 0x20 is movable too but mirrors VDF's own `GrayBytesUtils.BlackPixelLimit` — if exposed, document that changing it desyncs VBR's dark% definition from VDF's.
  - `matching`: presence threshold 0.90, rigid-hit threshold 0.89, pHash presence threshold 0.96 (VisualBumperMatcher.cs); audio min-similarity 0.80 (AudioBumperMatcher.cs).
  - `sampling`: match/remove sample interval 1.0s (VisualBumperMatcher.cs), add-bumper sample interval 0.2s (BumperCatalogBuilder.cs), scan profile — edge boundary 20s / dense 0.2s / sparse 4s (ScanCommand.cs), match/remove search-length slack +20s (SharedOptions/RemoveCommand), frame caps 400 per file/zone (VisualBumperMatcher, MixedDensitySampler, WholeFileSampler) and sparse cap margin 20 (WholeFileSampler.cs).
  - `removal`: end-cut overshoot safety margin 1.0s, keyframe search window 30s, re-encode preset "medium" / audio codec "aac" / audio bitrate "192k", per-codec quality targets (x264 crf 22, x265 crf 24, vp9 crf 31, NVENC cq 22 preset p5, QSV global_quality 22 preset slow) (ClipRemover.cs); stream-copy duration tolerance 2.0s (ClipExtractor.cs); `--validate-files` duration tolerance 2.0s (LibraryCleaner.cs).
  - `scan`: checkpoint interval 30s (LibraryScanner.cs).
  - `storage`: save-retry attempts 4 / retry delay 150ms (LibraryDatabaseStore.cs, BumperCatalogStore.cs — one shared setting pair, not two).
  - `limits`: label max 30 / description max 255 (AddBumperCommand.cs); DirectML device-probe cap 4 (HardwareAcceleration.cs).
- **Excluded, with reasons — not everything numeric is a tunable:** format magics and `CurrentFormatVersion`s (file identity, not behavior); `OnnxEmbedder.InputSide` 224 (the DINOv2 model's own input size) and `EmbeddingMath.QuantScale` 127 (stored-embedding comparability) — both also upstream VDF.Core code shared with VDF's own dedup scan; `FrameHashing.HashSide`/`Block` (the pHash definition — stored hashes stop being comparable if changed); CLI parse conventions (invariant decimal, duration suffixes). Changing any of these doesn't tune behavior, it silently breaks correctness or comparability with already-persisted data.
- **The recipe-staleness landmine, called out loudly — but scoped correctly this time (corrected 2026-08-11, see below):** only `frameQuality` is the fingerprint recipe in the sense that matters — it's a binary usable/not-usable *gate* on which frames even get embedded, so asymmetry between two stores means one side is being compared against content the other side never saw at all. Editing it in a config file invalidates every existing `.vbrdb`/`.vbrcat` exactly the way the 2026-08-07 `DarkOverrideDetail` code change did, except now it's a *supported user action* rather than a rare code event. The filed TODO ("Database/catalog fingerprint-recipe staleness has no detection", PROGRESS.md) graduates from standing risk to actively provoked. **Bundled mitigation, in scope for this work, not deferred:** stamp the effective `frameQuality` values into each database/catalog at save time; on load, compare against the running config and warn on mismatch ("this file was built with different frame-quality settings — results may be wrong; re-scan with `--rescan` / re-add bumpers"), with "refuse unless explicitly overridden" as the candidate stricter follow-up once the warning has proven itself.
- **`sampling` is deliberately EXCLUDED from the stamp/warning mechanism (corrected 2026-08-11, prompted by the maintainer questioning whether catalog/library sample intervals have to match — they don't, and an earlier draft of this entry wrongly implied they did by lumping `sampling` in with `frameQuality` as equally "recipe-breaking," citing the 2026-08-07 incident as justification even though that incident was caused by `frameQuality` alone, never by an interval mismatch):** presence matching (`VisualBumperMatcher.ComparePresence`) is deliberately alignment- and interval-agnostic — all-pairs best-cosine, no temporal correspondence required — and today's *normal* state is already mismatched (catalog at 0.2s vs scan's 0.2s-dense/4s-sparse profile) and works correctly. A coarser interval can only cost recall (fewer sampled frames near a bumper's occurrence, so a real match might land less well or, in the extreme, get missed) — it can never turn an already-found match into a wrong one, unlike a `frameQuality` gate excluding real content outright. Search-length slack isn't even stored *in* a file to begin with — `CompareUsingDatabase` re-applies it fresh against already-broadly-sampled data on every run — so there is nothing to stamp for it in the first place. Net effect: the stamp mechanism has exactly one input (`frameQuality`), checked per-file against the currently active config; no per-role comparison logic is needed (frameQuality isn't sampled per-role the way intervals are — add-bumper and scan both filter through the same shared `FrameQuality.SelectUsable` and the same global config values). `sampling` stays a config section (still worth exposing/tuning/validating), just not a staleness signal. Optional, non-blocking nice-to-have: still record the sampling values used at build time in each file purely for `--verbose`/diagnostic display ("this catalog was built with add-bumper interval=0.2s") — never compared, never warned on.
- **Implementation shape:** a `VbrConfig` root record with nested per-section records in VBR.Core; loaded and validated once at Program startup; exposed statically (the pattern `HardwareAcceleration.Mode` already establishes); today's `const`s become the record property defaults and call sites read the config. Tests construct config instances directly.
- **Tests:** loader coverage (missing file, comments tolerated, unknown key rejected, out-of-range rejected, valid override applied, cwd-beats-state-folder precedence), plus at least one flows-through test per section proving the value actually changes behavior (e.g. `darkOverrideDetail` flips a `SelectUsable` verdict; `presenceThreshold` flips a match verdict).

### TODO — implementation order

**Phase A — path flags (small, mechanical, first):**

1. `BumperCatalogStore`: replace `ResolveCatalogPath(folder, name)` with an explicit-path entry point plus a `DefaultCatalogPath()` helper (`default.vbrcat` in the default state folder); magic-header check stays as the wrong-file guard.
2. `LibraryDatabaseStore`: same treatment, keeping `DeriveLibraryName` for scan's no-flag default.
3. `SharedOptions`: retire `LibraryName`/`LibraryDbFolder`; add shared `--library-db` and `--catalog-db` option definitions (both are multi-command).
4. Rewire the six consumers — `add-bumper`, `list-bumpers`, `remove` (catalog side); `scan`, `remove`, `commit` (library side): delete the folder-flag guards and the "folder must be accompanied by name" validations, add the not-an-existing-directory check, derive display names from file stems. `remove` keeps its "`--catalog-db` requires `--bumper-label`" pairing rule (a catalog file alone doesn't select a bumper).
5. Update tests that drive commands by flag, `docs/running_and_building.md`'s example command lines, and add amendment notes to ADRs 0004/0009/0010/0011 pointing at this entry.
6. Manual smoke pass: add-bumper → list-bumpers → scan → remove → commit round-trip with (a) no DB flags at all, (b) a relative path, (c) an absolute path with a non-standard extension.

**Phase B — config file:**

1. `VbrConfig` schema + loader + validation (unknown keys, ranges) + loader unit tests.
2. Discovery/precedence wiring at Program startup; `DefaultValueFactory`s read the config so `--help` shows effective defaults.
3. Migrate one section per commit — `frameQuality` → `matching` → `sampling` → `removal` → `scan`/`storage`/`limits` — each with its flows-through test, so any regression bisects to one section.
4. Recipe stamp: persist the effective `frameQuality` values (only — not `sampling`) into `.vbrdb`/`.vbrcat` at save; warn on load mismatch — this closes the PROGRESS.md staleness TODO's minimum bar and must not slip out of this phase.
5. Optional QoL, maintainer's call: `vbr config init` writes a fully-commented template listing every key, its default, and its valid range — discoverability without any new required flag.
6. Docs: config reference section (key, meaning, default, range, which keys are recipe-relevant — `frameQuality` only, `sampling` explicitly called out as not); update the PROGRESS.md staleness TODO entry to reflect the recipe-stamp item above.

## Dogfooding fallout: `remove` HW acceleration visibility, and static-image bumpers — sequencing plan (2026-08-05)

**Status: plan for discussion, not yet approved or built.** Raised by the maintainer while
dogfooding the CLI in a separate environment from the usual dev/test machines: (1) `vbr remove`
doesn't *seem* to be using ffmpeg hardware acceleration, and (2) static image bumpers held for
many seconds are a recurring problem case. Both were investigated against the current code before
writing anything below — issue 2 turns out to already have a real, code-confirmed repro on record
(`PROGRESS.md`, 2026-08-05 entry, same date as this dogfooding session), and issue 1 turns out to
be less "missing" than "unverified" once the actual wiring is traced. No implementation yet — this
entry lays out findings and candidate directions for the maintainer to pick from.

### Issue 1 — `remove` and ffmpeg hardware acceleration

**What's actually already wired, confirmed by reading the code, not assumed:** `--hardware-accel`
(default `auto`, ADR 0013) drives `VBR.Core.Extraction.HardwareAcceleration.Mode`, and every
ffmpeg-invoking call site in the `remove` path already adds `-hwaccel <mode>` before `-i`:
[`ClipRemover.StartFfmpegArgs`](../VBR.Core/Removal/ClipRemover.cs) (stream-copy, a no-op there
but harmless), both of `ClipRemover`'s re-encode builders
(`RunFfmpegOutputSeekReEncode`/`RunFfmpegDurationReEncode`), `ClipExtractor`'s extraction of the
reference clip, and `DenseFrameSampler`'s CLI fallback (used for matching's own sampling within
`remove`, when the native FFmpeg.AutoGen binding — see the entry below — doesn't apply). Re-encode
also probes for a real GPU encoder (`GpuEncoderProbe`, H.264/HEVC only) rather than trusting a
static `ffmpeg -encoders` list, and ADR 0013's "Live-verified (2026-07-30, real RTX 3080 machine)"
section confirms this actually engages on real hardware: `ClipRemover` logged `Re-encode video
codec: h264_nvenc (GPU)` and the real command line showed `-c:v h264_nvenc -cq 22 -preset p5`. So
the infrastructure is not missing — the dogfooding machine is a different environment than that
RTX 3080 verification machine, and something in it isn't producing the expected result (or isn't
visibly confirming one).

**The real gap, found by comparing how differently each GPU layer verifies itself:** `GpuEncoderProbe`
learned (ADR 0013, live-verified) that a compiled-in encoder name proves nothing — it probed by
attempting a real synthetic encode instead. `HardwareAcceleration`'s DirectML path learned the
identical lesson twice as hard (ADR 0013's whole "Live-verified" history, culminating in
2026-08-02: `RunDirectMlProbe` originally treated "construction didn't throw" as success, when
`OnnxEmbedder` silently falls back to CPU with no exception at all — the probe had been lying on
every machine tested until `UsedDirectML` was added as the real signal). **Ffmpeg decode
(`-hwaccel auto`) never got the same treatment.** It's added to the command line unconditionally
whenever `HardwareAcceleration.Enabled`, with zero verification that it actually selected a working
hardware path rather than silently decoding in software — a known real-world ffmpeg behavior for
`-hwaccel auto` specifically (it can fail to attach and fall back with no error, no distinct exit
code, nothing to catch). Nothing in this codebase currently distinguishes "GPU decode engaged" from
"GPU decode silently declined" for the one layer that's never had a probe built for it — every
other layer in ADR 0013's history turned out to need exactly this distinction to trust its own
"it's working" belief.

**A real environmental confound to rule out before trusting any wall-clock comparison here, noted
by the maintainer (2026-08-05):** when this machine's NAS runs ZFS pool maintenance (scrub/resilver
etc.), `scan` throughput drops by half or more — nothing to do with ffmpeg, hwaccel, or this
project's own code, purely storage I/O contention on the pool the library lives on. Any "is HW
accel actually helping" measurement (here or under Option B below) needs to either confirm the pool
is idle first or explicitly control for this, or a slow run during unrelated NAS maintenance could
easily be misread as a hardware-acceleration regression.

**Candidate directions:**

- **A. Make decode acceleration status visible, unconditionally (not just under `--verbose`).**
  Cheapest option and the most likely to actually answer "is it working on this machine" without
  guessing further: print one line (stderr, matching this project's existing "Note:"/progress
  convention) reporting what `-hwaccel` mode was requested and, ideally, what ffmpeg itself reports
  choosing (`ffmpeg -loglevel verbose` surfaces the selected hwaccel method on stderr today — this
  project already parses ffmpeg's own text output elsewhere, e.g. `FfmpegErrorClassifier`/the
  `-progress` parser in `ClipRemover.ReadProgressAsync`). Doesn't fix anything by itself, but turns
  "does not seem to be" into a concrete yes/no the maintainer (or any future dogfooder) can read off
  a real run instead of inferring from wall-clock time or Task Manager.
- **B. A real decode probe, mirroring `GpuEncoderProbe`'s pattern.** Attempt a trivial synthetic
  decode (or decode a few frames of the real source) under the requested `-hwaccel` mode and confirm
  it actually ran on the device before trusting it for the real job — the same "prove it, don't
  infer it from compiled-in support" principle `GpuEncoderProbe` and the (eventually-fixed) DirectML
  probe both already apply. Real cost: another child-process invocation per run (small, one-time,
  same class of cost `GpuEncoderProbe` already accepts). Would also let `auto` be resolved to a
  concrete method up front and logged, rather than staying a black box ffmpeg decides internally —
  similar in spirit to ADR 0015's Step 7 candidate-list probing (`d3d11va`/`dxva2`/`vaapi`/
  `videotoolbox`), which today only applies to the *native* FFmpeg.AutoGen binding path, not this
  CLI `-hwaccel` path `ClipExtractor`/`ClipRemover` are permanently scoped to (ADR 0015's own
  explicit "decode-only, not encode/mux" boundary keeps native binding out of `remove`'s actual cut
  entirely — a decode probe here would be new, CLI-path-specific work, not a reuse of that ADR's
  candidate list).
- **C. Ffmpeg build/acquisition.** Already tracked separately (`PROGRESS.md`, "Ffmpeg/ffprobe
  acquisition" item, flagged 2026-08-04) — relevant here only if diagnosis on the dogfooding machine
  finds a build that lacks the necessary decoder/encoder support at all (a static or minimal build,
  the same class of gap already documented for native binding's shared-library requirement). Not
  redone in this entry; just noted as the likely fix if that turns out to be the cause.

**Recommendation:** start with A — it's near-zero effort, ships independently of any decision about
B, and directly produces the evidence needed to know whether B is actually warranted here or whether
this machine simply has no working hardware path (in which case correct CPU fallback is already
happening and there's nothing to fix). Don't build B speculatively before a real run's diagnostic
output says it's needed — same "don't build speculative surface" preference this document already
applies elsewhere (e.g. the "Utilizing Databases" entry's `--file`-plus-`--library-name` gap).

**Option A: implemented (2026-08-05).** `HardwareAcceleration.ReportDecodeRequest()` (new method,
[`VBR.Core/Extraction/HardwareAcceleration.cs`](../VBR.Core/Extraction/HardwareAcceleration.cs))
prints one unconditional stderr line per command invocation — `scan`/`match`/`remove`/
`add-bumper` all call it once, right after `HardwareAcceleration.Mode`/`NativeFfmpegBinding` are
set from the parsed CLI options — reporting the requested `-hwaccel` mode and whether native
binding is on. Deliberately reports only what's certain (the flag about to be passed), not whether
ffmpeg actually engaged hardware decode — that confirmation gap is still open (Option B, not built
here). Separately, `ClipRemover.Remove`'s re-encode path now prints its resolved encoder choice
unconditionally too (`Re-encode codec: h264_nvenc (GPU)` / `libx264 (CPU)`, promoted from a
`--verbose`-only `Logger` line to a plain `Console.Error` line) — this one **is** a real,
code-certain confirmation, not a hopeful request, since `GpuEncoderProbe` already probed a real
synthetic encode before the choice was made. No change to `-hwaccel` wiring itself, error handling,
or log verbosity — this is visibility only, chosen specifically to avoid the regression risk a
verbose-logging bump would add to existing failure-message extraction (see the option's own
write-up above). Not yet run against the dogfooding machine that raised this issue — next step is
reading its actual output there, not a code change.

**Open question for the maintainer:** is a real decode probe (B) worth its per-run cost and added
complexity, or is visibility (A) enough for now, deferring B until a concrete run shows `-hwaccel`
silently declining on real hardware that should support it?

### Issue 2 — static image bumpers held for many seconds

**Not a new finding — already has a real repro on record.** `PROGRESS.md`'s "Static image bumpers
produce zero usable frames" item (flagged 2026-08-05, the same day as this dogfooding session)
already documents a live failure: `vbr scan` against a real end-region static bumper produced *"No
usable frames found in '\<file\>'s end region (5s) -- every sampled frame was filtered out as
low-information (black/blank/duplicate)."* That entry explicitly calls for "a real design pass now
that a repro exists" — this section is that pass, informed by tracing exactly how the filtering
pipeline behaves, including checking both of the maintainer's own proposed directions against the
actual code before recommending anything.

**How the pipeline actually behaves, traced through the code:**

- `FrameQuality.SelectUsable` ([`VBR.Core/Fingerprinting/FrameQuality.cs`](../VBR.Core/Fingerprinting/FrameQuality.cs))
  is three filters stacked: VDF's own ≥80%-dark-pixel rejection, VDF's own byte-identical-to-
  previous-frame drop (`ScanEngine.SelectUsableDenseFrames`), and this project's own calibrated
  near-uniform rejection (`MinDetail = 1.0`, a mean horizontal luma-delta edge-energy measure).
  Applied identically to **both** the reference clip and every candidate's search window.
- This filter is not incidental — it's the direct, deliberately strict fix for a real, previously
  shipped false-positive bug (the 2026-07-18 "Finding 3" investigation, `docs/design/matcher-spec.md`
  §"2026-07-18 correction" and `docs/research/vdf-evaluation.md`): before the fix, near-black and
  near-uniform frames embedded at cosine 0.87–0.97 against *any other frame of the same character*,
  and duplicate/near-duplicate frames let one coincidental hit masquerade as several independent
  pieces of evidence (`present=6/14` was six copies of one black frame, not six distinct
  corroborating detections). Both the dark/duplicate guard and the near-uniform threshold were
  calibrated against real frame grids before shipping, and the fix was re-validated against a full
  TP/FP matrix (12/12 true positives, 0/33 false positives). Any change here needs to not reopen
  that bug.
- **A static bumper can legitimately fail this filter for reasons that have nothing to do with the
  2026-07-18 bug.** If the whole bumper is one still image with no fade/motion, dense sampling
  collapses to ~1 distinct frame (the duplicate guard drops the rest as byte-identical repeats of
  the first); if *that* one surviving frame is also low-detail (a flat background, minimal
  text/logo — exactly the "blank-white ident background 0.55–0.68" case `MinDetail`'s own
  calibration notes recorded), it fails the near-uniform bar too, and the clip is left with zero
  usable frames.
- **This is a hard, whole-run-aborting failure, not a soft per-candidate miss** — confirmed by
  reading `MatchingSession.PrepareAsync` ([`VBR.CLI/Commands/MatchingSession.cs:111-141`](../VBR.CLI/Commands/MatchingSession.cs)):
  the reference clip is sampled and filtered exactly once, before the per-candidate loop even
  starts, and `usableCount < 1` returns an error string that `RemoveCommand`/`MatchCommand` print
  and exit nonzero on. A static-image bumper that trips this doesn't just fail to match some
  files — `vbr remove`/`vbr match`/`vbr add-bumper` refuse to even start using it as a reference
  clip at all.

**Checking the maintainer's own two proposed directions against this trace, before proposing
anything else:**

1. **"Don't discard duplicates, treat as unique — can we get accurate vectors if we do that?"**
   Traced through `VisualBumperMatcher.Embed`/`MixedDensitySampler.AppendZone`: DINOv2 embeds
   whatever bytes it's handed, so byte-identical frames necessarily produce byte-identical
   (cosine=1.0) vectors — there's no "more accurate" vector to gain from embedding the same pixels
   five times instead of once; it's the same vector five times. Not deduplicating would restore
   exactly the evidence-inflation shape the 2026-07-18 fix eliminated (`present=N/M` counting
   repeats of one frame as N independent detections, and the rigid corroborator being trivially
   satisfied by repeats too) — real regression risk for no accuracy upside. **Recommend against
   this as a blanket change.** The narrower, useful part of the instinct — "a lone surviving frame
   shouldn't be thrown away just because it's the only one" — is closer to Option A below, which
   targets the near-uniform filter, not the duplicate filter.
2. **"Fall back to pHash or audio when DINO fails."** Traced through
   `MatchingSession.PrepareAsync` (lines 124–141) and `MixedDensitySampler.GatherFrames`: pHash is
   **not independent of this failure** as currently wired. `SamplePHash`/`SampleWithPHash` both
   draw their frames from the identical `GatherFrames` → `FrameQuality.SelectUsable` pipeline DINOv2
   uses — the exact same `usableCount < 1` check gates pHash-only mode too. A bumper that collapses
   to zero usable frames for DINO collapses to zero usable frames for pHash as well, for the
   identical reason, in the same call. **pHash cannot serve as a fallback here without first being
   decoupled from `FrameQuality`'s filter** (see Option C). Audio (`AudioBumperMatcher`) genuinely
   is independent — it never touches `FrameQuality` or frame sampling at all — but
   `docs/design/matcher-spec.md` is explicit that audio is a secondary accelerator specifically
   because many of this project's actual target bumpers are silent studio/network idents ("the only
   path validated on the bumpers this project exists to remove ... which audio cannot touch"). A
   purely visual static end-card is exactly the case most likely to have no distinguishing audio
   either — falling back to audio may simply trade one silent failure for another on the bumpers
   this issue actually describes. Worth having as a documented last resort (Option E), not assumed
   to be a real fix for the common case.

**Candidate solution directions, informed by the above:**

- **A. Relax `MinDetail` specifically for the "collapsed to ~1 distinct frame" case.** Today the
  same bar (1.0) applies whether there are 20 distinct candidate frames to choose the best of or
  exactly 1. When duplicate-collapse leaves only a single surviving frame (or a very small handful),
  trust it rather than discarding the clip's only evidence outright. Doesn't touch the duplicate
  guard at all, so the Finding-3 evidence-inflation bug stays fixed — this only changes what happens
  to the one frame that's left after deduplication already ran. Lowest regression risk of the
  options here, but needs the same kind of calibration pass `MinDetail`'s original 1.0 value got
  (real frame grids, not a guess) before picking a relaxed bar or a frame-count cutoff.
- **B. Widen the sampled window to capture more of any fade in/out at the bumper's true boundary.**
  Helps genuine fades (more chances at a non-uniform frame near the transition); does nothing for a
  bumper that's flat for its entire real duration, which is exactly the case in the live 2026-08-05
  repro. **Tried by the maintainer (2026-08-05): no change in outcome.** Consistent with — and
  further evidence for — the "flat for its entire real duration" read above: if any part of the
  window had contained a fade or other non-uniform content, widening should have surfaced it, so a
  null result here is informative, not just a shrug. **Ruled out as sufficient on its own** — an
  edge-window change can't manufacture detail in a bumper that has none anywhere. Doesn't invalidate
  A/C/D below, which don't depend on the window containing any non-uniform content in the first
  place.
- **C. Give pHash its own, independently-tunable low-information filter, decoupled from
  `FrameQuality`.** Turns "fall back to pHash" from currently-false (see above) into something that
  could actually be true — pHash's false-positive risk from near-uniform frames may not be identical
  to DINOv2's (untested assumption; pHash and cosine-similarity-on-embeddings are different
  mechanisms and could tolerate different content differently), so a separately-calibrated bar (or
  none at all, with pHash's own similarity threshold doing the real work instead) is plausible. Real
  work: needs its own calibration pass, not a mechanical "reuse `FrameQuality` minus one check."
- **D. Turn the reference-clip "zero usable frames" case from a hard abort into a loud, explicit
  degraded-evidence path** — e.g., when filtering leaves nothing, fall back to the single least-
  uniform raw sampled frame despite it failing `MinDetail`, but flag the session (and every
  resulting match) as lower-confidence so results are legible rather than silently equivalent to a
  clean match. This is the option that actually closes "this bumper cannot be used as a reference
  clip at all today," independent of whether A/B/C also ship.
- **E. Automatic audio fallback when visual AND pHash both produce zero usable reference frames.**
  Only helps the subset of static bumpers that carry distinguishing audio (per the analysis above,
  likely a minority of this project's actual target bumpers) and would mean relaxing
  `matcher-spec.md`'s explicit "must never be the only path consulted" rule for this one degenerate
  case — a real design-doc change, not just code, so it needs its own maintainer sign-off rather
  than being bundled into this pass by default.

**Recommendation:** A is the safest first move and most consistent with how this project has fixed
filtering issues before (narrow, calibrated, doesn't touch the part of the filter that's working
correctly). D is what actually resolves "I cannot use this bumper at all," and is worth doing
alongside A rather than instead of it. **B has now been tried and ruled out** — no change in
outcome, confirming the target bumper has no non-uniform content anywhere in the window, not just
near the sampled edge — so it's dropped as a candidate fix (not retested further) rather than kept
open. C is
only worth it paired with a real calibration pass — not as a blind "reuse the DINO filter minus one
line" fallback, since that's what made pHash's current "fallback" illusory in the first place. E is
the most speculative of the five and probably not worth building until A/B/D are shipped and a real
static bumper is *still* unmatchable after them.

**Before implementing anything here:** this needs the same evidence discipline the original
2026-07-18 fix and `MinDetail`'s own calibration used — a real static-bumper repro (the 2026-08-05
file already on record, or a fresh one from this dogfooding session), a `--dump-frames` grid of it,
and measured detail/duplicate numbers on the actual frames, not a threshold picked by guessing.

**Open questions for the maintainer:**

- How far should `MinDetail` relax under Option A, and by what rule (frame count? something else)?
  Needs real numbers before picking one.
- Is Option D's "degraded confidence" soft-fail an acceptable default, or should a genuinely flat
  bumper keep hard-failing, just with clearer guidance (e.g., pointing at `--dump-frames` up front)?
- Is decoupling pHash from `FrameQuality` (Option C) in scope for this pass, or a separate,
  later calibration effort?
- Is Option E (audio-fallback-of-last-resort) worth building now, given it conflicts with
  `matcher-spec.md`'s current explicit design rule, or should that rule stand as-is?

## Native FFmpeg binding for scanning/sampling — implemented (2026-08-03)

**Status: implemented and live-verified against real media (2026-08-03).** `SampleFrames` (the
dense, closely-spaced case) has real native decode with CLI fallback; `SampleKeyframes` was
deliberately left CLI-only (a scope narrowing found during implementation, not the original
plan — see the ADR's "Live-verified" section). A real bug (a double-EOF-flush crash-to-fallback)
was found and fixed during verification. One important caveat found during verification: most
typical ffmpeg installs (static builds — this project's own dev machine included) have no shared
libraries, so native decode silently never activates for them without a separate acquisition
step VBR.CLI doesn't have yet. See [`docs/decisions/0015-native-ffmpeg-binding.md`](decisions/0015-native-ffmpeg-binding.md)
for full details — this entry is kept as a historical record of the original plan.

### Context

Comparing VDF's own GUI settings against VBR's scanning/matching pipeline (screenshots reviewed
2026-08-02) surfaced a real, already-built, currently-unused optimization: VDF.Core ships a native
FFmpeg binding (`FFmpeg.AutoGen` calling `libavformat`/`libavcodec` directly) that decodes frames
in-process, never spawning `ffmpeg.exe`. VDF's own settings text is explicit about the payoff:
*"For scan speed this is usually the biggest win, bigger than GPU decoding."* VBR's own sampling
code (`VBR.Core/Fingerprinting/DenseFrameSampler.cs`, the single choke point every sampler —
`MixedDensitySampler`, `WholeFileSampler`, and `VisualBumperMatcher` indirectly — funnels through)
exclusively spawns `ffmpeg.exe` via `ProcessStartInfo` for every sampling call, with zero use of
this native path — confirmed by grep: zero `FFmpeg.AutoGen` references anywhere in `VBR.Core`.
Since `VDF.Core` is already a direct dependency of `VBR.Core` (not a separate app to integrate),
this is reuse of code already sitting in our own dependency tree, not a forklift from an unrelated
project.

### What already exists in VDF.Core to build on

- **`VDF.Core/FFTools/FFmpegNative/VideoStreamDecoder.cs`** — opens a file (`avformat_open_input`),
  finds the best video stream, and exposes `TryDecodeFrame(out AVFrame, TimeSpan position)`: seeks
  (`av_seek_frame`) to a position and decodes forward to the target PTS, handling keyframe seek
  fallback, bad-packet tolerance (up to 64, issue #731), still-image draining (issue #801's native
  analogue), and an interrupt-callback timeout (re-armed per call, not per decoder lifetime — a
  single 15s budget across a whole batch previously starved later positions on slow files).
  Optionally takes an `AVHWDeviceType` for hardware decode (`av_hwdevice_ctx_create`), deferring
  pixel-format detection until after the first frame when hardware decode is active (10-bit content
  correctness).
- **`VDF.Core/FFTools/FFmpegNative/VideoFrameConverter.cs`** — thin `sws_scale` wrapper: source
  size/format → destination size/format (e.g. 32×32 GRAY8 for pHash, or 224×224 RGB24 — exactly
  `OnnxEmbedder.InputSide`, VBR's own AI input size — for embeddings), reusable across frames when
  the source layout is unchanged.
- **`VDF.Core/FFTools/FfmpegEngine.cs`** (`internal static class` — reachable from `VBR.Core` via
  the existing `InternalsVisibleTo` grant, ADR 0005, same as every other VDF.Core bridge this
  project has already built) — orchestrates the above with production-grade fallback behavior
  already built and battle-tested in VDF:
  - `UseNativeBinding` (public property on the internal class) is the master toggle.
  - `ShouldUseNativeBinding` additionally gates on `FFmpegHelper.CanLoadNativeLibraries` and a
    per-scan session circuit breaker: `RecordNativeFailure`/`RecordNativeSuccess` track
    consecutive per-file failures, disabling native for the rest of that scan after
    `NativeFailureThreshold` (5) consecutive failures — one summary warning instead of a
    stack-trace storm, with a `BuildNativeFailureDetail` helper that captures FFmpeg's own log
    output plus a classified plain-language hint.
  - `TryGetGrayBytesFromVideoNativeBatch` (line ~225) is the reference pattern for multi-position
    batch decode: **one** `VideoStreamDecoder` open per file, looping over requested positions,
    reusing **one** `VideoFrameConverter` across positions (rebuilt only if hardware decode hands
    back a different `sw_format` mid-file), producing both the 32×32 gray/pHash output and a
    224×224 RGB24 AI-embedding output from the *same* decoded frame when both are wanted. This is
    structurally very close to what `DenseFrameSampler` needs, with one important caveat — see
    Step 5 below.
  - `GetConfiguredHardwareDeviceType()` maps `FFHardwareAccelerationMode` → `AVHWDeviceType`
    directly (already `internal`, already reachable) — including an explicit Vulkan guard (forces
    software decode under native binding; Vulkan hardware decode segfaults the whole process
    uncatchably on some NVIDIA setups under this specific native path, issue #799).
  - `GetDenseAiFrames` (line ~995) already does a **keyframe-only sequential native decode at an
    interval** with CLI fallback built in — this is architecturally near-identical to what
    `DenseFrameSampler.SampleKeyframes` reimplements by hand via `-skip_frame nokey` + an `fps=`
    filter. Likely the single most direct reuse opportunity in this whole plan (see Step 4) —
    but its exact return shape (pixel format/size) needs verifying against what
    `WholeFileSampler`'s sparse pass actually needs before assuming a clean swap.

### Explicit scope boundary: decode-only, not encode/mux

VDF's native binding never writes an output video file — every native call reads frames for
in-process analysis (hashing, embedding) only. `VBR.Core/Extraction/ClipExtractor.cs` (writes a
real `.mkv` via stream-copy or re-encode, for the bumper catalog) and
`VBR.Core/Removal/ClipRemover.cs` (writes the actual `.vbr.` cut output) both mux/encode real
output files — outside this native path's scope entirely. **Both stay on the `ffmpeg.exe` CLI
path unconditionally.** This is a deliberate scope boundary, not an oversight: only
`DenseFrameSampler`'s two internal decode methods (`SampleFrames`, `SampleKeyframes` — both
already funnel every sampling caller through one file) are in scope.

### Step-by-step implementation plan

1. **Extend the existing `HardwareAcceleration` bridge** (`VBR.Core/Extraction/HardwareAcceleration.cs`)
   with a new `NativeFfmpegBinding` bool property forwarding to `FfmpegEngine.UseNativeBinding` —
   same bridge pattern already used for `Mode`/`HardwareAccelerationMode` (VBR.CLI has no
   `InternalsVisibleTo` grant from VDF.Core; VBR.Core does).
2. **Add a `--native-ffmpeg-binding` CLI flag** in `VBR.CLI/Commands/SharedOptions.cs`, wired the
   same way `--hardware-accel` already is: set once at the start of each command handler
   (`scan`/`match`/`remove`/`add-bumper`) that samples frames. **Defaults to `true`** (on by
   default, opt-out via the flag) — decided 2026-08-03, overriding this plan's original
   "default off" recommendation. VDF's own circuit breaker (5-consecutive-failure-per-scan
   disable, already reused as-is per Step 6) covers *repeated* native failures; it does not
   prevent a first uncaught native crash taking down the process, which remains the real risk a
   default-on posture accepts more broadly than an opt-in one would have. Live verification
   (Step 9) carries more weight under this posture, not less.
3. **Verify `GetDenseAiFrames`'s actual return shape** (pixel format, frame size, whether it
   already matches what `WholeFileSampler`'s sparse whole-file pass needs, or needs a wrapping
   conversion) before assuming step 4 is a clean swap.
4. **Rewrite `DenseFrameSampler.SampleKeyframes`** to call `FfmpegEngine.GetDenseAiFrames` when
   `HardwareAcceleration.NativeFfmpegBinding` is set (falling back to the existing CLI
   implementation otherwise, or on any native exception) — the most direct, lowest-risk reuse in
   this plan, since VDF has already built and hardened almost exactly this method.
5. **Add a new sequential-decode method** purpose-built for `DenseFrameSampler.SampleFrames`'s
   actual access pattern: **dense, closely-spaced positions across a short window** (e.g. every
   0.2–1s). This is *not* a drop-in use of `TryDecodeFrame(position)` per requested position —
   that method seeks (`av_seek_frame`) on every call, which is the right behavior for VDF's own
   sparse, spread-out positions (its batch method's actual use case) but would mean reseeking on
   every single closely-spaced position here, likely *slower* than the current CLI approach (which
   decodes the window forward once and picks frames via an `fps=` filter — no repeated seeking at
   all). **Resolved 2026-08-03 (see Open Questions below)**: split into (a) a new
   `TryDecodeNextFrame(out AVFrame frame)` primitive on `VideoStreamDecoder` — decodes forward
   *without* seeking, reusing the exact same packet-read/bad-packet/draining loop
   `TryDecodeFrame` already has, just skipping the seek-and-target-PTS part — and (b) a new
   orchestration method in `FfmpegEngine.cs`, matching `TryGetGrayBytesFromVideoNativeBatch`'s
   existing shape: seek **once** to the region start (or not at all, for the whole-file overload),
   then loop `TryDecodeNextFrame`, converting to 224×224 RGB24 via `VideoFrameConverter` whenever
   a frame's PTS crosses the next `intervalSeconds` threshold — mirroring exactly what the current
   `fps=1/{intervalSeconds}` filter chain does, just in-process. Keeps VDF's own established
   low-level-primitive-vs-orchestration layering rather than introducing a second convention.
6. **Wire the new method into both `DenseFrameSampler.SampleFrames` overloads** (the region-seeking
   one `MixedDensitySampler` uses, and the whole-file one `VisualBumperMatcher` uses directly),
   gated by `HardwareAcceleration.NativeFfmpegBinding`, falling back to the existing CLI
   implementation on any native failure. **Resolved 2026-08-03**: share `FfmpegEngine`'s existing
   static per-scan health-tracking/circuit-breaker state directly rather than maintaining a
   separate instance — zero new tracking code, and more correct, since Step 4's `GetDenseAiFrames`
   reuse and this new method both run in the same VBR.CLI process during a single scan, so a
   native failure in one very likely shares a root cause with the other.
7. **GPU decode wiring for the native path**: reuse `FfmpegEngine.GetConfiguredHardwareDeviceType()`
   directly rather than reimplementing the `FFHardwareAccelerationMode` → `AVHWDeviceType` mapping.
   `auto` has no `AVHWDeviceType` equivalent (matches VDF's own settings UI disallowing "auto" with
   native binding). **Resolved 2026-08-03**: since `--hardware-accel` defaults to `auto` (ADR 0013)
   and `--native-ffmpeg-binding` now also defaults to `true` (Step 2), this combination is the
   default experience, not an edge case — rejecting it was ruled out. When `auto` is requested
   under native binding, probe-by-attempting a platform-appropriate list of `AVHWDeviceType`
   candidates (`d3d11va`/`dxva2` on Windows, `vaapi` on Linux, `videotoolbox` on macOS), falling
   back to `none` if none work — reusing the same probe-by-attempting pattern `GpuEncoderProbe`
   (ADR 0013) and DirectML device selection already established in this codebase, rather than
   inventing a new detection mechanism.
8. **`ClipExtractor`/`ClipRemover` stay on the CLI path — no changes.** Confirm this explicitly in
   code review; don't let native-binding wiring creep into either file.
9. **Testing and live verification**:
   - `dotnet build` clean, `dotnet test VBR.Tests`/`dotnet test VDF.Core.Tests` clean throughout.
   - **Correctness parity is non-negotiable — speed is the only thing allowed to differ.** Live
     run the same real media (the established Daredevil/Caprica verification set) through both
     `--native-ffmpeg-binding` on and off, confirm byte-identical or equivalent-within-tolerance
     sampled frames and identical match results (`present=N/M`, `bestCos=`) either way.
   - Time a real multi-file library scan/match run before and after, to quantify the actual win —
     don't just trust VDF's own settings text applies at the same magnitude to VBR's edge-focused,
     shorter-window sampling pattern (VDF's own win is measured on its own whole-file/keyframe
     sampling pattern, not necessarily identical to VBR's).
   - Deliberately trigger a native failure (e.g. a corrupt or unusual file) and confirm the
     graceful per-file fallback actually engages and the scan continues, rather than aborting.
10. **Docs**: update `docs/decisions/0006-edge-focused-fingerprinting.md` and/or
    `docs/design/matcher-spec.md` if the new native sequential-decode method changes any previously
    documented sampling-behavior nuance; update `AGENTS.md`'s ADR index for the new ADR 0015.

### Not done here — native encode/mux (deferred, separate effort)

Deliberately out of scope for this plan, not an oversight: **VDF.Core has no native encode/mux
code to build on at all.** `FFmpegNative/JpegFrameEncoder.cs` encodes single still-image
thumbnails only; nothing in VDF.Core writes video output natively, because VDF (a duplicate
finder) never needs to. Unlike decode — where this plan reuses hardened, already-shipped code —
a native encode path for `ClipRemover`'s re-encode step would mean building an entirely new
`avcodec_send_frame`/`avcodec_receive_packet`/muxer/audio-passthrough/GPU-encoder-interop
subsystem from scratch, with no head start from the fork. It's also not obviously the same class
of win: `vbr remove` does one (or a handful of) ffmpeg invocations per file, not the many small
per-position calls where process-spawn overhead dominates a scan — the "biggest win" rationale
motivating the decode-side work doesn't obviously carry over. Revisit as its own separate
ADR/plan, informed by real timing data from this decode-side work, once it's live-verified and
shipped — not folded into this effort.

### Open questions — resolved pre-implementation, plus one found during it

All four real open questions from this plan's first draft were resolved with the maintainer
before implementation started (see the inline "Resolved 2026-08-03" notes in Steps 2, 5, 6, and 7
above). The one remaining "verify, don't decide" item (Step 3's `GetDenseAiFrames` return shape)
turned out moot: `GetDenseAiFrames` is CLI-only even with native binding on, so `SampleKeyframes`
was never rewired to use it at all — see the ADR's "Live-verified" section.

**New, found only during implementation, not anticipated in this plan**: VBR.CLI has no way to
acquire a *shared* FFmpeg build (the kind native decode actually needs — most typical installs,
including this project's own dev machine's Chocolatey ffmpeg, are static builds with no shared
libraries at all). The fallback is completely safe (silent, correct CLI fallback), but it means
native decode currently provides no benefit for most real-world installs until this gets a real
acquisition path (VDF.GUI/Web have `FfmpegDownloader`; VBR.CLI doesn't) or clear user-facing
documentation. Tracked in the ADR's own Open Questions, not decided or scoped yet.

## CLI feedback during remove — implemented (2026-07-29)

**Status: implemented and live-verified against real media, both removal modes.**

Raised by the maintainer: `vbr remove` gave zero console feedback between starting work on a
candidate and printing its final result row — worst during a re-encode removal, which can run as
long as encoding the whole file normally would, with nothing printed in the meantime. A user with no
way to distinguish "still working" from "hung" understandably loses confidence partway through a
long library run. Three asks: (1) show which video is currently being checked, (2) indicate when a
removal is actually in progress (not just matching), (3) show progress on that removal if possible.

### What changed

**`VBR.Core.Removal.ClipRemover`** gained a `RemovalProgress` record (`Processed`/`Total`/
`SpeedMultiplier`) and an optional `Action<RemovalProgress>? onProgress` parameter on `Remove`,
threaded through all four `RunFfmpeg*` cut paths (both stream-copy and both re-encode variants) down
into the one shared `RunFfmpeg` method. Every ffmpeg invocation now passes `-progress pipe:1`
(previously nothing consumed stdout at all — `ReadToEndAsync` just drained and discarded it, purely
to avoid the pipe-buffer deadlock the class's own doc comment already warns about for stderr). The
old `ReadToEndAsync()` on stdout was replaced with a new `ReadProgressAsync` that reads
`-progress`'s repeating `key=value` block line-by-line *while ffmpeg is still running*, invoking
`onProgress` once per block (ffmpeg's own default stats cadence, roughly twice a second — no
client-side throttling needed, confirmed live). Parses `out_time=` (the `HH:MM:SS.ffffff` string)
rather than the same-block `out_time_ms` key — that key is actually **microseconds**, a
long-standing ffmpeg naming quirk; the human-readable string sidesteps the unit ambiguity entirely.
`speed=` is parsed too, when ffmpeg has reported one yet (it can be briefly absent right at start).
Wired uniformly across both stream-copy and re-encode (not a re-encode-only special case) — cheap,
and stream-copy's near-instant completion means it's a non-issue there either way (confirmed live: a
5.5s stream-copy cut reported exactly one progress tick at "100%").

**`VBR.CLI.Commands.RemoveCommand`**'s per-candidate loop now prints, unconditionally, before any
work starts on a file: `[i/N] Checking: <name>` — direct answer to ask (1), and also a running
library-wide progress counter (ask 3's other half: not just per-file ffmpeg progress, but "how far
through the whole library are we"). When a match is found, before calling `ClipRemover.Remove`, it
prints a "Match found (...) — removing bumper (...)..." line naming which of the two (very
differently paced) removal mechanisms is about to run — direct answer to ask (2), and means a slow
re-encode reads as expected, not alarming. `onProgress` renders a single `\r`-updated line showing
percent complete, processed/total time, and encode speed — live during
the cut; a trailing newline is only emitted if at least one progress tick actually printed (so a
near-instant stream-copy that only got one or zero ticks doesn't leave a stray blank line). All of
this goes to stderr, same convention as this project's existing "Note:"/error lines — stdout keeps
carrying exactly one line per candidate (the existing `RemoveRow.ToLine()`), so `--output`'s report
file (built from the `rows` list, not console text) and any script piping stdout are both unaffected.

### Live-verified (2026-07-29)

Against the same real Daredevil Netflix end-card setup used throughout this project's verification
(`daredevil-end20.mkv` + `caprica-end5.mkv` distractor), both `--re-encode` values:

```sh
[1/2] Checking: caprica-end5.mkv
     caprica-end5.mkv                                  visual: present=0/6  bestCos=65%  win=3
[2/2] Checking: daredevil-end20.mkv
  Match found (present=5/6  bestCos=98%  win=14) — removing bumper (re-encode -- this may take a while for large files)...
    44%  (3.921s / 9s, 7.64x realtime)
    99%  (8.926s / 9s, 10.1x realtime)
REMOVED  daredevil-end20.mkv                               visual: present=5/6  bestCos=98%  win=14  -> daredevil-end20.vbr.mkv

1/2 file(s) matched, 1 removed.
```

Stream-copy mode showed the same shape, completing in one tick (`100%  (5.755s / 5.506s, 2250x
realtime)` — over 100% of the raw seconds is expected and correctly not clamped in the displayed
raw values, only the percentage itself is clamped to 100%, since stream-copy's own overshoot
behavior — see `EndCutOvershootSafetyMarginSeconds`'s doc comment — means the actual cut can run
very slightly past the arithmetic target). `dotnet build`/`dotnet test VBR.Tests` clean throughout
(72 passed, unaffected — this is CLI/reporting behavior, no test asserts on console output shape).

### Not done here

No `--quiet`/verbosity-level flag for this new output (unlike `vbr scan`'s five-tier
`--console-info`) — not requested, and `remove`'s existing `--verbose` bool convention stays as the
one existing lever; revisit if a future request wants it toned down for scripted/piped use (where it
currently always prints, but on stderr, so stdout scripting is unaffected either way).

## Utilizing Databases — implemented (2026-07-29)

**Status: implemented and live-verified against real media, all four combinations.**

Modify the `remove` command to take a scanned library as an argument in addition to an ad-hoc library. Also, modify the `remove` command to take a bumper label and catalog for the bumper to be removed in addition to ad-hoc bumpers. These can be mixed-and-matched: an ad-hoc library with a bumper catalog; a library db with an ad-hoc bumper clip; ad-hoc libraries and bumper (current state); library database and catalog bumper.

New parameters for the `remove` CLI:

- `--bumper-label <label>` Uses the "default" catalog if catalog information is not supplied. Use of `--clip-from`, `-region`, and `--clip-length` with this is invalid.
- `--catalog-name <name>` to specify a bumper catalog. This must be accompanied by `--bumper-label`. Use of `--clip-from`, `-region`, and `--clip-length` with this is invalid.
- `--catalog-db-folder <folder>` to specify the folder of the bumper catalog. This must be accompanied by `--bumper-label` and `--catalog-name`. Use of `--clip-from`, `-region`, and `--clip-length` with this is invalid.
- `--library-name <name>` to specify a scanned library. Use of this with `--library` is invalid.
- `--library-db-folder <folder>` to specify the folder for the library. This must be accompanied by `--library-name`. Use of this with `--library` is invalid.

When a library database is provided, the `remove` will find any videos that already exist in the database to find any that may contain the bumper. For videos with the bumper, it will remove the bumper in the same fashion as the current ad-hoc removal. It must leverage the sampling/fingerprint data already in the database and not re-scan the video.

When a catalog database and bumper label is provided, the `remove` will use the metadata in the catalog for the named bumper, as well as the fingerprint data, to find the bumper in videos within the specified library. It must not attempt to re-extract the bumper from the source. The `remove` should be consistent with existing behavior.

### Implementation (2026-07-29)

**`--clip-from`/`--region`/`--clip-length` lost their declarative `Required = true`.** These are
`SharedOptions` instances also used by `match`/`add-bumper`, and `Option.Required` is a property of
the shared instance, not something a single command can toggle — so making them optional for
`remove`'s `--bumper-label` case would have silently made them optional for `match`/`add-bumper`
too. Fixed by dropping `Required` from all three and adding an explicit check in each of the three
commands' own actions instead (`match`/`add-bumper` unconditionally require them, matching prior
behavior exactly; `remove` requires them only when `--bumper-label` isn't given). One real type
change fell out of this: `--region` became `Option<ClipEdge?>` (was `Option<ClipEdge>`) specifically
so "omitted" is observable as `null` rather than silently defaulting to `ClipEdge.begin` — a
non-nullable enum option with no explicit default otherwise returns `default(ClipEdge)` when absent,
which would have been a silent wrong-answer bug, not a missing-required-option error. `--library-db-folder`
was promoted from `ScanCommand`-local into `SharedOptions` (mirroring how `--library-name` was
promoted earlier once `add-bumper` needed it too) since `remove` now needs the identical option.

**`MatchingSession` gained two axes, not one.** The reference (bumper) side and the candidate
(library) side were already independent in the code's shape (`PrepareAsync`/`Compare` were already
separate steps); this entry just gave each side a second possible source: `PrepareFromCatalogEntry`
(reference from a `BumperCatalogEntry`, no sampling) alongside the existing `PrepareAsync` (reference
from `--clip-from`, sampled fresh), and `CompareUsingDatabase` (candidate from a
`LibraryDatabaseEntry`, no sampling) alongside the existing `Compare` (candidate sampled fresh). The
four combinations in the plan above are exactly the 2×2 of "which `Prepare` method was called" ×
"which `Compare` method is called per candidate" — nothing in `RemoveCommand`'s per-file loop needs to know which
combo is active beyond that one branch.

**A real timestamp-origin mismatch, worth recording.** `BumperCatalogEntry.Fingerprints`
(`TimedFingerprint[]`, populated via `MixedDensitySampler.SampleWithPHash`) are **window-relative**
timestamps (seconds from the start of the sampled region) — same convention as the ad hoc
`TimedFrame`/`TimedPHash` path. `LibraryDatabaseEntry.Fingerprints` (populated via `WholeFileSampler`)
are **absolute** timestamps (seconds from the true start of the file) — that sampler always knows
the file's real probed duration, so it uses real absolute time throughout, per its own doc comment.
This is not a bug to reconcile: `VisualBumperMatcher.MatchMixedDensity`/`MatchMixedDensityPHash`
never require temporal alignment between the two sides being compared (presence matching only asks
"does this clip position appear *somewhere* in the candidate," never "at a corresponding time"), so
mixing an absolute-origin side against a window-relative one is exactly as valid as any other
pairing. It matters for exactly one thing: filtering a database entry's fingerprints down to "the
requested search window" (`MatchingSession.SearchWindowSeconds`) has to reason in the entry's own
absolute-from-BOF terms (using its already-probed `Duration` — no ffprobe call needed either),
whereas a freshly sampled candidate's window is implicitly "everything just sampled." Catalog
fingerprints need no such filtering at all (they already *are* the reference clip's own captured
extent, nothing to narrow down further) — this is why `PrepareFromCatalogEntry` maps
`entry.Fingerprints` straight into `TimedFrame`/`TimedPHash` with no windowing step, while
`CompareUsingDatabase` filters first.

**Audio got a real efficiency fix, not just a database-reuse path.** The old `AudioBumperMatcher.Match`
re-extracted the reference clip's own Chromaprint fingerprint via `ChromaprintEngine.ExtractFingerprint`
on *every candidate call* — harmless waste at ad hoc-vs-ad hoc scale, but exactly the kind of
per-candidate re-work this entire entry is about eliminating. Added
`AudioBumperMatcher.MatchFingerprints(uint[]? clip, uint[]? file, ClipRegion, float minSimilarity)` —
a static, state-free counterpart to `Match` that takes two already-computed whole-file fingerprints
instead of extracting them itself (same relationship `VisualBumperMatcher.MatchMixedDensity` already
has to the instance `Match` method). `MatchingSession` now computes/reuses the reference's fingerprint
exactly once per run (`referenceAudioFingerprint`) — ad hoc via one `ChromaprintEngine.ExtractFingerprint`
call right after extracting the reference clip, catalog via direct reuse of
`BumperCatalogEntry.AudioFingerprint` — and always compares through `MatchFingerprints` from then on,
for both ad hoc and database candidates alike. The original `AudioBumperMatcher.Match` instance method
is untouched (still exercised directly by `AudioBumperMatcherTests`'s real-media test) — this was
additive, not a rewrite of existing, working, tested code. One accessibility wrinkle: `ChromaprintEngine`
is `internal` in `VDF.Core`, and only `VBR.Core` (not `VBR.CLI`) has an `InternalsVisibleTo` grant from
it (ADR 0005) — so `MatchingSession` (in `VBR.CLI`) needed a public entry point, added as
`AudioBumperMatcher.ExtractFingerprint`, a thin wrapper rather than a second implementation.

**Candidate resolution for `--library-name` mode** enumerates `LibraryDatabase.Entries.Values`
directly (not a folder walk): skips tombstoned entries (`TombstonedUtc is not null` — the file no
longer exists on disk, nothing to remove a bumper from) and anything that's since vanished without
being tombstoned yet as a defensive extra; applies the same `.vbr.`-output filename filter and
`--exclude-folders` path filter the ad hoc path already applies, for consistent behavior regardless
of candidate source; sorted by path for deterministic output, same convention as `ResolveCandidates`'s
own sort. `--no-recurse` has no effect in this mode (a database's file list is what it is) and prints
a one-time note, same style as the existing `--dump-frames`-with-`--detection-mode audio` note.

**AI-component (ONNX) download is now conditional, not unconditional.** The old code always checked
`AiComponents.IsReady`/downloaded inside `MatchingSession.PrepareAsync` whenever visual matching was
requested. That check moved to `RemoveCommand` itself and is now skipped entirely when *both* sides
are already cached (catalog bumper + database candidates) — genuinely zero ONNX/ffmpeg work for
matching in that combination, live-confirmed (see below): no `[mixed-density]` log lines at all,
only the final removal cut's own decode for files that actually matched.

**Deliberately conservative candidate-source scoping, one gap left unfilled on purpose:** the plan's
own wording only says `--library-name` is "invalid with `--library`," not with `--file`. A
`--file`-plus-`--library-name` combination (look up one specific file's cached database entry rather
than enumerating the whole database) would be a reasonable future extension, but it's a distinct
feature (single-entry lookup by path, a different code path from "enumerate all entries") that
nothing in this entry's request actually asked for — implemented instead as a plain three-way
mutually exclusive choice (`--library` / `--library-name` / `--file`), matching this project's
standing "don't build speculative surface" preference. Revisit if a real workflow needs it.

**Live-verified** against a real Daredevil episode (`daredevil-end20.mkv`, the same Netflix end-card
used throughout this project's verification, `--region end --clip-length 8s`) plus a real distractor
(`caprica-end5.mkv`) in a scratch two-file library, across all four combinations:

| Combo | Candidate present/matched | Notable |
| --- | --- | --- |
| ad hoc library + ad hoc bumper (regression check) | 5/6 frames, bestCos=98% | Unchanged from pre-existing behavior |
| ad hoc library + catalog bumper | 24/29 frames, bestCos=99% | Reference fingerprints (29) came from the catalog, zero re-extraction |
| library database + ad hoc bumper | 6/6 frames, bestCos=100%, win=126 | Candidate window (126 fingerprints, dense+sparse merged) came entirely from the database; log shows only the reference clip's own embedding, nothing for the candidates |
| library database + catalog bumper (fully cached) | 29/29 frames, bestCos=100%, win=126 | **Zero** `[mixed-density]`/ONNX log lines at all — confirmed no AI components were touched; only the final removal cut (for the one file that matched) did any decoding |

Every combo correctly left the distractor file unmatched and untouched. All seven validation-error
paths (`--bumper-label`+`--clip-from`, `--library-name`+`--library`, `--catalog-db-folder` without
`--catalog-name`, `--library-db-folder` without `--library-name`, no bumper source given, no
candidate source given, an unknown `--bumper-label`) produced the expected clear error and a nonzero
exit. `match`/`add-bumper` regression-checked after the shared-option changes: both still correctly
require `--clip-from`/`--region`/`--clip-length` and both still function normally when given.

**Tests:** 3 new, synthetic and real-media-free (`AudioBumperMatcherTests.MatchFingerprints_*`) —
covering an exact-block match embedded in a larger array, a genuine non-match (bit-complemented
blocks, chosen because naively "different" small integers share enough leading-zero bits to
misleadingly inflate Hamming similarity), and the two null/too-short soft-failure paths. 72 tests
total (up from 69), all passing. No `VBR.Tests` coverage of `RemoveCommand`/`MatchingSession`
themselves — `VBR.Tests` doesn't reference `VBR.CLI` (the same pre-existing, already-documented gap
`add-bumper`/`list-bumpers` note), so verification of the CLI wiring itself follows this project's
established live-smoke-test convention instead.

### Extended to `match` (2026-08-13)

**Status: implemented, built, and smoke-tested.** This entry originally scoped both database axes
to `remove` only ("Modify the `remove` command to..." — see the plan text above). The maintainer
asked for the same support on `match`, having expected it was already there. Mechanical, since
nothing above was actually `remove`-specific under the hood: `MatchCommand` gained the identical
`--bumper-label`/`--catalog-db`/`--library-db` options, validation, catalog/database resolution,
and staleness-warning logic `RemoveCommand` already had — `MatchingSession.PrepareFromCatalogEntry`/
`CompareUsingDatabase` were already command-agnostic, so `match`'s per-file loop just gained the
same `candidateDbEntries is not null ? CompareUsingDatabase : Compare` branch `remove`'s loop has,
minus the removal call. `--clip-from`/`--region`/`--clip-length`'s shared `SharedOptions`
descriptions, which previously said "unless remove's `--bumper-label`," were corrected to name
both commands rather than just one now-stale one. `vbr match` is the natural dry-run/investigation
tool for a catalog-and-scanned-library setup before committing to an actual `remove`, so this
closes a real, if accidental, capability gap rather than adding new surface area. Build + full test
suite (98 passing) unaffected; `match --help`/`remove --help` smoke-tested to confirm identical
option sets on the shared axes.

## Bumper CRUD Part 1 — implemented (2026-07-29)

**Status: implemented and live-verified.** Built exactly to the plan below.

In order to effectively use bumpers, we need to see what bumpers are available. For the CLI, and looking ahead to the GUI, we need to implement the first step in *listing* the bumpers in a catalog.

The `list-bumpers` command will list the bumpers of a catalog in the following format:

```sh
"bumper label", region, length, thumbnail location
```

An example:

```sh
"Netflix ident then black 4s", end, 8s, "c:\temp\.vbrthumbs\Netflix ident then black 4s-thumbnail.jpg"
```

Takes the `--catalog-db-folder` and `--catalog-name` arguments to specify a catalog.

When a catalog is not specified, the "default" catalog will be assumed.

Thumbnails will use the user's temp folder location from the system, create a directory `.vbrthumbs`, and write the thumbnail to that folder with the label used as the name and "-thumbnail" appended. Looking forward to the GUI, serializing to disk may or may not be necessary.

An optional `--show-guids` parameter will change the output to show the GUID on a new line preceding the other output.

Example:

```sh
5e449c84-12a7-4936-95c6-f0d01753ab8a
"Netflix ident then black 4s", end, 8s, "c:\temp\.vbrthumbs\Netflix ident then black 4s-thumbnail.jpg"
```

### Implementation (2026-07-29)

New `VBR.CLI.Commands.ListBumpersCommand` (`vbr list-bumpers`), registered in `Program.cs`. Read-only
against the catalog: loads it via the existing `BumperCatalogStore.Load`/`ResolveCatalogPath` (same
mechanism `add-bumper` already uses, including the "missing catalog file loads as empty, not an
error" behavior — `list-bumpers` reports `Catalog '<name>' has no bumpers.` and exits 0 rather than
erroring), then prints one line per entry in the plan's exact format. Entries are sorted by `Label`
(case-insensitive, matching this project's existing convention for deterministic listing output —
same rationale as `ResolveCandidates` sorting its file list) since the plan didn't specify an order
and unsorted `Dictionary` enumeration order isn't a contract worth depending on.

`--catalog-name` defaults to `"default"` when omitted (unlike `add-bumper`, where it's required —
listing has a sensible default to fall back to; adding a bumper to an implicit catalog didn't seem
worth the same convenience). `--catalog-db-folder` reuses the identical "already a file" guard
`add-bumper` has, for the same reason. `--show-guids` prints `entry.Id` on its own line immediately
before each entry's regular line, exactly as specified.

**Thumbnail materialization.** `BumperCatalogEntry.Thumbnail` only ever stores bytes inline in the
catalog (see the "Bumper catalog" entry above) — there's no thumbnail *file* until something writes
one, and `list-bumpers` is that something. Each call re-writes every entry's thumbnail to
`{Path.GetTempPath()}\.vbrthumbs\{label}-thumbnail.jpg`, unconditionally overwriting whatever was
there before — simplest option, and cheap (thumbnails are single JPEGs, tens of KB). An entry with
an empty `Thumbnail` (capture failed at add-time — see `add-bumper`'s "never blocks adding the
bumper" note) reports `none` instead of a path, rather than writing a zero-byte file. The filename
uses the entry's raw `Label` for the *displayed* `"label"` field (matching the plan's own example
verbatim) but a separately filesystem-sanitized (`Path.GetInvalidFileNameChars()` replaced with `_`)
version for the actual file on disk — labels are free text up to 30 characters and can contain
characters Windows rejects in filenames (verified live with a label containing `:` and `/`, both
replaced, the file written without error and the console line still showing the unsanitized label).

**Live-verified** against a real scratch catalog (two bumpers added via `add-bumper` from real
media, `caprica-end5.mkv` and `daredevil-end20.mkv`): output format matched the plan's example
exactly (`"Test Bumper One", end, 3s, "...\.vbrthumbs\Test Bumper One-thumbnail.jpg"`), both real
JPEG thumbnails landed on disk (153,340 and 29,667 bytes, matching the byte counts `add-bumper`
reported when it captured them) and were loadable files, not stubs. `--show-guids` correctly
prefixed each entry with its GUID. An empty/nonexistent catalog name printed the friendly message
and exited 0; omitting `--catalog-name` fell back to `"default"` (also empty in this test, same
message). `--catalog-db-folder` pointed at an existing file failed with the same clear error
`add-bumper` gives for the identical mistake.

**Not covered by `VBR.Tests`** — `VBR.CLI` isn't referenced by the test project (a pre-existing gap
this project has already accepted for every other CLI command; see `add-bumper`'s own
implementation note above), so verification follows the same established live-smoke-test convention
rather than unit tests for command wiring. `dotnet build`/`dotnet test VBR.Tests` both clean (69
passed, unaffected, since nothing in `VBR.Core` changed).

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

### Phase 2 — Multi-folder libraries: `--library` becomes a delimited list, plus a new exclusion flag — implemented (2026-07-29)

**Status: done.** Real feature work, not a rename — touched `SharedOptions.ResolveCandidates`/`DisplayName` and every command that calls it (`match`/`remove`/`cleanup`/`scan`; `add-bumper` is unaffected, since it dropped `--library` entirely in the last round of changes).

What shipped, per the maintainer's decided shape (VDF's own "Where to look" UI — include/exclude folders as parallel, symmetric concepts):

- `--library` changed from `Option<DirectoryInfo>` to `Option<DirectoryInfo[]>` with a `CustomParser` that splits on `;`, trims, and validates each path exists (a missing folder is a parse-time error via `result.AddError`, same as any other bad argument — never silently reaches the command's action). Single flag, semicolon-delimited, as decided — not a repeatable `--library A --library B` flag.
- New `--exclude-folders` — same semicolon-delimited shape, but existence is *not* required (excluding a currently-offline folder, e.g. a dismounted share, should still work by path rather than error).
- `ResolveCandidates` now enumerates every `--library` folder, filters out anything `IsUnderAny` an exclude folder (path-prefix match, applied *after* enumeration — same trade-off as the `.vbr.`-filename filter, simpler than skipping traversal, costs a little wasted enumeration on a large excluded subtree), and deduplicates the combined list by full path — needed because overlapping/nested `--library` folders (or the maintainer's decided "same folder in more than one library" case) can otherwise reach the same file twice in one run. `CleanupCommand` (which doesn't go through `ResolveCandidates` — it walks directories, not files) got the same treatment directly: loops every library folder's directory tree, skips any directory `IsUnderAny` an exclude folder, and deduplicates directories the same way.
- `DisplayName`/`CandidateSet.LibraryRoots` changed from a single nullable root to a list — a resolved candidate's relative-path display now picks whichever of the given `--library` roots actually contains it, rather than assuming there's only one.
- `--no-recurse` unchanged — still one flag applying uniformly to every listed folder, as decided (per-folder recursion control was explicitly out of scope).
- `vbr scan`'s default `--library-name` (when not given) now derives from the *first* `--library` folder when more than one is given — the same "best-effort default, override if it's wrong" philosophy as the single-folder case, just extended to pick one of several since there's no other sensible single name to derive. All folders' files still land in one database — multi-folder `--library` does not mean multiple databases per `scan` invocation.
- Confirmed distinct from `.vbr.`-output filtering, as decided — that's a filename-pattern rule; this is a path/folder rule. Both stay, independently.
- The "same folder may belong to more than one library at once" decision needed no code change here — nothing in this implementation checks for or prevents that overlap; it's simply not a case `ResolveCandidates` (scoped to one command's one `--library` argument) has any way to observe.

Verified live: two library folders combined (3 files total, one correctly dropped by `--exclude-folders`); an intentionally overlapping pair (`dirA` + `dirA\sub`) correctly deduplicated to 2 files, not 3; a nonexistent folder in the list correctly failed at parse time with "Folder not found"; `vbr commit` (named `vbr cleanup` at the time this verification ran — see the rename below) walked both folders' directory trees (skipping the excluded one) without touching anything, since no real `.vbr.` pairs were present. Not covered by `VBR.Tests` — `SharedOptions` lives in `VBR.CLI`, which `VBR.Tests` doesn't reference (a pre-existing gap, not introduced here); verification followed this project's established CLI-layer convention of live smoke-testing rather than unit tests for command wiring.

### `vbr cleanup` renamed to `vbr commit` — implemented (2026-07-29)

**Status: done.** VDF's actual menu (screenshots reviewed 2026-07-28) has both **"Cleanup Database"** and **"Prune Ghost Entries"** — VDF's own terminology neighborhood for future database-maintenance commands (Phase 3 below). VBR's existing `vbr cleanup`/`clean` (promoting verified `.vbr.` outputs, deleting originals) occupied that vocabulary for something unrelated. **Decided (2026-07-29): `vbr cleanup` is renamed to `vbr commit`** — not `vbr promote` as earlier floated; the maintainer's own choice. Frees "cleanup"/"clean"/"prune" for Phase 3's database-maintenance commands.

What changed: `VBR.CLI/Commands/CleanupCommand.cs` → `CommitCommand.cs`, class `CleanupCommand` → `CommitCommand`, `Command("cleanup", ...)` → `Command("commit", ...)`. The `clean` alias was dropped entirely, not kept or repointed — keeping it would have defeated the point of freeing the word for Phase 3. `Program.cs`'s registration and every other command's cross-referencing help text (`ScanCommand`'s `--include-vbr-outputs`, `SharedOptions`' `--validate-files` comment) updated to match. `VBR.Core.Cleanup`/`LibraryCleaner`/`CleanupFileResult`/`CleanupOutcome`/`CleanupRunResult` were deliberately **not** renamed — this is a CLI-surface rename only; nothing in `VBR.Core` had a naming collision with Phase 3's future commands, and "cleaner"/"cleanup" as internal engine vocabulary describing what the algorithm does is still accurate regardless of what the CLI verb exposing it is called. ADR 0008 got an amendment note at the top recording the rename, rather than being rewritten — its body still says `cleanup` throughout, an accurate historical record of what was decided and built on 2026-07-20, same convention this document follows for its own dated entries. `docs/running_and_building.md` updated throughout (section header, example commands, cross-references from `match`/`scan`'s own docs). `PROGRESS.md`/`AGENTS.md`'s mentions were checked and left as-is — all inside their own dated historical entries.

Verified live: `vbr commit --help` shows the new description; `vbr cleanup`/`vbr clean` no longer resolve as subcommands (System.CommandLine falls through to root help rather than erroring, existing framework behavior, not something this change controls); the top-level `vbr --help` command list shows `commit`, not `cleanup`; a real `vbr commit --library <folder>` run against a folder with no `.vbr.` pairs completed cleanly (`0 cleaned, 0 broken, 0 pending reclamation.`, exit 0). `dotnet build`/`dotnet test VBR.Tests` clean throughout (69 passed, unaffected — no `VBR.Tests` coverage touches the CLI layer either way).

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
- **`vbr cleanup` → `vbr commit`** — implemented 2026-07-29, see the dedicated section above.
- **Tombstoning** — adopt it; implemented 2026-07-29, see the dedicated section above.
- **Bumper label uniqueness — unique within a catalog, not globally. Implemented (2026-07-29).** Two different catalogs may each have a bumper labeled e.g. "Studio ident" without conflict. The check landed in `AddBumperCommand` (not `BumperCatalogBuilder`), right after the catalog is loaded and before the builder is called at all — `catalog.Entries.Values.Any(e => string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase))`, case-insensitive. Deliberately placed before the expensive part (ffmpeg decode + ONNX inference in the builder), same "fail fast on a cheap check" principle as the existing `--catalog-db-folder`-is-a-file guard right above it in the same file — a duplicate label is rejected instantly instead of after a full sample-and-extract pass. `BumperCatalogEntry`/the data model are unchanged, as anticipated — this is CLI-level validation only, nothing persisted differently. `--label`'s help text updated to document the constraint. Verified live: exact-duplicate label rejected; same label differing only in case rejected; same label in a *different* catalog accepted; a different label in the *same* catalog accepted. `dotnet build`/`dotnet test VBR.Tests` clean (69 passed, unaffected — this is CLI-only, `VBR.Tests` doesn't reference `VBR.CLI`).
- **Orphaned bumpers (source doc Portability case/handling #9) — no surfacing needed.** `BumperCatalogEntry.SourceVideoPath` is informational provenance metadata only; it stays exactly as-is when the source file it names disappears, and its unresolvability has no effect on the bumper's validity or matching utility. This closes the "unresolved technical consequences" the source doc flagged — the resolution is that there *are* no technical consequences worth building for, given today's data model. No code change implied.

### Open questions

None outstanding as of 2026-07-29 — every item raised in this entry's planning pass has a maintainer decision recorded above, and every decided-but-unbuilt item (Phase 1 rename, Phase 2 multi-folder libraries, tombstoning, the `vbr cleanup` → `vbr commit` rename, and catalog-scoped bumper-label uniqueness) is now implemented. What's left from this entry is Phase 3 (database-maintenance commands) — sketched, now unblocked (the `commit` rename it was waiting on has landed), but not yet started — plus the items already logged under "Later, not sequenced in detail here" above (bumper CRUD, catalog-aware "apply" matching, GUI work).

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
| --- | --- | --- | --- |
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
| --- | --- |
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
| --- | --- | --- |
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
| --- | --- | --- | --- |
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

1. **Recursive library traversal by default.**
   [`MatchCommand.cs:198`](../VBR.CLI/Commands/MatchCommand.cs#L198) currently enumerates a single
   folder. Switch to `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }`,
   add a `--no-recurse` switch, update the `--library` help text (it currently says
   "non-recursive"), and print **library-relative paths** so same-named files in different
   subfolders remain distinguishable.

2. **`--output <file>`.**
   Write the same per-file lines + summary to a file. The probe already did this (its
   `visual-tail-results-*.txt`); the feature was lost during productionization. Restructure each row
   into a small record while doing this so a later `--output-format json` follows cheaply
   (`VDF.CLI` already has a JSON-output precedent).

3. **Optional but recommended: `--dump-frames <dir>` diagnostic.**
   Write the sampled clip/window frames as images. This diagnosis required rebuilding the pipeline
   by hand; this switch makes the next "why did this match?" a ten-second glance.

#### C. Re-validation matrix — PASSED (2026-07-18, all five runs; `--detection-mode visual`, 0.2s interval)

| Test | Expectation | Result |
| --- | --- | --- |
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
