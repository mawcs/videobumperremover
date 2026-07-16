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
- Verified the fork builds/runs (`dotnet build VideoDuplicateFinder.sln`).
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
- **Roadmap** ([`ROADMAP.md`](ROADMAP.md)) — phases; inherited-vs-net-new; two-tier note.
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
- Probes (temporary, in `VDF.IntegrationTests/Comparison/`): `BumperMatchProbe`,
  `VisualBumperMatchProbe`, `VisualTailProbe` — each file's header has its `dotnet test` recipe.

## Open / next steps

- [ ] **Boundary detection.** Turn a match offset (~0.2–0.5s resolution) into a precise cut point
  — find the content→junk transition; for edge bumpers cut to BOF/EOF so black/silence padding is
  removed by inclusion. (Refine offset toward frame accuracy.)
- [ ] **Sub-bumper extent.** Given a matched region, determine its true extent (grow boundaries
  until frames stop agreeing across all containing files); distinguish "whole stack" from "a piece."
- [ ] **GPU acceleration.**
  - [x] Measure `hwaccel=cuda` decode speedup vs. CPU. **~38 files/min avg (stable across 4
    checkpoints to 1,000/2,110 files) vs. ~6 files/min CPU (~6.3×, conservative — ran under an
    unrelated background NAS scrub), RTX 3080, same 2,110-file subset** — see
    `research/vdf-evaluation.md` Measurement 3. Considered validated; no re-test planned. Still
    open: measure "Use native Ffmpeg binding" as a separate, possibly bigger lever.
  - [ ] Implement GPU decode (NVDEC) + ONNX CUDA execution provider. Code touch-points in
    `research/vdf-evaluation.md`.
- [ ] **Productionize matching (leave probes behind).** Build real modules per ADR 0005:
  - [ ] Edge-focused scan + a **cached** fingerprint/embedding index (scan once, compare cheaply).
  - [ ] **Catalog** — enroll a bumper once, apply forever; personal export/import.
  - [ ] **Removal engine** — trim (mode A stream-copy vs. mode B re-encode) + manifest + verify;
    never mutate originals until confirmed.
  - [ ] **Verification UI** (Avalonia) — preview/confirm cuts; fix the VDF UX traps in `ux-issues.md`.
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
