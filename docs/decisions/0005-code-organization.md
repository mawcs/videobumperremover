# ADR 0005: Code organization — `VBR.*` tree alongside VDF's projects

- **Status:** accepted
- **Date:** 2026-07-16
- **Related:** [`../design/code-organization.md`](../design/code-organization.md) (working doc
  with the full option analysis), [0002](0002-tech-stack.md) (fork VDF),
  [0003](0003-repository-structure.md) (repo structure)

## Context

We forked Video Duplicate Finder (VDF) and have been adding diagnostic probes inside VDF's own
test project (`VDF.IntegrationTests`) for expediency during the risk-retirement spike. Before
starting real product build, we need a durable answer to four questions: how *our* code relates
to VDF's project layout; how deeply we bridge VDF's `internal` primitives; how we handle
removing/diverging from VDF while staying mergeable; and how copyright attribution works on
files we modify. Full option analysis and trade-offs live in the working doc; this ADR records
the converged decision.

## Decision

### 1. A separate `VBR.*` tree, referencing `VDF.Core` as a dependency

New product code does **not** live inside VDF's projects. We add our own subprojects:

- **`VBR.Core`** — everything that's ours: edge-scan, matchers (presence/rigid wrappers +
  thresholds), the bumper catalog, boundary detection, the removal engine. References
  `VDF.Core`.
- **`VBR.Tests`** — our tests; the diagnostic probes graduate here from
  `VDF.IntegrationTests` (`BumperMatchProbe`, `VisualBumperMatchProbe`, `VisualTailProbe`).
- **UI stays `VDF.GUI`** — see decision 6 below; there is no `VBR.Gui` for now.

**Namespace/assembly naming: `VBR.*`** (not `VideoBumperRemover.*`). This matches VDF's own
precedent exactly — its assemblies use the abbreviated namespace matching the project name
(`VDF.Core`, not `VideoDuplicateFinder.Core`), not the full product name. `VBR.Core`,
`VBR.Tests`, etc. stay consistent with the code they sit beside. The AGPL license header still
spells out "VideoBumperRemover" in full (that's prose, not code).

### 2. Which VDF projects to keep vs. drop

**Keep everything in the `.sln` for now** (`VDF.Web`, `VDF.CLI`, `VDF.Benchmarks`, `VDF.GUI`,
plus their test projects) — no drops yet. Revisit per-project once `VBR.Core` is further along
and it's clearer what's actually unused. `VDF.Web` and `VDF.Benchmarks` are the likeliest future
drops (no network-service deployment is planned per ADR 0002; our own diagnostic probes already
cover benchmarking needs), but dropping is cheap and reversible whenever we do it — these are
independent projects removable from the `.sln` without touching `VDF.Core` — so there's no cost
to deferring.

### 3. Bridging the `internal` gap: minimal `InternalsVisibleTo`

The primitives `VBR.Core` needs from `VDF.Core` (`ChromaprintEngine`, `OnnxEmbedder`,
`ScanEngine.SlidingWindowCompare`, `ScanEngine.TryMatchDenseFrames`,
`FfmpegEngine.GetDenseAiFrames`) are `internal`. Add:

```csharp
[assembly: InternalsVisibleTo("VBR.Core")]
```

to `VDF.Core.csproj`, alongside the existing entries for `VDF.GUI`, `VDF.CLI`, `VDF.Web`, and
the test projects. This is a one-line, low-merge-conflict addition to an upstream file — treated
as **glue** under decision 4, so it carries a "Modifications Copyright" line but stays in place
in `VDF.Core` rather than forking the file. Revisit promoting specific primitives to `public`
once the exact set we depend on has stabilized through real `VBR.Core` development (the list
above is accurate as of this ADR but is expected to grow).

### 4. Divergence strategy: track upstream, fork files only on substantial change

Default posture is **track upstream** — build all new logic in `VBR.*`, touch `VDF.Core` only
for glue. When a specific VDF file needs real change, **fork that file into `VBR.*`** rather
than editing it in place, using this concrete bright line:

> **Fork if the change adds new logic or behavior** — a new branch, a parameter that changes
> behavior, or bumper-specific logic. **Edit in place** if the change is a signature tweak, an
> access-modifier bump (e.g. `internal` → `public`), or a one-line fix.

Vendor-freezing `VDF.Core` entirely (stop tracking upstream) is **not decided now** — it's a
later call if/when enough files have forked that `VDF.Core` divergence makes upstream merges
consistently painful.

### 5. Copyright on modified VDF files

Per AGPL: modifying an AGPL file keeps the original copyright and adds ours (never remove
`0x90d`'s notice):

```
// Copyright (C) 2026 0x90d          (original)
// Modifications Copyright (C) 2026 mawcs
```

Tied to decision 4: **glue edits** (in-place, e.g. `InternalsVisibleTo`) carry dual copyright
and stay in `VDF.Core`. **Substantial changes** fork the file into `VBR.*`, also carrying dual
copyright, leaving the `VDF.Core` original pristine and mergeable. Brand-new files are pure VBR
with our header only (per the standard block in [`AGENTS.md`](../../AGENTS.md)).

### 6. UI: extend `VDF.GUI` in place

No `VBR.Gui` for now. Continue building on `VDF.GUI`, fixing known UX issues incrementally per
[`../design/ux-issues.md`](../design/ux-issues.md). This preserves the "running end-to-end app
from day one" benefit that motivated forking VDF whole (ADR 0002) rather than discarding its UI.
If specific GUI files need substantial bumper-specific rework, they fork into `VBR.*` under the
same rule as decision 4 (e.g. a bumper-review screen with no VDF equivalent would be a new
`VBR.*` file, not a `VDF.GUI` edit) — but there's no wholesale `VBR.Gui` rewrite planned.

## Consequences

Positive: clean separation between inherited engine and net-new product code; low upstream
merge friction while `VDF.Core` stays mostly untouched; a running app throughout, since
`VDF.GUI` isn't discarded; a concrete, checkable rule for the recurring "edit in place or fork"
call instead of relitigating it per file.

Negative / watch-outs: the `InternalsVisibleTo` list on `VDF.Core` is a small but real point of
upstream drift to track through merges; deferring project drops means `VDF.Web`/`VDF.CLI`/
`VDF.Benchmarks` keep building (and keep their test projects building) even though nothing uses
them yet; extending `VDF.GUI` in place means bumper-specific and inherited-duplicate-finder UI
concerns will coexist in the same project until/unless individual screens fork out.

## Open questions

- The exact primitive list bridged via `InternalsVisibleTo` will grow as `VBR.Core` is built —
  keep `VDF.Core.csproj`'s comment/list current rather than letting it silently expand.
- Revisit the `VDF.Web` / `VDF.CLI` / `VDF.Benchmarks` drop decision once `VBR.Core` has enough
  real usage to show what's actually dead weight (Phase 7's on-ingest automation could still
  want `VDF.CLI` as a scriptable entry point — don't drop it reflexively).
- When (if ever) to vendor-freeze `VDF.Core`.
