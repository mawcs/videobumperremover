# ADR 0008: The `cleanup` command — filename-derived pairing, pairwise commit, no secondary trash stage

- **Status:** accepted. The three items the maintainer flagged for a final call are decided
  (below); everything else in "Open questions" remains genuinely open.
- **Date:** 2026-07-20
- **Implementation status (2026-07-20):** built and verified. `VBR.Core.Cleanup.LibraryCleaner` +
  `VBR.CLI.Commands.CleanupCommand`. 18 unit tests (temp-directory-based, no curated real-media
  library needed for the core mark/promote/delete/recovery logic — only the two `--validate-files`
  tests shell out to ffmpeg/ffprobe, same as `ClipRemoverTests`) plus a live end-to-end run through
  the compiled CLI (recovery sweep + main pass + reporting, including a simulated interrupted-run
  marker that got restored and cleaned in the same invocation). **Follow-up same day:** manifest
  renamed to `name.json` (see Decision 3's implementation note); tests and docs updated to match,
  full suite re-verified green.
- **Related:** [`0007-removal-command.md`](0007-removal-command.md) (reserves the `cleanup` name —
  Decision 4; defines the `.vbr.` sibling-output convention and `ClipRemover.BuildOutputPath` this
  command inverts; records the "already-cut `.vbr.` as `--clip-from`" risk this command's existence
  resolves), [`../AGENTS.md`](../AGENTS.md) (standing "never modify source videos in place" /
  "verification before destruction" rules this command operationalizes), [`../ROADMAP.md`](../ROADMAP.md),
  [`../PROGRESS.md`](../PROGRESS.md)

## Context

ADR 0007 made `remove` strictly non-destructive: it only ever writes a `name.vbr.ext` sibling plus
a manifest (`name.json`, named after the *original* — see "Implementation findings," 2026-07-20),
next to the untouched original. It explicitly reserved a `cleanup`
command (name only, not built) to do the actual replace-original step, framing it as "where
verification before destruction is actually enforced" — and left its design (verification gate,
replace-vs-delete-original policy, backup retention) as an open question.

Two things motivated designing it now:

- **Multi-pass workflow.** A user will often remove more than one bumper from the same file across
  separate `remove` invocations (one bumper per pass — `remove`'s hard rule is BOF/EOF-anchored,
  arithmetic cut points with no per-file boundary detection, see ADR 0007). ADR 0007 logged an open
  risk (maintainer test, 2026-07-19): pointing `--clip-from` at a prior `.vbr.` output while its
  untouched original still sits in the same library produces a silently-wrong cut on every file
  the run matches. **Resolved in discussion (2026-07-20, recorded in ADR 0007): no guard added to
  `remove`.** The intended workflow already avoids this — validate a `remove` pass, run `cleanup`
  to commit it, *then* start the next pass. This ADR is what that resolution depends on existing.
- **An initial design sketch over-engineered the safety mechanics.** A first draft (discussed
  2026-07-20) paired original ↔ output via the JSON manifest and added a soft-delete/trash stage
  before the real delete. Both were reconsidered and dropped — see Decisions 3 and 5 for the
  reasoning, which came directly from the maintainer:
  - The manifest is not a dependable pairing mechanism. It can be separated from the video file,
    can point at a file that's since moved or been deleted, or can itself be deleted by a user who
    assumes it's disposable clutter while the video remains. The only reliable source of truth for
    "does this file exist" is the filesystem itself.
  - `remove`'s non-destructive sibling output *already is* a recycle bin — the original survives
    untouched for the entire review window between `remove` and `cleanup`. A second staged-deletion
    layer inside `cleanup` would duplicate that safety mechanism while tripling transient disk usage
    on libraries that are already large from `remove` alone.
  - At the CLI there's no way to enforce that a user has actually reviewed a `.vbr.` output before
    running `cleanup` — that has to wait for a UI. An **opt-in, assistive** integrity check is the
    right amount of automation for now.

## Decision

1. **New CLI command: `vbr cleanup`.** It promotes a verified `.vbr.` output to replace its
   original — delete the original, rename the output into the original's name, delete the
   manifest. **This is the only command permitted to delete video files or manifests**; `match`
   and `remove` never do (AGENTS.md's "never modify source videos in place" rule holds for both).

2. **Traversal and scope.** `--library <folder>` (with `--no-recurse` for top-level-only) walks the
   directory tree and processes **one directory at a time** — within a directory, only files that
   currently have a live `.vbr.` counterpart are ever touched; an original with no `.vbr.` sibling
   is not inspected, marked, or renamed. `--file <path>` is scope-narrowed differently — see
   Decision 10, which supersedes ADR 0007's original "reuse `ResolveCandidates`/`CandidateSet`
   unchanged" note now that `cleanup`'s per-directory design makes `--file` genuinely different
   from how it works for `match`/`remove`, not just a smaller candidate list.

3. **Pairing is filename-derived, not manifest-derived.** Original ↔ output pairing uses the exact
   inverse of `ClipRemover.BuildOutputPath` ([`ClipRemover.cs:180`](../../VBR.Core/Removal/ClipRemover.cs))
   — strip a trailing `.vbr` infix immediately before the extension, anchored the same way
   `BuildOutputPath` builds it (not a substring/`Contains` match anywhere in the path). Add a
   round-trip unit test (`Inverse(BuildOutputPath(x)) == x`) alongside the existing
   `BuildOutputPath_InsertsVbrBeforeExtension_BesideSource` theory
   ([`ClipRemoverTests.cs:41-46`](../../VBR.Tests/Removal/ClipRemoverTests.cs)) covering the same
   cases, so the two functions can't silently drift apart. The manifest remains a diagnostic
   artifact only (Decision 6) — never a dependency for deciding what to touch.

   **Implementation finding (2026-07-20):** writing this test surfaced a real bug before it
   shipped — the naive inverse, applied to `name.vbr.json` (the manifest's original filename),
   parsed as "a `.vbr.` output named `name.vbr`, extension `.json`" and would have offered up a
   bogus `name.json` "original" to promote onto. Fixed by requiring the recovered original to end
   in a recognized video extension (`ClipExtractor.VideoExtensions`). **Separately, the same day,
   the manifest itself was renamed** to `name.json` (derived from the original path, not the
   output — see ADR 0007's "Implementation findings," 2026-07-20), which sidesteps this exact
   scenario entirely — `name.json` doesn't even superficially resemble the `.vbr.` pattern. The
   video-extension guard stays anyway, per the maintainer, as defense against any other
   `.vbr.something` non-video file, not just this one.

4. **Content verification is opt-in: `--validate-files`, off by default.** When passed, before a
   candidate is allowed past pairing, ffprobe the `.vbr.` file and confirm it opens and its
   duration is plausible. Prefer a precise check against the manifest's recorded source duration
   and cut point when that manifest is present and parses (tolerating stream-copy's keyframe-snap
   slack, the same margin `ClipRemover` already uses); **degrade to a coarser structural check**
   (file ffprobes successfully, non-zero duration, shorter than the paired original) when the
   manifest is missing or unreadable — per Decision 3, `--validate-files` can *use* the manifest
   opportunistically but must not *require* it. A file that fails validation is treated exactly
   like a mark/promote failure (Decision 7): logged, added to the broken list, original untouched,
   never proceeds to renaming. Off by default because the CLI cannot enforce that a human has
   actually reviewed the output — this is an assist, not a substitute, and a future UI can make
   more of this a default gate.

5. **No soft-delete/trash stage.** Once a file's swap is committed (Decision 7), the marked
   original and its manifest are deleted directly. See Context — `remove`'s coexistence window is
   the recycle bin; `cleanup` is the commit, not a second one.

6. **The manifest is deleted alongside cleanup**, best-effort, same treatment as deleting the old
   original (Decision 7e). It has no purpose after the swap it describes is committed.

7. **Per-file processing is pairwise (mark → promote → delete, fully resolved before the next
   file), not phase-batched — and rollback covers only the swap, never the delete.** For each
   candidate, in order:
   a. *(if `--validate-files`)* Validate (Decision 4). Failure → log, add to the **broken** list,
      move to the next file. Original untouched.
   b. **Mark:** rename the original `name.ext` → `name.ext.vbrdelete` (**decided**, maintainer,
      2026-07-20 — full original name plus a trailing marker, not `.vbrdelete` inserted before the
      extension, so the marked file's last extension is no longer a video extension; this
      incidentally shrinks the window in which a player, indexer, or AV scanner might open/lock it
      while it's mid-flight). Failure → nothing changed yet; log, add to broken list, next file.
   c. **Promote:** rename `name.vbr.ext` → `name.ext`. Failure → **roll back (b)**: rename
      `name.ext.vbrdelete` back to `name.ext`. If that rename *also* fails, log it distinctly as
      needing manual attention — this is the one case no in-process logic can fully resolve. Either
      way: log, add to broken list, next file.
   d. Once (b) and (c) have both succeeded, **the swap is complete and correct.** `name.ext` now
      holds the cleaned content. Nothing past this point ever undoes it.
   e. **Delete** the marked original (`name.ext.vbrdelete`). Best-effort: failure is logged and the
      file is left in place for the next run's recovery sweep (Decision 8) to retry. **Not** added
      to the broken list — the correctness-relevant work already succeeded. Tracked separately as
      **pending reclamation**.
   f. **Delete the manifest** (`name.json`, next to the original — see Decision 3's implementation
      note). Same best-effort treatment as (e).

   Rationale for narrowing rollback to (b)/(c) only: once the swap succeeds, a failure in (e) is a
   disk-space problem, not a correctness problem. Unwinding an already-successful, already-correct
   promotion because a *subsequent, independent* delete failed is strictly worse than leaving the
   old file in place — the original design sketch's rollback-after-delete-failure path could, if
   the same lock that broke the delete also broke the rollback's own rename, leave **neither** the
   original nor the new content reachable under `name.ext` at all.

8. **Startup recovery sweep, per directory, unconditional (no flag).** Before the main pass, scan
   for stray `name.ext.vbrdelete` files with no corresponding `name.vbr.ext` — unambiguous evidence
   of a previous run interrupted (crashed, killed, or otherwise not cleanly completed) between
   Decision 7's steps (b)–(f). Reconcile each:
   - `name.ext` already exists → promotion had completed; only the delete (7e) was interrupted.
     Retry the delete.
   - `name.ext` does not exist and `name.vbr.ext` still exists → interrupted between mark and
     promote (7b/7c). **Conservatively restore**: rename the marked file back to `name.ext`, and
     let the normal pass reprocess the pair from scratch. Resuming forward instead was considered
     and rejected — an abnormal termination doesn't tell us enough to safely assume "finish the
     swap" reflects the last-known-good intent, whereas restoring the known-safe original state
     matches AGENTS.md's "verification before destruction."
   Runs unconditionally, unlike `--validate-files`, because it's cheap (directory listing only, no
   ffprobe) and purely restorative — it only ever acts on files already in a visibly broken
   (interrupted) state, never a settled one. Recovery actions are logged under their own tally, not
   mixed into the current run's broken / pending-reclamation counts; a restored pair simply becomes
   a normal candidate for the pass that follows.

9. **Two-tier failure tracking, reset per directory, reported as a whole-run summary printed by
   default.** **Broken** (validation, mark, or promote failed — needs the user's attention) is
   kept separate from **pending reclamation** (swap succeeded, the follow-up delete didn't —
   self-heals via Decision 8 on a future run, not urgent). The broken list is scoped to and cleared
   after each directory, so one bad file doesn't block unrelated folders elsewhere in the tree. At
   the end of the run, print a summary (files cleaned / broken / pending reclamation, with reasons)
   to the console by default — not only to `log.txt` behind `--verbose` — mirroring how `match`/
   `remove` already print a report. `--output <file>` is supported to also write it to a file, for
   the same reason it exists on `match`/`remove`. `--verbose` is supported too, logging each
   rename/delete call and recovery-sweep action, consistent with how `remove` logs its ffmpeg
   commands today.

10. **`--file <path>` touches only that one file's own pairing/marker state — never the rest of
    its directory (decided, maintainer, 2026-07-20).** For `match`/`remove`, `--file` is just a
    one-element candidate list run through the same logic a `--library` scan would use. `cleanup`
    can't reuse that shape as-is: `CleanDirectory` (Decisions 7-8) is inherently directory-scoped
    (it lists every `.vbr.` file present and sweeps every stray marker in that directory), so
    pointing it at a single file's directory would incidentally also touch unrelated sibling
    episodes. Instead, `--file <path>` resolves the one target/original pair directly (via
    Decision 3's pairing, trying both directions — the path given may be the original or the
    `.vbr.` output) and runs Decision 7's per-pair steps, plus Decision 8's recovery check, on
    **only that pair** — no directory listing at all.
    **Why "the same directory as the file":** "the current file as it exists in the library" is
    the intended scope, but without a library database there is no notion of a file's identity
    beyond its current path — `remove` always writes the `.vbr.` sibling next to the source
    (ADR 0007, Decision 3), so today "the current file as it exists" and "the same directory as
    the file" are the same thing by construction. **Explicitly left open:** once a library
    database/index exists, "the current file as it exists in the library" may need to resolve
    through it instead (e.g. after a file has been moved) — not designed here, deferred until
    that database exists.

## Consequences

Positive: pairwise processing bounds the blast radius of an interruption to at most one file at a
time; the recovery sweep makes a crashed run self-healing on the next invocation instead of
requiring manual cleanup; no dependency on a side-channel file for a decision that deletes video
data; no tripling of disk usage; rollback is narrow enough that it cannot cascade into a state
where a file is reachable under *no* name.

Negative / watch-outs: filename-derived pairing means a `.vbr.` file that's been manually renamed
won't be found by `cleanup` — accepted as the same class of risk as any convention-based tool, and
strictly safer than trusting a detachable manifest instead; `--validate-files` being opt-in means a
default `cleanup` run performs **zero** automated integrity checking, fully trusting the user's own
review (a deliberate choice — see Context — but worth restating plainly); a promote-then-rollback
failure where *both* renames fail still requires manual intervention — no algorithm closes that gap
completely; the recovery sweep's "restore, don't resume forward" default means an interrupted swap
always costs a full reprocess on the next run rather than finishing where it left off — a
deliberate conservatism trade-off, not an oversight.

## Open questions

- **`--dry-run`: decided, deferred (maintainer, 2026-07-20).** Not built now. Reconsideration
  isn't warranted on cost grounds: Decision 7's per-pair steps are already a clean plan-then-act
  sequence (compute paths and decisions first, mutate — `File.Move`/`File.Delete` — only at each
  named step), so gating those specific calls behind a flag is the same small, mechanical change
  whenever it's done. Building it now wouldn't avoid any rework later; it just wasn't asked for.
  Worth revisiting only if real usage surfaces an actual need to preview a run, not pre-emptively.
- **`--validate-files` duration tolerance** — reuse `ClipRemover`'s existing safety-margin constant
  directly, or does `cleanup` need its own (validation is checking a finished file after the fact,
  not choosing a cut point, so the right tolerance may not be the same number)?
  Not resolved here.
- **Pending-reclamation retry policy** — currently "the next `cleanup` run's recovery sweep retries
  it," with no in-process retry/backoff. Sufficient for a single-user desktop tool; flagged in case
  it isn't.
- Manifest schema itself remains open per ADR 0007 — unchanged by this ADR.
