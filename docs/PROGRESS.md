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

## Open / next steps

- [ ] **Boundary detection.** Turn a match offset (~0.2–0.5s resolution) into a precise cut point
  — find the content→junk transition; for edge bumpers cut to BOF/EOF so black/silence padding is
  removed by inclusion. (Refine offset toward frame accuracy.)
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
- [ ] **Productionize matching (leave probes behind).** Build real modules per ADR 0005 and
  **[`design/matcher-spec.md`](design/matcher-spec.md)** — the authoritative "definition of done."
  Read the spec first: the PRIMARY matcher is the visual DINOv2 presence path, audio is a secondary
  accelerator, and the whole thing is recreated as a standalone, expandable `vbr` CLI. (The first
  productionization pass went audio-only/no-AI/no-edge-sampling by reading the research order as a
  build order — the spec exists to prevent that.)
  - [x] **Recreate the validated pipeline as a standalone `vbr` CLI — built (2026-07-17), not
    yet re-validated against the probe.** Ported `VisualTailProbe`'s DINOv2 presence pipeline into
    `VBR.Core.Matching.VisualBumperMatcher`; both matchers implement `IBumperMatcher`
    (`VBR.Core.Matching.IBumperMatcher`); shared extraction in `VBR.Core.Extraction.ClipExtractor`
    (folds in what used to be `AudioBumperMatcher`'s private copy + `VisualTailProbe.ExtractTail`);
    `vbr match` runs visual by default, `--detection-mode visual|audio|both`. Full solution builds
    clean; `--help`/error-path smoke-tested; `VBR.Tests` still skips cleanly with no env vars.
    **`VisualTailProbe` stays in place untouched — not deleting/graduating it until the CLI's
    output is confirmed against it on real media** (Daredevil end-stack + Avatar unrelated-content
    set — see the spec's "Definition of done" acceptance numbers). That validation run is the
    next step, blocked on the maintainer supplying the clip/episode paths and AI model folder.
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
