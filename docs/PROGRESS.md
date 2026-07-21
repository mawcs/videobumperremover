# Project progress & task log

Durable export of the working task list from the initial Cowork build session (the ephemeral
task list doesn't transfer between tools). Kept as the running record — check items off and add
new ones as we go. For the *why* behind any of this, see the docs referenced inline and the
findings log in [`research/vdf-evaluation.md`](research/vdf-evaluation.md).

## Milestone: MVP reached (2026-07-20)

The full pipeline now works end-to-end and non-destructively, verified against real media, not
just synthetic tests: `vbr match` finds a bumper across a library from one hand-picked reference
clip; `vbr remove` cuts it — stream-copy or frame-accurate re-encode — into a sibling file,
never touching the original; `vbr cleanup` (alias `clean`) promotes reviewed outputs to replace
their originals once trusted.

**In the maintainer's own words, marking this moment:** *"I could use this tool as-is to begin
cleaning up my library and it would be an improvement over my existing manual process of
reviewing each file to find how much to trim off of it. Sure, it's slow, clumsy, and open to
errors and problems if I don't handle it right. But, to me, that meets the definition of MVP."*
The gain isn't that the tool measures a bumper for you — by design it doesn't, and per this
project's hard rule it never guesses a cut point per file (ADR 0007) — it's that a bumper only
needs to be measured **once**, by hand, and is then applied to every file that contains it,
instead of eyeballing every file individually.

**What MVP does *not* include**, scored honestly against the "five hard parts" at the top of
[`ROADMAP.md`](ROADMAP.md):

- **Detection — partial.** No per-file boundary search; BOF/EOF-anchored arithmetic only (a
  deliberate hard rule, not a gap to close casually). Mid-video interstitials aren't handled.
- **Fingerprinting + matching — done.** Visual DINOv2 presence matching (primary), audio
  Chromaprint (accelerator).
- **The bumper catalog — not started.** Every run takes a fresh `--clip-from`; nothing persists
  across invocations, so a bumper identified today isn't remembered for the next rip.
- **Verification UX — not started.** "Review before cleanup" today means dropping files into an
  external player by hand; nothing in this tool shows you what changed.
- **Removal — done.** Both modes, plus the non-destructive `remove` → `cleanup` commit split.

Also missing: any GUI, GPU acceleration (CPU-only re-encode, genuinely slow for full episodes),
and batching more than one bumper per invocation. This is a careful person's power tool right
now, not yet something safe to hand to someone who won't read the output.

## Completed

### Repo & project setup

- Scaffolded the repo and initialized git.
- `README.md`, `CLAUDE.md` (→ `AGENTS.md`), `AGENTS.md`.
- `.gitignore` extended for media/artifacts; `LICENSE` = AGPLv3.
- Forked **Video Duplicate Finder**; migrated our planning docs into the fork; preserved VDF's
  original README as `README.vdf.md`.
- Verified the fork builds/runs (`dotnet build VideoBumperRemover.sln`).
- Dev setup captured ([`development.md`](development.md)) — .NET 10 SDK + VS Code base **C#**
  extension (skip C# Dev Kit / no MS account); NuGet-source + AOT caveats.

### Decisions & design docs

- **ADR 0001 / 0002 — tech stack:** C#/.NET, fork of VDF, Avalonia UI, ONNX for ML, desktop over
  SMB. (0001 superseded by 0002.)
- **ADR 0003 — repo structure:** fork VDF as the main repo, migrate docs in.
- **ADR 0004 — persistent bumper catalog** (+ [`design/bumper-catalog.md`](design/bumper-catalog.md)):
  bumpers are first-class, reusable; enroll/apply; personal export/import; share fingerprints only.
- **ADR 0005 — code organization:** the `VBR.*` tree vs. VDF (see
  [`design/code-organization.md`](design/code-organization.md) for the option analysis).
- **ADR 0006 — edge-focused, variable-density fingerprinting + region tagging:** dense sampling
  (~0.5–1s) in the first/last N seconds, sparse (~5–15s) in the middle; bumpers tagged
  begin/end/middle; two-tier matching (fast edge path vs. heavier interstitial path);
  `(timestamp, value)` fingerprint data model.
- **Roadmap** ([`ROADMAP.md`](ROADMAP.md)) — phases; inherited-vs-net-new; two-tier note.
- **Matcher spec** ([`design/matcher-spec.md`](design/matcher-spec.md)) — the authoritative
  "definition of done" for matching (visual-primary, audio-accelerator, edge-focused, port the
  probe, standalone CLI). Overrides other docs on *how matching works*.
- **Design/research notes:** [`removal-pipeline.md`](design/removal-pipeline.md) (stream-copy vs
  re-encode, per-video enhancements, output options), [`prior-art.md`](research/prior-art.md),
  [`matching-approaches.md`](research/matching-approaches.md), [`ux-issues.md`](design/ux-issues.md)
  (VDF UX traps), [`glossary.md`](glossary.md) (fingerprints/embeddings/cosine).

### Matching validation — the risk-retirement spike (all in [`research/vdf-evaluation.md`](research/vdf-evaluation.md))

- Throughput baselines: default visual scan ~118 files/min (frame-sampled, not full-read); Deep
  Clean ~6 files/min → **GPU is idle** (VDF is CPU-only).
- **Audio matcher** validated on a single series; false-positive floor characterized (clean gap
  for long clips; collapses ≤5s at full-file).
- **Positional (edge) windows** rescue short *audible* clips — shrinking the offset space drops
  the FP floor.
- **Visual matcher** built and validated: `VisualTailProbe` self-embeds the clip + episode tails
  (auto-cuts the clip from a reference episode). **Daredevil ident stack: 12–13/13 at 98–99%**;
  **presence matcher** (ours) and VDF's rigid matcher both work; **FP floor ≤33%** on unrelated
  content (~65-pt gap, zero false positives).
- Hard rule learned: **the tool must extract clips itself — never trust a hand-cut clip.**
- Probes (temporary, in `VDF.IntegrationTests/Comparison/`): `VisualBumperMatchProbe`,
  `VisualTailProbe` — each file's header has its `dotnet test` recipe. `BumperMatchProbe`
  graduated (see below).

### VBR.\* tree scaffolded (2026-07-16, ADR 0005)

- `VBR.Core`, `VBR.CLI`, `VBR.Tests` created and wired into the solution (since renamed
  `VideoDuplicateFinder.sln` → `VideoBumperRemover.sln`, see below);
  `InternalsVisibleTo("VBR.Core")` glue added to `VDF.Core.csproj`.
- First real module: `VBR.Core/Matching/AudioBumperMatcher.cs` — productionized audio-fingerprint
  "video → catalog" matching (full-file + optional head/tail positional search windows).
- `BumperMatchProbe` deleted from `VDF.IntegrationTests`; its logic now lives in
  `AudioBumperMatcher`, and its env-var-gated real-media test graduated to
  `VBR.Tests/Matching/AudioBumperMatcherTests.cs`. `VisualBumperMatchProbe`/`VisualTailProbe`
  (visual/DINOv2 path) are untouched — not yet productionized.

### Clip-input contract fixed (2026-07-16)

`AudioBumperMatcher`/`vbr match` reworked per the "clip extraction is the tool's job" rule
(`AGENTS.md` core principles, `design/bumper-catalog.md` "Precision is the tool's job") — this
was flagged right after the initial scaffold and fixed before anything else was built on top:

- `AudioBumperMatcher.FindInLibrary` now takes `(sourceVideoPath, ClipRegion region, ...)` —
  `ClipRegion.Head(duration)` / `.Tail(duration)` / `.At(start, duration)` — and extracts the
  clip internally via ffmpeg stream-copy (same approach as `VisualTailProbe.ExtractTail`).
  There is no overload that accepts a pre-cut clip file.
- `vbr match` dropped `--clip <file>`; now `--source <video>` + exactly one of
  `--clip-head-seconds` / `--clip-tail-seconds` (validated — the command errors if zero or both
  are given). Renamed the unrelated per-file search-window options to `--search-head-seconds` /
  `--search-tail-seconds` to disambiguate from the new clip-extraction options.
- `AudioBumperMatcherTests` env vars renamed to match: `BUMPER_CLIP_EPISODE` (source video, same
  name `VisualTailProbe` already used for this), `BUMPER_CLIP_HEAD_SECONDS` /
  `BUMPER_CLIP_TAIL_SECONDS`, `BUMPER_SEARCH_HEAD_SECONDS` / `BUMPER_SEARCH_TAIL_SECONDS`.
- Rebuilt clean, `vbr match --help` and the validation-error path smoke-tested, `VBR.Tests`
  still skips cleanly without env vars set.

### Begin-region false-positive diagnosis + match CLI usability (2026-07-18)

- **Diagnosed the first begin-region test's false positives** (a 5s Netflix ident clip matching
  Doctor Who/Avatar episodes that don't contain it): the shared decode path decodes **keyframes
  only** and duplicates them onto the sampling grid, and the "skip black frames" guard is dead
  code — the clip collapsed to 3 distinct images (6×black + 7×blank-white + 1 distinctive card
  duplicates) and the six black duplicates matched dark lead-ins anywhere (low-information
  frames embed at cosine 0.87–0.97 against each other). **Ground-truth verified** after the
  maintainer challenged the first write-up with per-frame DaVinci exports
  (`test_materials/dd_netflix_bumper_davinci_export/`): full decode of the same 5s at the same
  0.2s grid matches the DaVinci reference frame-for-frame, keyframes decode pixel-correct (the
  defect is frame *selection* + duplication + missing filtering, not decode), and the failure is
  keyframe-cadence + content dependent on both sides — **not begin-specific** (the end-stack
  region keyframes every ~1.4–3s on bright cards, which is why end validation passed). Full
  analysis + fix plan: [`iterativeplan.md`](iterativeplan.md); findings-log entry:
  `research/vdf-evaluation.md` (2026-07-18); spec correction: `design/matcher-spec.md` §2.
  *(Correctness fixes §A + re-validation §C landed later the same day — see the checked-off item
  under Open / next steps below.)*
- **`vbr match` usability (iterativeplan §B, implemented):**
  - `--library` is now traversed **recursively by default**; `--no-recurse` restores
    top-level-only; results print **library-relative paths** so same-named files in different
    subfolders stay distinguishable.
  - `--output <file>` writes the match report (parameter header + the same rows/summary as the
    console); rows are built as a structured `MatchRow` record so a machine-readable format can
    follow cheaply.
  - `--dump-frames <dir>` (diagnostic) writes every sampled frame as a PNG (`clip/` + one
    numbered folder per candidate) via the new `VBR.Core.Diagnostics.FrameDump` — turns the next
    "why did this match?" into a glance instead of a pipeline reconstruction.
  - Verified: build clean; `VBR.Tests` still skips cleanly; live run on real media confirmed
    recursion, relative paths, report file, dump structure (14 clip frames = the black-frame
    diagnosis numbers), and `--no-recurse`.

### Removal command — designed and built (stream-copy) — ADR 0007 (2026-07-19)

- **Decided:** [`decisions/0007-removal-command.md`](decisions/0007-removal-command.md)
  specs a new `vbr remove` command. v1 bundles clip extraction + matching + removal in one
  invocation, reusing `match`'s parameter surface unchanged (catalog/index-aware variants are
  future, additive work). Key resolutions:
  - **Cut point is arithmetic, not per-file-detected** — `fileDuration − duration` (end) /
    `duration` (begin) — following a maintainer spot-check across ~70 videos (multiple studios/
    lengths, incl. personally-ripped DVD sources) finding bumper boundaries consistent to
    ~0.02s. `--clip-length` is reused as the removal length; no second parameter. This **retires
    the "boundary-growing / edge detection" per-file mechanism** previously described in
    `design/bumper-catalog.md` (now updated) and the old "Boundary detection" open item below
    (now updated to reflect this).
  - **Non-destructive, sibling-file output:** never touches the original; writes `name.vbr.ext`
    beside it. Supersedes the earlier "staging area" language in `ROADMAP.md` Phase 5. A future
    `cleanup` command (reserved name, not built) will handle promoting/replacing originals —
    that's where "verification before destruction" is actually enforced, not in `remove` itself.
  - **`--re-encode <true|false>`, default `true`:** re-encode (Mode B) is now the default,
    specifically because ffmpeg's stream-copy path realigns subtitle cues poorly (found via a
    prior Cowork investigation) — not primarily for frame accuracy, though that follows too.
    `--re-encode false` (Mode A/stream-copy) remains available as an explicit v1 exception. See
    `design/removal-pipeline.md`'s 2026-07-19 update.
  - Open (deferred, tracked in the ADR): manifest schema (a first concrete shape shipped — see
    below), re-encode algorithm/container/codec specifics, `cleanup` command design, a possible
    lightweight post-cut sanity check (verify one frame each side of the computed cut point via
    the existing presence matcher — proposed as a cheap alternative to full boundary detection,
    not required).
- **Built and verified against real media (2026-07-19), stream-copy only** (the maintainer's
  chosen build order — re-encode next): `VBR.Core.Removal.ClipRemover` (arithmetic cut, ffmpeg
  invocation, JSON manifest sidecar via a source-generated `JsonSerializerContext`),
  `VBR.CLI.Commands.RemoveCommand` (`vbr remove`, errors clearly if `--re-encode` resolves
  `true` since Mode B isn't built), and `VBR.CLI.Commands.SharedOptions` (factored out of
  `MatchCommand` so `match`/`remove` share one option surface instead of drifting copies).
  - **Two ffmpeg seek behaviors verified empirically before trusting them** (real Daredevil
    media): begin-region `-ss` placed *after* `-i` snaps FORWARD to the next keyframe (safe —
    never leaks bumper content backward); end-region `-t`/`-to` overshoots the requested duration
    by a small, roughly constant amount (~0.2s, independent of target) — so end cuts target a
    keyframe **≥1s before** the arithmetic cut point, never the nearest one. Both documented in
    `ClipRemover`'s doc comments and ADR 0007's new "Implementation findings" section.
  - **Live test caught a real precision gap the ADR had flagged as a risk, not a code bug:** a
    `--clip-length 10s` end-region test (10s was validated earlier this session for *matching*
    the Daredevil end-stack) left part of the ~20.5s real stack (`abc studios`/`MARVEL` cards) in
    the "cleaned" output — a length sufficient to match is not necessarily sufficient to remove.
    Corrected to 20.5s, the cut landed cleanly (present=70/70, output independently re-probed with
    ffprobe, source confirmed byte-for-byte/timestamp untouched). A begin-region test removed
    *exactly* 5.000s and landed precisely at a second, separate intro sequence (a Marvel
    comic-page animation) — correct: that's a distinct bumper, not part of the one measured.
  - 5 new unit tests (`VBR.Tests/Removal/ClipRemoverTests.cs`): pure tests for output-path
    naming and the reject-not-implemented/reject-non-positive-length guards, plus an env-var-gated
    real-media test (same skip-cleanly convention as `AudioBumperMatcherTests`) verifying output
    exists, source is untouched, and the actual cut duration is independently re-probed.
  - **Surgical sub-length cuts confirmed working (2026-07-19, maintainer-prompted re-check):**
    `--clip-length` isn't limited to "the whole bumper stack" — it removes exactly the length
    given, anchored to the true edge, regardless of what else sits further inside. Re-verified
    against the real Daredevil end-stack: `--clip-length 8s` removed only the Netflix card
    (which sits at EOF−8s, confirmed via frame dump) while leaving ABC Studios/Marvel/Goddard/
    DeKnight completely intact in the output. **Hard rule confirmed:** `--region begin|end`
    always clips to BOF/EOF respectively — there is no "whole stack" concept in the code at all,
    `ClipRemover` only ever does `fileDuration − length` / `length` arithmetic. Mid-video
    (non-edge-anchored) removal remains explicitly out of scope, deferred to later.
  - **Bug found and fixed (2026-07-19):** `match` and `remove` silently excluded `--clip-from`'s
    own file from the candidate list — so enrolling from S01E01 and scanning/cleaning its own
    season folder always skipped S01E01 itself, with zero indication in the output that this had
    happened. Removed the exclusion in both `MatchCommand.cs` and `RemoveCommand.cs` (kept the
    unrelated `.vbr.` self-exclusion in `remove`, which prevents re-processing a prior run's own
    output — that one's correct and stays). Verified live: S01E01 now reports
    `present=70/70  bestCos=100%` against its own season folder. Historical "N/12" figures
    recorded earlier in this doc and in `iterativeplan.md`/`vdf-evaluation.md` were computed
    under the old (excluding) behavior — not wrong for the code that produced them, but a future
    re-run against the same folder will now correctly show "N/13."
  - **Open risk logged (maintainer test, 2026-07-19), resolved by workflow (2026-07-20):** using
    an already-cut `.vbr.` file as `--clip-from`, with a mismatched `--clip-length`, against a
    library still holding both originals and `.vbr.` outputs (i.e. before `cleanup` runs) —
    silently removes the wrong amount from the original files' fresh `.vbr.` outputs (confirmed);
    does **not** produce a `.vbr.vbr.` filename collision (that part of the maintainer's prediction
    didn't hold — `remove`'s existing `.vbr.`-suffix candidate filter incidentally prevents it).
    Full mechanism: [ADR 0007](decisions/0007-removal-command.md) "Implementation findings."
    **No code guard added — resolved by running [`cleanup`](decisions/0008-cleanup-command.md)
    between bumper-removal passes** instead of stacking `remove` runs over a library that still
    holds prior `.vbr.` outputs; see ADR 0007's resolution note.

### Removal command — Mode B (re-encode) implemented (2026-07-20)

- **`--re-encode true` now works** — both removal modes are implemented and verified against
  real media. Built same-day per the maintainer's stated order (stream-copy first, re-encode
  second). Full detail: [ADR 0007](decisions/0007-removal-command.md) "Implementation findings —
  Mode B."
  - End-region: frame-accurate (28ms off a 20.5s cut, vs. Mode A's 1s+ safety-margin overshoot
    on the same content). Audio/subtitles stream-copied — verified safe (no seek involved).
  - Begin-region: also frame-accurate, **and correctly realigns subtitle cues** — the core
    reason ADR 0007 defaults to re-encode. Verified with a synthetic 5-cue SRT: cues at
    6s/15s/25s correctly became 1s/10s/20s after a 5s begin-region cut.
  - **A real, reproducible ffmpeg bug was found and fixed along the way:** `-ss` input-seeking
    combined with `-c:a copy` (audio stream-copy) silently produced output ~2s longer than
    requested, regardless of whether an explicit `-t` bound was also given — isolated by
    testing every copy/re-encode combination systematically, using output-seeking (sequential
    decode from the true start) as an independent ground truth since the buggy mechanism
    couldn't be used to verify itself. Fixed by re-encoding audio only on the begin-region
    (seeking) path; end-region (no seek) keeps audio stream-copy, verified safe.
  - Tests updated: the "not implemented" rejection test was replaced (parameterized existing
    validation tests across both modes instead); the real-media test now accepts an optional
    `BUMPER_REMOVE_MODE` env var.

### `--verbose` logging + `--file` single-target (2026-07-20)

- **VDF already has a logging system** (`VDF.Core.Utils.Logger`, public singleton, `Info`/`Warn`/
  `Error` + a `LogEntryAdded` event, writes to `log.txt` next to the running executable or the
  state folder). VDF.GUI already subscribes to it for its log panel; VDF.CLI never wired it up,
  and neither did any VBR.\* code before now — confirmed by grep, not assumed.
- **`--verbose` added to both `match` and `remove`** (`SharedOptions.SubscribeVerboseLogging`):
  when set, every `Logger` entry raised anywhere (VBR.Core or VDF.Core) is echoed live to
  stderr. Entries are written to `log.txt` **unconditionally** regardless of `--verbose` — VBR.Core
  logs without checking a flag; `--verbose` only gates whether the CLI *also* prints them live.
  Confirmed live: `log.txt` already contained VDF's own pre-existing Warning-level entries from
  earlier in this project, predating any of today's changes.
- **Debug/info statements added across the pipeline**, directly answering "is the AI model
  actually being used": `VisualBumperMatcher` logs the resolved ONNX model path and confirms
  session creation on first use, then per file logs sampled/usable/filtered frame counts and
  **each inference batch call** (frame count + quantized vector byte length — concrete, checkable
  numbers, not a trust-me claim). `ClipExtractor` and `ClipRemover` log the exact ffmpeg command
  line for every extraction/cut, plus (for `remove`) the computed cut point and, for stream-copy
  end cuts, the safety-margin rationale (arithmetic point → nearest safe keyframe → how much
  extra was trimmed and why). `AudioBumperMatcher` logs fingerprint block counts and the
  sliding-window comparison result. All verified live against real media (model path, batch
  inference, exact ffmpeg commands, and cut-point rationale all confirmed in actual console/
  `log.txt` output) — re-run any command with `--verbose` to see it yourself.
- **`--file <path>` added to both commands as an alternative to `--library <folder>`** — exactly
  one of the two is required, validated with a clear error either way
  (`SharedOptions.ResolveCandidates`, also used to unify the previously-duplicated
  candidate-enumeration logic in `match`/`remove`). Display names fall back to just the file name
  (no library root to be relative to). Verified live for both commands.
- **Forward-looking note for the future `cleanup` command** (not built yet): the maintainer
  wants single-file targeting there too, in addition to a library. `ResolveCandidates`/
  `CandidateSet` in `SharedOptions.cs` are written generically enough to be reused as-is when
  `cleanup` is designed — no rework anticipated.

### `cleanup` command designed and implemented — ADR 0008 (2026-07-20)

- **[ADR 0008](decisions/0008-cleanup-command.md) written and proposed — not yet implemented.**
  The maintainer asked for a design review before any code is written. Covers: filename-derived
  original↔output pairing (the manifest was judged undependable as a side-channel — it can be
  separated from, or deleted independently of, the video it describes); pairwise per-file
  processing (mark original → promote output → delete old original, fully resolved before the
  next file, replacing an earlier phase-batched draft); rollback narrowed to just the mark/promote
  swap (a failed final delete is treated as a retryable housekeeping issue, not something that
  unwinds an already-correct promotion); an unconditional per-directory startup sweep that
  self-heals leftovers from a previous crashed/killed run; opt-in `--validate-files` (ffprobe/
  duration check, off by default — the CLI can't enforce that a human reviewed the output, so this
  is an assist, not a gate); and **no secondary trash/soft-delete stage** — `remove`'s non-destructive
  sibling output already serves as the recycle bin, so `cleanup` commits directly once run.
- **This closes the "already-cut `.vbr.` as `--clip-from`" risk** logged above and in ADR 0007
  (2026-07-19): resolved by workflow, not a code guard — run `cleanup` between bumper-removal
  passes rather than stacking `remove` runs over a library that still holds prior `.vbr.` outputs.
  See ADR 0007's "Implementation findings" section for the resolution note.
- **Three open items the maintainer flagged for a final call, decided (2026-07-20):** the deletion
  marker suffix (`.vbrdelete`, appended after the full original filename) is final; `--dry-run` is
  deferred — no cost-savings case for building it now, since Decision 7's per-file steps are
  already plan-then-act, so gating the mutating calls behind a flag later is the same small change
  whenever it happens; `--file` touches only its own target file's pairing/marker state, never the
  rest of its directory (different from how `--file` works for `match`/`remove`, since `cleanup`'s
  per-directory design made that shape impossible to reuse as-is — see ADR 0008 Decision 10).
- **Implemented same day.** `VBR.Core.Cleanup.LibraryCleaner` (core logic) +
  `VBR.CLI.Commands.CleanupCommand` (CLI wiring, registered in `Program.cs`) + `--validate-files`
  added to `SharedOptions`. Writing tests first caught a real bug before it shipped: the naive
  filename-inverse function would have mistaken a manifest (`name.vbr.json`) for a video output
  and tried to pair it with a bogus `name.json` "original" — fixed by requiring the recovered
  original to end in a recognized video extension (`ClipExtractor.VideoExtensions`), with a test
  (`OriginalPathFor_ReturnsNullForNonVbrPaths`) covering exactly that case so it can't regress.
  18 tests in `VBR.Tests/Cleanup/LibraryCleanerTests.cs`, almost all of them ordinary always-on
  unit tests against temp directories (no video content needed for mark/promote/delete/recovery —
  a real Windows file lock, not a mock, is used to force and verify the rollback path); only the
  two `--validate-files` tests shell out to ffmpeg/ffprobe. Also verified live through the compiled
  CLI: a scratch library with a normal pair, an untouched file with no `.vbr.` counterpart, and a
  simulated interrupted-run marker — the recovery sweep restored the interrupted pair and the same
  run cleaned it alongside the normal one, exactly as designed. Full solution builds with 0
  warnings/errors; `dotnet test VBR.Tests` is 30 passed / 2 skipped (env-gated) / 0 failed.

### Orphaned ffmpeg process on cancellation, fixed; a "hang" traced to a corrupted test file (2026-07-20)

- **Live bug (maintainer): `vbr remove --re-encode true` appeared to hang** on a real episode —
  0-byte output, CPU maxed, 12+ minutes, and killing the CLI (Ctrl+C) left ffmpeg running as an
  orphaned process afterward. Root cause of the orphan: `RunFfmpeg` blocked in a synchronous,
  cancellation-blind `Process.StandardError.ReadToEnd()` — the existing
  `ct.IsCancellationRequested`-then-kill check right after it could never be reached while that
  call was still blocked. **Fixed** in both `ClipRemover.RunFfmpeg` and `ClipExtractor.Extract`
  (the latter gained a `CancellationToken` parameter, threaded through its three call sites) by
  registering a `CancellationTokenRegistration` immediately after `Process.Start()` that kills the
  process tree independent of what the calling thread is currently blocked on. While in both
  methods, also switched `ReadToEnd()` on stdout/stderr from sequential to concurrent
  (`ReadToEndAsync` on both before waiting) — draining one stream fully before the other risks
  deadlocking ffmpeg against this process if the undrained stream's pipe buffer fills, the same
  bug class independently hit (and fixed) the same day in `VBR.Tests`'s synthetic-clip helper.
- **Not a bug: the source file was corrupted.** After the fix above, a retry was still far slower
  than expected with an implausible frame count. `ffmpeg -v warning -i <source> -map 0:v:0 -f
  null -` (decode-only, writes nothing) showed continuous "non monotonically increasing dts"
  warnings — a corrupted-stream signature — and the file also wouldn't play in MPC-BE. The
  maintainer swapped in a known-good copy from their real library; it played and re-encoded
  normally. Notable: the earlier steps in the same run (short stream-copied tail extraction, full
  audio fingerprinting) had all completed cleanly — only a full video decode start-to-cut-point
  (what re-encoding requires) touched the damaged part of the stream. No code change from this;
  see ADR 0007's "Open questions" for a possible future source-side validation idea.
- **Manifest renamed:** `name.vbr.json` → `name.json`, derived from the original path rather than
  the `.vbr.` output (maintainer preference). Sidesteps the manifest-mistaken-for-output bug class
  entirely (the defensive video-extension check added when building `cleanup` stays anyway, as
  extra robustness — see ADR 0008 Decision 3). `ClipRemover.Remove`, `LibraryCleaner`, and both
  test suites updated; live-verified against real media through the actual env-gated
  `ClipRemoverTests` case (manifest correctly written as `....json`).
- Full solution: 0 warnings/errors. `dotnet test VBR.Tests`: 30 passed / 2 skipped / 0 failed
  (re-verified after all of the above). See [ADR 0007](decisions/0007-removal-command.md) and
  [ADR 0008](decisions/0008-cleanup-command.md) "Implementation findings" for full detail.

## Open / next steps

- [ ] **Removal engine — both modes implemented; algorithm specifics still open.** See
  [ADR 0007](decisions/0007-removal-command.md): `vbr remove`, arithmetic cut point (no per-file
  boundary detection), non-destructive `.vbr.` sibling output, both stream-copy and re-encode
  verified against real media. Still open (ADR 0007 "Open questions"): real codec choice
  (currently a fixed libx264/AAC placeholder), GPU (NVENC) encode (currently CPU-only and slow
  for full episodes), manifest schema finalization, 10-bit/HDR preservation. The "already-cut
  `.vbr.` as `--clip-from`" risk is resolved (workflow, not code — see above); `cleanup` command
  design is no longer open, it's proposed in [ADR 0008](decisions/0008-cleanup-command.md) and
  awaiting the maintainer's review before implementation.
- [ ] ~~Boundary detection. Turn a match offset (~0.2–0.5s resolution) into a precise cut point
  — find the content→junk transition...~~ **Superseded (2026-07-19) — see ADR 0007.** Per-file
  content→junk detection turned out to be unnecessary: bumper duration is empirically constant
  (~0.02s across a 70-video spot-check), so the cut point is computed arithmetically from a
  duration measured once, not searched per file. Precision now lives entirely in *clip
  selection* (a UI/UX problem — a scrubber that assists frame-accurate boundary picking),
  deferred like the rest of the UI.
- [ ] **Sub-bumper extent.** Given a matched region, determine its true extent (grow boundaries
  until frames stop agreeing across all containing files); distinguish "whole stack" from "a piece."
- [ ] **GPU acceleration.**
  - [x] ~~Measure `hwaccel=cuda` decode speedup vs. CPU.~~ **Correction (2026-07-16): Deep Clean
    is multi-phase and this only measured phase 1.** ~38 files/min / ~6.3× holds for phase 1
    only (RTX 3080) — phase 2 ("sampling keyframes," est. 1 day 1 hour, likely CPU-only ONNX
    inference) then began with no warning. See `research/vdf-evaluation.md` Measurement 3's
    correction. **Not validated as a whole-scan number; re-test needed once phases are
    understood.** Also logged as a UX issue (`design/ux-issues.md`): the UI hides multi-phase
    structure and only shows time-remaining for the current phase.
  - [ ] Measure phase 2 ("sampling keyframes") throughput specifically — likely the real
    bottleneck, which would promote GPU ONNX inference (below) over decode.
  - [ ] Measure "Use native Ffmpeg binding" as a separate, possibly bigger decode-side lever.
  - [ ] Implement GPU decode (NVDEC) + ONNX CUDA execution provider. Code touch-points in
    `research/vdf-evaluation.md`.
- [x] **Fix the visual matcher's black-frame / keyframe-only-decode defect** (begin-region false
  positives, found 2026-07-18) — **done same day** per [`iterativeplan.md`](iterativeplan.md):
  §A1 `FrameQuality` low-information filter (VDF's dark/duplicate guards + calibrated
  near-uniform rejection, both sides, loud `PrepareClip` failure on an all-black clip) + §A2
  `DenseFrameSampler` full decode of the short edge windows + clip-embed caching per run. §C
  re-validation matrix **passed clean**: begin TP 12/12 @ 99–100% (present=18/18) vs FP 0/13 DW
  + 0/20 Avatar (bestCos ≤56%); end-stack regression 12/12 @ 99–100% vs 0/20 Avatar (≤71% —
  floor legitimately higher than the old ≤33% keyframe-only baseline; see the 2026-07-18 "FIX
  VALIDATED" entry in `research/vdf-evaluation.md`). Presence rule and all default thresholds
  unchanged; 5 new `FrameQuality` unit tests.
- [ ] **Productionize matching (leave probes behind).** Build real modules per ADR 0005 and
  **[`design/matcher-spec.md`](design/matcher-spec.md)** — the authoritative "definition of done."
  Read the spec first: the PRIMARY matcher is the visual DINOv2 presence path, audio is a secondary
  accelerator, and the whole thing is recreated as a standalone, expandable `vbr` CLI. (The first
  productionization pass went audio-only/no-AI/no-edge-sampling by reading the research order as a
  build order — the spec exists to prevent that.)
  - [x] **Recreate the validated pipeline as a standalone `vbr` CLI — built AND validated
    against the probe (2026-07-17).** Ported `VisualTailProbe`'s DINOv2 presence pipeline into
    `VBR.Core.Matching.VisualBumperMatcher`; both matchers implement `IBumperMatcher`
    (`VBR.Core.Matching.IBumperMatcher`); shared extraction in `VBR.Core.Extraction.ClipExtractor`
    (folds in what used to be `AudioBumperMatcher`'s private copy + `VisualTailProbe.ExtractTail`);
    `vbr match` runs visual by default, `--detection-mode visual|audio|both`.
    - **Parity confirmed, exact match to VisualTailProbe:** `vbr match --detection-mode visual`
      on the Daredevil end-stack (`--clip-from` S01E01, `--region end --clip-length 10s
      --sample-interval 0.2s`) — **12/12 @ 98–99%**, identical per-episode bestCos/present/rigid
      to the probe's own output. Avatar (unrelated, 21 files incl. a stray `introclip.mkv`) —
      **0/21 @ ≤33%**, matching the documented FP floor exactly (down to the stray clip's
      recorded 21%). `--detection-mode visual` never invokes the audio matcher at all (separate
      `IBumperMatcher` implementations), confirming visual doesn't depend on audio; `both` mode
      showed audio also corroborating at 82–98% (this particular bumper is "mixed," not purely
      silent, per the original probe notes — correct behavior, not a bug). Edge-only sampling is
      structural: `VisualBumperMatcher.Match` always extracts via `ClipExtractor` before
      embedding, so the full-length episode is never opened for dense decode.
    - **2026-07-18 caveat:** parity to the probe stands, but the *validated pipeline itself* was
      later found defective for begin-region / dark bumpers (black-frame false positives —
      keyframe-only decode + a dead black-frame guard). See "Begin-region false-positive
      diagnosis" above and [`iterativeplan.md`](iterativeplan.md).
    - **Correction found during validation:** the documented "~4.8s" realized clip length comes
      from requesting `--clip-length 10s` (not 5s as first assumed) — stream-copy keyframe
      rounding is what shortens it. A 5s request landed entirely in trailing black padding after
      the last ident faded and produced zero usable frames. Diagnosed by first reproducing
      against a known-good static clip (`daredevil-end20.mkv`) to isolate extraction-window
      choice from a pipeline bug.
    - `VisualTailProbe` is still in place (not graduated/deleted yet — that's a separate step
      to decide now that parity is confirmed).
    - **CLI surface finalized (2026-07-17) after a design review — see `matcher-spec.md` § 3.2
      for the authoritative flag list.** Key outcomes of that discussion, in case anyone wonders
      why the shape differs from the first draft:
      - One `--region begin|end` flag drives *both* clip extraction and candidate search (a
        bumper lives at one edge; separate per-edge flag pairs invited nonsensical combinations
        like `--clip-tail-seconds` + `--search-head-seconds`). Multi-region bumpers are two
        invocations, not one command doing both.
      - `--clip-length` (required) and `--search-length` (optional, **defaults to
        `--clip-length` + 20s**, not a flat constant) are separate — the search window needs
        slack beyond the clip's own length, and tying the default to clip length avoids an
        under-sized-window foot-gun.
      - `--sample-interval` (renamed from an earlier "density" idea — interval is the accurate
        term) **must support ~0.2s with no artificial floor** — validated hard requirement, not
        tuning (see the GPU/matching findings in `vdf-evaluation.md`, 2026-07-17 entry).
      - `--source`/`--signal` renamed to `--clip-from`/`--detection-mode` (clearer, less jargon).
  - [x] Audio-fingerprint video→catalog **accelerator** — `VBR.Core.Matching.AudioBumperMatcher` +
    `vbr match` CLI command. No caching yet (re-fingerprints every run). *(This is the accelerator,
    not the primary matcher — see the spec.)*
  - [x] **Clip-input contract.** `AudioBumperMatcher`/`vbr match` take a source video + time
    range and extract the clip internally — see "Clip-input contract fixed" above. Still applies
    as a standing rule to every *future* entry point (catalog enroll, UI marking, etc.).
  - [ ] Edge-focused scan + a **cached** fingerprint/embedding index (scan once, compare cheaply).
  - [ ] **Catalog** — enroll a bumper once, apply forever; personal export/import.
  - [ ] **Removal engine** — trim (mode A stream-copy vs. mode B re-encode) + manifest + verify;
    never mutate originals until confirmed.
  - [ ] **Verification UI** (Avalonia) — preview/confirm cuts; fix the VDF UX traps in `ux-issues.md`.
    UI project structure itself is a reopened question — see ADR 0005 Open questions.
- [ ] **Two-tier design.** Fast optimized **edge** path (common case) vs. heavier **mid-video
  interstitial** path (on demand).
- [ ] **Stretch (Phase 8):** per-video enhancements (aspect fix, letterbox/text crop, deinterlace,
  flip, logo removal, frame interpolation, chapter marks) + focused output/transcode options
  (container remux, codec, quality) — all composing into the re-encode pass.
- [ ] **Housekeeping:** verify VDF's exact license text is present; decide which VDF projects to
  drop (`VDF.Web`/`VDF.CLI`/`VDF.Benchmarks`); measure SMB throughput; confirm ffmpeg NVDEC/NVENC.
- [ ] **Naming clarity:** rename `referenceClip`/`referenceClipPath` to `bumperClip`/
  `bumperClipPath` throughout — `VisualBumperMatcher.cs`, `AudioBumperMatcher.cs`,
  `IBumperMatcher.cs`, `MatchCommand.cs`, `RemoveCommand.cs`, `AudioBumperMatcherTests.cs`, and
  `docs/design/matcher-spec.md`. "Reference" undersells that this specifically is the bumper
  clip being matched against, not a generic reference input.

## Cross-cutting reminders (from AGENTS.md)

- Never modify source videos in place; verification before destruction.
- Subtitles are first-class — preserve tracks through remux/re-encode; convert formats on
  container change; verify in post-cut review.
- Mixed resolution + letterboxed/burned-in text must be normalized for matching.
- License header on every source file *we* create; dual copyright on modified VDF files.
