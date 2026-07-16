# Agent instructions

Instructions for automated agents (Claude, etc.) working in this repository.

## Project

Video Bumper Remover — detect, verify, and remove repeated bumpers/interstitials across a
large personal video library. Read [`README.md`](README.md) for the full problem statement
and [`docs/ROADMAP.md`](docs/ROADMAP.md) for the plan.

## Current status — read this first (2026-07-16)

Past the risk-retirement spike; about to begin real product build. What's established:

- **The matching approach is validated on real bumpers.** Full story:
  [`docs/research/vdf-evaluation.md`](docs/research/vdf-evaluation.md). In short:
  - **Audio fingerprint** (VDF Chromaprint) matches audible bumpers with a clean gap; **dead**
    for silent/varying-audio bumpers.
  - **Positional (edge) windows** rescue short *audible* bumpers — searching only the first/last
    N seconds shrinks the offset space and drops the false-positive floor.
  - **Visual DINOv2 matching** — VDF's rigid ≥4-hit matcher *and* our new **presence matcher** —
    detect silent/short bumpers (Daredevil ident stack) at **98–99%** vs. **≤33%** for unrelated
    content. Zero false positives, ~65-pt gap.
  - **Hard-won rule:** the tool must extract clips itself — *never trust a hand-cut clip* (most
    "failures" this spike were corrupt/mis-cut input, not the matcher).
- **The `VBR.*` tree is scaffolded** (2026-07-16), per
  [ADR 0005](docs/decisions/0005-code-organization.md): `VBR.Core` (references `VDF.Core` via
  the `InternalsVisibleTo("VBR.Core")` glue in `VDF.Core.csproj`), `VBR.CLI` (`vbr match`
  command), `VBR.Tests`. First real module: `VBR.Core/Matching/AudioBumperMatcher.cs` —
  productionized audio-fingerprint "video → catalog" matching (full + head/tail windows),
  graduated from `BumperMatchProbe` (now deleted from `VDF.IntegrationTests`; its test lives on
  as `VBR.Tests/Matching/AudioBumperMatcherTests.cs`, same env-var-gated real-media workflow).
  Extracts the clip itself — no pre-cut-clip input, per the rule below. Try it:
  `dotnet run --project VBR.CLI -- match --source <episode> --clip-tail-seconds 40 --library <folder>`.
- **Remaining diagnostic probes** live temporarily in `VDF.IntegrationTests/Comparison/`:
  `VisualBumperMatchProbe` (reads cached embeddings), `VisualTailProbe` (self-embeds clip +
  episode tails; can auto-cut the clip from a reference episode via `BUMPER_CLIP_EPISODE`) — the
  visual/DINOv2 matching path, not yet productionized. **Each probe file's top comment has its
  exact `dotnet test` recipe + env vars.** Test media is under `test_materials/` (gitignored).

### Open threads / next steps — canonical checklist: [`docs/PROGRESS.md`](docs/PROGRESS.md)

(Summary below; the maintainable checklist + full completed-work log lives in `docs/PROGRESS.md`.)

- **Code organization — decided (Core/CLI); GUI reopened.** `VBR.*` project layout vs. VDF
  resolved: [`docs/decisions/0005-code-organization.md`](docs/decisions/0005-code-organization.md).
  `VBR.Core`/`VBR.CLI`/`VBR.Tests` scaffolded; decision 6 (extend `VDF.GUI` in place vs. a
  separate `VBR.Gui`) is reopened — see the ADR's Open questions before scaffolding any UI.
- **Boundary detection.** Turn a match offset (~0.2–0.5s resolution) into a precise cut point
  (content→junk transition → file edge; edge bumpers cut to BOF/EOF so padding is auto-removed).
- **GPU acceleration — Deep Clean turned out to be multi-phase; re-measure needed.** The
  `hwaccel=cuda` ~6.3× number only covers phase 1 — a second phase ("sampling keyframes," ~1
  day+ estimated, likely CPU-only ONNX inference) followed with no warning, so total-scan
  throughput is still unknown. See `docs/research/vdf-evaluation.md` Measurement 3's
  2026-07-16 correction. Also a UX bug to fix in our own UI (`docs/design/ux-issues.md`): make
  multi-phase scans and a whole-job time estimate visible. Code touch-points for GPU work in
  `docs/research/vdf-evaluation.md`.
- **Productionize matching.** Audio-fingerprint video→catalog matching now lives in
  `VBR.Core`/`VBR.CLI` (see above). Still to build: a cached fingerprint/embedding index (avoid
  re-fingerprinting unchanged files); the visual/DINOv2 matcher's equivalent productionization;
  the **catalog** (enroll a bumper once, apply forever); the **removal engine** (cut + manifest +
  verify); the **UI**.
- **Two-tier design.** Fast optimized **edge** path (common case) vs. heavier **mid-video
  interstitial** path (on demand).

## Documentation map

- [`docs/PROGRESS.md`](docs/PROGRESS.md) — **the task log & running checklist** (completed work +
  open next steps). Start here to see what's done and what's next.
- [`README.md`](README.md) — problem, goal, fork attribution.
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — phased plan; inherited-vs-net-new; cross-cutting concerns.
- [`docs/research/vdf-evaluation.md`](docs/research/vdf-evaluation.md) — **the hands-on findings
  log** (throughput + all matcher validation results). The most important doc for context.
- [`docs/research/prior-art.md`](docs/research/prior-art.md),
  [`docs/research/matching-approaches.md`](docs/research/matching-approaches.md) — background.
- [`docs/design/bumper-catalog.md`](docs/design/bumper-catalog.md) — catalog data model + workflows.
- [`docs/design/removal-pipeline.md`](docs/design/removal-pipeline.md) — trim modes (stream-copy
  vs. re-encode) + per-video enhancements + output options.
- [`docs/design/code-organization.md`](docs/design/code-organization.md) — VBR-vs-VDF structure
  (option analysis; decided in
  [`docs/decisions/0005-code-organization.md`](docs/decisions/0005-code-organization.md)).
- [`docs/design/ux-issues.md`](docs/design/ux-issues.md) — VDF UX traps to fix in our redesign.
- [`docs/glossary.md`](docs/glossary.md) — fingerprinting / embeddings / cosine, plain-language.
- [`docs/decisions/`](docs/decisions/) — ADRs: 0001 stack (superseded by 0002), 0002 stack
  (accepted), 0003 repo structure (fork VDF), 0004 bumper catalog, 0005 code organization.
- [`docs/development.md`](docs/development.md) — build/run + VS Code setup (use base C# extension,
  skip C# Dev Kit).

## Core principles

- **Stack (decided):** C#/.NET, built as a **fork of Video Duplicate Finder** (reuse
  `VDF.Core` engine), **Avalonia** UI, SQLite index, ML (if added) via in-process **ONNX
  Runtime** on the desktop GPU. Desktop app reaching media over SMB. Full rationale and the
  throughput architecture (audio-first + sparse sampling) are in
  [`docs/decisions/0002-tech-stack.md`](docs/decisions/0002-tech-stack.md).
- **Build/run:** .NET 10 SDK + VS Code (full Visual Studio not required); build with
  `dotnet build VideoDuplicateFinder.sln`. Setup and the Native-AOT-publish caveat are in
  [`docs/development.md`](docs/development.md).
- **Python is disfavored.** The maintainer will almost never choose Python. Propose
  alternatives and only use Python if explicitly approved for a specific task.
- **Check VDF's license before forking/redistributing** and keep this project compliant.
- **Never modify source videos in place.** All trimming operations write to new files or a
  staging area until the maintainer confirms. Original media is irreplaceable.
- **Verification before destruction.** Any removal step must be previewable and reversible
  until confirmed. Guard explicitly against the **sub-bumper** problem (a short match that
  is really part of a longer bumper) and against mid-video **interstitials** being treated
  as edge bumpers.
- **Clip extraction is the tool's job — the user NEVER supplies a pre-cut clip.** Enrollment and
  matching take a **source video + a rough region** (a reference video plus e.g. "last N seconds",
  or an in-UI marked range); our code extracts *and* fingerprints the clip itself. Every API/CLI/UI
  entry point must accept `(video path, time range)` — **not** a pre-cut clip file. Rationale:
  hand-cut clips were the single biggest source of false failures during the spike (mis-cuts,
  corruption), and no user can be that precise; precision is *our* responsibility. See
  [`docs/design/bumper-catalog.md`](docs/design/bumper-catalog.md) → "Precision is the tool's job."
  **Fixed (2026-07-16):** `vbr match` now takes `--source <video>` + exactly one of
  `--clip-head-seconds`/`--clip-tail-seconds` and extracts the clip internally (`VBR.Core`'s
  `AudioBumperMatcher.FindInLibrary` takes `(sourceVideoPath, ClipRegion, ...)`); there is no
  `--clip <file>` option.

## Decisions

- Record every significant technical decision as an ADR in `docs/decisions/`
  (`NNNN-short-title.md`). Keep the status field current: `proposed`, `accepted`,
  `superseded`.
- When a decision is still open, capture the options and trade-offs rather than picking one
  silently.

## Working with git (IMPORTANT)

**The maintainer owns all git operations. Agents must not run git.**

- Agents may freely read, create, and edit files in the working tree. Agents must **not**
  run any `git` command that changes repository state (`init`, `add`, `commit`, `branch`,
  `merge`, `rebase`, `reset`, `tag`, `push`, `pull`, etc.).
- When a change is ready to be committed, **pause and hand the maintainer** the exact git
  commands to run (with a suggested commit message). Do not commit on the maintainer's
  behalf.
- Read-only inspection (`git status`, `git log`, `git diff`) is fine if useful, but prefer
  to just describe what changed.
- **Why / environment note:** this rule originated in **Cowork**, where the repo was exposed to
  the agent sandbox through a FUSE bridge that corrupted git's lockfile/atomic-rename operations
  (native Windows git was unaffected). In a **local IDE (VS Code / Claude Code), git works
  normally**, so the hard technical reason no longer applies — but the maintainer drove git
  throughout the spike and this remains the **default**. Confirm the maintainer's current
  preference; until told otherwise, edit files and hand over commit commands rather than running
  git.

## Repo conventions

- Documentation lives in `docs/`. Research/exploration notes go in `docs/research/`.
- Keep `CLAUDE.md` as a one-line pointer to this file; put actual instructions here.
- Prefer small, focused commits with clear messages (made by the maintainer — see above).
- **License header (required on every source file *we* create).** Put the AGPLv3 header block at
  the very top of each new source file (`.cs`, etc.). Do **not** add it to upstream VDF files
  (they keep their `0x90d` header) or to docs/config. The block:

  ```
  // /*
  //     Copyright (C) 2026 mawcs
  //     This file is part of VideoBumperRemover
  //     VideoBumperRemover is free software: you can redistribute it and/or modify
  //     it under the terms of the GNU Affero General Public License as published by
  //     the Free Software Foundation, either version 3 of the License, or
  //     (at your option) any later version.
  //     VideoBumperRemover is distributed in the hope that it will be useful,
  //     but WITHOUT ANY WARRANTY without even the implied warranty of
  //     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  //     GNU Affero General Public License for more details.
  //     You should have received a copy of the GNU Affero General Public License
  //     along with VideoBumperRemover.  If not, see <http://www.gnu.org/licenses/>.
  // */
  //
  ```

## Safety around media & FFmpeg

- Prefer stream-copy trims (`-c copy`) where frame-accuracy allows; note when re-encoding
  is required and why.
- Always keep a manifest of what was cut from each file (source, snippet start/end, method)
  so removals can be audited and undone.
