# Project progress & task log

Durable export of the working task list from the initial Cowork build session (the ephemeral
task list doesn't transfer between tools). Kept as the running record — check items off and add
new ones as we go. For the *why* behind any of this, see the docs referenced inline and the
findings log in [`research/vdf-evaluation.md`](research/vdf-evaluation.md).

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

## Open / next steps

- [ ] **Removal engine — designed (ADR 0007) and built for stream-copy; re-encode next.** See
  [ADR 0007](decisions/0007-removal-command.md) and the entry above: `vbr remove`, arithmetic
  cut point (no per-file boundary detection), non-destructive `.vbr.` sibling output, verified
  against real media. Next: implement Mode B (re-encode), including proper subtitle cue
  realignment — the reason `--re-encode` defaults to `true` even though only `false` runs today.
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

## Cross-cutting reminders (from AGENTS.md)

- Never modify source videos in place; verification before destruction.
- Subtitles are first-class — preserve tracks through remux/re-encode; convert formats on
  container change; verify in post-cut review.
- Mixed resolution + letterboxed/burned-in text must be normalized for matching.
- License header on every source file *we* create; dual copyright on modified VDF files.
