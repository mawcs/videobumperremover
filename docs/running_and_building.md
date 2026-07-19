# Running & building

Command reference for building, running, and testing this project day to day. For first-time
environment setup (SDK install, VS Code config, NuGet/AOT troubleshooting), see
[`development.md`](development.md) — this doc assumes that's already done.

VBR is *this* project — the part actively being built — so its commands come first below. VDF
is the inherited engine/GUI this project forks; its commands follow, since it's mostly
already-working infrastructure at this point rather than the day-to-day focus.

All commands run from the repo root. VBR and VDF projects currently share one solution file,
`VideoBumperRemover.sln`, so building or testing against it covers everything at once.

## Whole solution

```sh
dotnet build VideoBumperRemover.sln   # build everything: VBR + inherited VDF
dotnet test VideoBumperRemover.sln    # test everything
```

## VBR — this project

### Build

```sh
dotnet build VBR.Core/VBR.Core.csproj
dotnet build VBR.CLI/VBR.CLI.csproj
dotnet build VBR.Tests/VBR.Tests.csproj
```

### Run — `vbr match`

```sh
dotnet run --project VBR.CLI -- --help
dotnet run --project VBR.CLI -- match --help
```

Finds a bumper's presence across a library of videos — visual DINOv2 presence matching by
default, audio as an opt-in accelerator (`--detection-mode visual|audio|both`). The reference
clip is always extracted internally from a source video + a region — there is no way to pass a
pre-cut clip file (see [`AGENTS.md`](../AGENTS.md) → "Clip extraction is the tool's job"):

```sh
dotnet run --project VBR.CLI -- match --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 10s --sample-interval 0.2s --library "D:\Media\Show"
```

Key options (run `--help` for the full list):

- `--region begin|end` — which edge the bumper lives at; drives both clip extraction and where
  each candidate is searched.
- `--clip-length` (required) / `--search-length` (defaults to clip length + 20s) /
  `--sample-interval` (default 1s; go as low as ~0.2s for short clips) — durations take a bare
  number of seconds or a suffix (`5.1s`, `200ms`).
- `--library` is traversed **recursively by default**; `--no-recurse` searches only its top
  level. Results print library-relative paths.
- `--output <file>` — also write the match report (parameter header + the same rows/summary as
  the console) to a file.
- `--dump-frames <dir>` — diagnostic: dump every sampled frame as a PNG (`clip/` + one numbered
  folder per candidate) to inspect exactly what the visual matcher compared.

Both regions are validated end to end (2026-07-18): begin-region Netflix-ident test 12/12 true
positives vs 0 false positives across two unrelated libraries, end-stack regression clean — the
recorded numbers live in [`iterativeplan.md`](iterativeplan.md) §C.

### Run — `vbr remove`

```sh
dotnet run --project VBR.CLI -- remove --help
```

Finds a bumper (same matching as `vbr match` — reuses all its options) and removes it from every
match, non-destructively: writes a sibling `name.vbr.ext` beside the source plus a JSON manifest
(`name.vbr.json`), never touching the original. See
[ADR 0007](decisions/0007-removal-command.md) for the full design.

```sh
dotnet run --project VBR.CLI -- remove --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 20.5s --sample-interval 0.2s --library "D:\Media\Show" --re-encode false
```

**`--re-encode false` is currently required** — re-encode (Mode B, the eventual default) isn't
implemented yet; omitting the flag (or passing `true`) prints a clear error rather than silently
doing something else. Stream-copy (Mode A) is built first per the maintainer's chosen order
(faster to iterate on while testing); re-encode is next.

**`--clip-length` must be the bumper's full, true length — not just enough to match reliably.**
Verified live (2026-07-19): a length that reliably *matches* a multi-card studio ident stack can
still be shorter than the *whole* stack, and removal cuts exactly what you tell it to. Using a
10s length against a real ~20.5s Daredevil end-stack matched fine but left part of the stack
(`abc studios`/`MARVEL` cards) in the "cleaned" output; the corrected 20.5s length cut cleanly.
There's no per-file check to catch an under-measured length (by design — see ADR 0007) — get the
length right at clip-selection time.

Stream-copy cut points aren't exact: end-region cuts land at a keyframe **at least 1s before**
the arithmetic cut point (ffmpeg's `-t`/`-to` overshoots by ~0.2s past any requested boundary, so
the code trims a bit extra rather than risk leaking bumper content); begin-region cuts land at
the next keyframe at or after the boundary (verified safe — snaps forward, never backward into
the bumper). Both are documented, accepted v1 stream-copy characteristics (see the ADR).

### Test

```sh
dotnet test VBR.Tests
dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests"
```

`AudioBumperMatcherTests` and `ClipRemoverTests`' real-media case only run against real video
files, gated by environment variables — they skip cleanly when unset, so a normal `dotnet test`
run never needs them. Each header comment has the exact recipe; representative examples:

```powershell
$env:BUMPER_CLIP_EPISODE = "D:\Media\Show\S01E01.mkv"
$env:BUMPER_CLIP_TAIL_SECONDS = "40"
$env:BUMPER_EPISODES_DIR = "D:\Media\Show"
dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests" -l "console;verbosity=detailed"
```

```powershell
$env:BUMPER_REMOVE_SOURCE = "D:\Media\Show\S01E02.mkv"   # a file the bumper clip matches
$env:BUMPER_REMOVE_REGION = "end"
$env:BUMPER_REMOVE_LENGTH_SECONDS = "20.5"
dotnet test VBR.Tests --filter "FullyQualifiedName~ClipRemoverTests" -l "console;verbosity=detailed"
```

## VDF — inherited engine (Video Duplicate Finder)

### Build

```sh
dotnet build VDF.GUI/VDF.GUI.csproj
dotnet build VDF.CLI/VDF.CLI.csproj
```

### Run

#### VDF GUI (Avalonia desktop app)

```sh
dotnet run --project VDF.GUI
```

The main app — library scanning, Deep Clean, results review. Known rough edges are tracked in
[`design/ux-issues.md`](design/ux-issues.md).

#### VDF CLI (`vdf-cli`)

VDF's own headless CLI — scan/compare/mark/database subcommands.

```sh
dotnet run --project VDF.CLI -- --help
dotnet run --project VDF.CLI -- scan --include "D:\Media"
dotnet run --project VDF.CLI -- compare
```

#### VDF Web

```sh
dotnet run --project VDF.Web
```

#### Other projects

`VDF.Benchmarks` (BenchmarkDotNet perf suite) and `FakeDatabaseGenerator` (dev utility for
seeding a large fake scan DB) also build and run via `dotnet run --project <name>`, but aren't
part of the normal day-to-day workflow — see their source if you need them.

### Test

```sh
dotnet test VDF.Core.Tests
dotnet test VDF.IntegrationTests
```

Two more probes only run against real video files, gated by environment variables (same
skip-cleanly-by-default behavior as `AudioBumperMatcherTests` above) — see each file's header
comment for its exact recipe:

- `VDF.IntegrationTests/Comparison/VisualBumperMatchProbe.cs` — visual/DINOv2 matching against
  cached embeddings.
- `VDF.IntegrationTests/Comparison/VisualTailProbe.cs` — fine-grained visual tail matching,
  auto-cutting the clip from a reference episode.
