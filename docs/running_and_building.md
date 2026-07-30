# Running & building

Command reference for building, running, and testing this project day to day. For first-time environment setup (SDK install, VS Code config, NuGet/AOT troubleshooting), see[`development.md`](development.md) — this doc assumes that's already done.

VBR is *this* project — the part actively being built — so its commands come first below. VDF is the inherited engine/GUI this project forks; its commands follow, since it's mostlyalready-working infrastructure at this point rather than the day-to-day focus.

All commands run from the repo root. VBR and VDF projects currently share one solution file, `VideoBumperRemover.sln`, so building or testing against it covers everything at once.

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
default, audio as an opt-in accelerator, pHash as an experimental alternate signal
(`--detection-mode visual|audio|phash|both|all`). The reference clip is always sampled internally
from a source video + a region — there is no way to pass a pre-cut clip file (see
[`AGENTS.md`](../AGENTS.md) → "Clip extraction is the tool's job"):

```sh
dotnet run --project VBR.CLI -- match --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 10s --sample-interval 0.2s --library "D:\Media\Show"
```

Target a single file instead of a folder with `--file`:

```sh
dotnet run --project VBR.CLI -- match --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 10s --sample-interval 0.2s --file "D:\Media\Show\S01E05.mkv"
```

> **Gotcha: PowerShell + admin elevation + an SMB target (`\\server\share\...` or a mapped drive
> backed by one) can silently fail to resolve.** This is a general Windows/PowerShell behavior, not
> specific to `vbr` — an elevated PowerShell session runs under a different logon session than the
> one a network drive was mapped in (or than your normal desktop session), so SMB access that works
> fine unelevated can just not work elevated. The non-obvious part: your **shell's working
> directory doesn't need to be on the share** — pointing any argument at one (`--library`,
> `--clip-from`, `--file`) is enough to hit it. If a command against a network path fails or
> behaves oddly, try it from a non-elevated shell before assuming it's a tool bug.

Key options (run `--help` for the full list):

- `--region begin|end` — which edge the bumper lives at; drives both clip extraction and where
  each candidate is searched.
- `--clip-length` (required) / `--search-length` (defaults to clip length + 20s) /
  `--sample-interval` (default 1s; go as low as ~0.2s for short clips) — durations take a bare
  number of seconds or a suffix (`5.1s`, `200ms`).
- `--edge-boundary` / `--sparse-interval` — for a bumper longer than a small dense zone (e.g. a
  47s title sequence): `--edge-boundary` sets how far from the true edge stays dense
  (`--sample-interval`), with everything beyond it sampled at `--sparse-interval`. Default: the
  whole `--clip-length`/`--search-length` window is dense — today's plain single-density behavior
  when these are left alone.
- `--phash-presence-threshold` (default 0.96) — pHash's own presence gate, only relevant with
  `--detection-mode phash|all`. Treat `phash` mode as experimental: on real testing it's had a
  much narrower true/false-positive margin than visual and has missed real matches visual caught.
- **Exactly one of `--library <folder(s)>` or `--file <path>` is required.** `--library` accepts
  one or more semicolon-delimited folders (e.g. `"D:\Show;D:\Extras"`) — video files under every
  one are combined into a single candidate list (deduplicated, so overlapping/nested folders don't
  double-count a file). Traversed **recursively by default**; `--no-recurse` searches only each
  folder's top level (no effect with `--file`). `--exclude-folders <folder(s)>` (same
  semicolon-delimited shape) drops any candidate whose path falls under one of them, regardless of
  which `--library` folder it came from — a path/folder rule, independent of the `.vbr.`-output
  filename filter below. Results print each file's path relative to whichever `--library` folder
  contains it, or just the file name for `--file`.
- `--output <file>` — also write the match report (parameter header + the same rows/summary as
  the console) to a file.
- `--dump-frames <dir>` — diagnostic: dump every sampled frame as a PNG (`clip-dense/`/`clip-sparse/`
  plus one numbered folder per candidate) to inspect exactly what the visual/pHash matching compared.
- `--verbose` — logs the resolved ONNX model path, per-file sampled/usable frame counts, each
  inference batch call, and the exact ffmpeg command lines run, to the console and to VDF's
  `log.txt` (next to the running executable, or the state folder if that's not writable). VDF's
  own `Logger` already wrote warnings/errors there before this project touched it —
  `--verbose` adds detailed Info-level tracing on top, and it's on-by-default-to-disk: every
  run logs to `log.txt` regardless of `--verbose`, which only controls whether the CLI *also*
  echoes it live. Concrete proof the AI model is real, not just trusted by reading the code —
  see [`PROGRESS.md`](PROGRESS.md) (2026-07-20 entry) for the full reasoning.

Both regions are validated end to end (2026-07-18): begin-region Netflix-ident test 12/12 true
positives vs 0 false positives across two unrelated libraries, end-stack regression clean — the
recorded numbers live in [`iterativeplan.md`](iterativeplan.md) §C.

### Run — `vbr remove`

```sh
dotnet run --project VBR.CLI -- remove --help
```

Finds a bumper (same matching as `vbr match` — reuses all its options, including `--file` for a
single target and `--verbose` for a full ffmpeg-command/model-load audit trail) and removes it
from every match, non-destructively: writes a sibling `name.vbr.ext` beside the source plus a
JSON manifest (`name.json`, named after the *original*, not the output), never touching the
original. See [ADR 0007](decisions/0007-removal-command.md) for the full design.

**Prints progress as it goes** (docs/iterativeplan.md, "CLI feedback during remove") — `[i/N]
Checking: <file>` before each candidate is compared, `Match found (...) — removing bumper
(re-encode|stream-copy)...` before a matched file's cut starts, and a live `NN%  (Xs / Ys, Z.ZZx
realtime)` line while the cut itself runs — a re-encode removal can take as long as encoding the
whole file normally would, and previously printed nothing at all in the meantime. All of this goes
to stderr; stdout still carries exactly one line per candidate, so `--output`'s report file and any
script piping stdout are unaffected.

```sh
dotnet run --project VBR.CLI -- remove --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 20.5s --sample-interval 0.2s --library "D:\Media\Show"
```

Or a single file, exactly as with `match`:

```sh
dotnet run --project VBR.CLI -- remove --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 20.5s --sample-interval 0.2s --file "D:\Media\Show\S01E05.mkv"
```

**Both the bumper and the library can each independently be ad hoc or persisted** (docs/iterativeplan.md,
"Utilizing Databases") — four combinations, freely mixable:

```sh
# ad hoc library + a named bumper from a catalog -- no re-extraction of the bumper
dotnet run --project VBR.CLI -- remove --library "D:\Media\Show" --bumper-label "Netflix ident" --catalog-name "my-bumpers"

# a vbr scan'd library database + an ad hoc bumper clip -- no re-scan of any candidate
dotnet run --project VBR.CLI -- remove --library-name "my-show" --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 8s

# both persisted -- the fastest combination: zero ffmpeg/ONNX work for matching, only the final cut decodes
dotnet run --project VBR.CLI -- remove --library-name "my-show" --bumper-label "Netflix ident" --catalog-name "my-bumpers"
```

- `--bumper-label` — look up a named bumper instead of an ad hoc `--clip-from` clip (case-insensitive,
  looked up in `--catalog-name`, defaulting to the `default` catalog if that's omitted).
  `--clip-from`/`--region`/`--clip-length` are invalid together with this — the catalog entry's own
  region and precisely-measured duration are used instead. `--catalog-db-folder` mirrors
  `add-bumper`'s own option and must be accompanied by both `--bumper-label` and `--catalog-name`.
- `--library-name` — search a `vbr scan`'d database's files instead of walking `--library`'s folder(s)
  live; invalid together with `--library`. `--library-db-folder` mirrors `vbr scan`'s own option and
  must be accompanied by `--library-name`. Tombstoned/missing database entries are silently skipped
  (nothing on disk to remove a bumper from); `--exclude-folders` and the existing `.vbr.`-output
  filter still apply. `--no-recurse` has no effect in this mode.
- When both `--bumper-label` and `--library-name` are given, matching touches no ffmpeg/ONNX at all —
  every fingerprint on both sides is already persisted; only files that actually match get decoded,
  for the removal cut itself.

**`--re-encode` defaults to `true` (Mode B — re-encode)**, and both modes are implemented and
verified against real media:

- **`true` (default): frame-accurate, correctly realigns subtitle cues.** Slow — it decodes and
  re-encodes the *entire kept portion* of the file, not just the trimmed region (essentially the
  whole episode for an end-region cut), so expect it to take roughly as long as a normal encode
  of that file. Video is CPU-encoded (libx264, fixed placeholder quality settings — real
  codec/GPU configurability is future work, see ADR 0007).
- **`--re-encode false`: fast (no decode/encode at all), but keyframe-bound and — for
  begin-region cuts specifically — does not realign subtitle cues.** Built first, per the
  maintainer's chosen order, for faster iteration while testing.

**`--clip-length` must be the bumper's full, true length — not just enough to match reliably.**
Verified live (2026-07-19): a length that reliably *matches* a multi-card studio ident stack can
still be shorter than the *whole* stack, and removal cuts exactly what you tell it to. Using a
10s length against a real ~20.5s Daredevil end-stack matched fine but left part of the stack
(`abc studios`/`MARVEL` cards) in the "cleaned" output; the corrected 20.5s length cut cleanly.
There's no per-file check to catch an under-measured length (by design — see ADR 0007) — get the
length right at clip-selection time.

**Verified live (2026-07-29)**, all four bumper/library source combinations, against a real
Daredevil Netflix end-card (`--region end --clip-length 8s`) plus a real distractor file: the ad
hoc/ad hoc combination reproduced its pre-existing behavior unchanged; the catalog-bumper
combination reused the catalog's 29 stored fingerprints with no re-extraction; the database-library
combination reused the database's cached fingerprints (126, dense+sparse merged) with no re-scan;
the fully-cached combination logged zero ONNX/ffmpeg activity for matching at all. Full numbers:
[`iterativeplan.md`](iterativeplan.md).

**Stream-copy cut points aren't exact**: end-region cuts land at a keyframe **at least 1s before**
the arithmetic cut point (ffmpeg's `-t`/`-to` overshoots by ~0.2s past any requested boundary, so
the code trims a bit extra rather than risk leaking bumper content); begin-region cuts land at
the next keyframe at or after the boundary (verified safe — snaps forward, never backward into
the bumper). Both are documented, accepted v1 stream-copy characteristics (see the ADR).
**Re-encode cut points are frame-accurate** (~28ms off in testing) — this is the main practical
reason to prefer it beyond subtitle correctness.

### Run — `vbr commit`

```sh
dotnet run --project VBR.CLI -- commit --help
```

Promotes verified `.vbr.` outputs (from a prior `vbr remove` run) to replace their originals —
deletes the pre-cut original and its manifest. **The only command that deletes video files**; see
[ADR 0008](decisions/0008-cleanup-command.md) for the full design (built as `vbr cleanup`, renamed
to `vbr commit` 2026-07-29 — see the ADR's amendment note and
[`iterativeplan.md`](iterativeplan.md) → "CLI terminology & multi-folder libraries" for why). Run
this *between* bumper-removal passes, once you've reviewed each `.vbr.` output — not as a
substitute for reviewing them, and not automatically after `remove`.

```sh
dotnet run --project VBR.CLI -- commit --library "D:\Media\Show"
```

Or a single file — scoped to *only* that file, never the rest of its directory (decided,
2026-07-20; see the ADR's Decision 10):

```sh
dotnet run --project VBR.CLI -- commit --file "D:\Media\Show\S01E05.mkv"
```

Key behavior:

- **Pairing is filename-derived, not manifest-derived.** The JSON manifest can be separated from,
  or deleted independently of, the video it describes, so it's never trusted to decide what to
  touch — only the `.vbr.` naming convention is (the exact inverse of `remove`'s own
  `name.ext` → `name.vbr.ext`).
- **Per file: mark the original for deletion, promote the output into its name, delete the old
  original and manifest — fully resolved before moving to the next file.** If promoting fails, the
  mark is rolled back and the original is restored; a failure deleting the *old* original
  afterward is never rolled back (the swap already succeeded — that's a disk-space problem, not a
  correctness one) and is reported separately as "pending reclamation," not broken.
- **A cheap recovery sweep runs automatically at the start of every directory**, before anything
  else — no flag needed. It reconciles leftover marker files from a previous run that didn't
  finish cleanly (crashed, killed, or otherwise interrupted): if the promotion had already
  completed, it just retries the delete; if it hadn't, it restores the original and lets the
  normal pass reprocess the pair from scratch in the same run.
- **No trash/soft-delete stage.** `remove`'s non-destructive sibling output already is the review
  window — the original survives untouched until you run `commit`. A second staged-deletion layer
  here would just triple disk usage on libraries that are already large.
- `--library` accepts the same semicolon-delimited multiple folders as `match`/`remove`/`scan`
  (each folder's own directory tree walked, deduplicated where folders overlap), and
  `--exclude-folders` skips whole directories that fall under it, before `commit` ever looks
  inside them for `.vbr.` pairs.
- `--validate-files` — off by default. When set, ffprobes each `.vbr.` output and sanity-checks
  its duration (precisely, against the manifest, when one is present and parses; otherwise a
  coarser "shorter than the original" check) before it's allowed anywhere near the original. A
  file that fails is reported broken and left completely alone. Off by default because the CLI
  can't enforce that you actually reviewed the output — this assists that, it doesn't replace it.
- `--output <file>` — also write the commit report to a file, same as `match`/`remove`.
- `--verbose` — same logging hookup as `match`/`remove`: every mark/promote/delete call and
  recovery action, to the console and `log.txt`.

### Run — `vbr scan`

```sh
dotnet run --project VBR.CLI -- scan --help
```

Builds/updates a **cached fingerprint database** for a library — samples every file's true edges
(dense) and whole-file middle (sparse) up front so a bumper can be found later without re-decoding.
Unlike `match`/`remove`/`commit`, it doesn't take `--clip-from`/`--region`/`--detection-mode` —
there's nothing to match against yet, only fingerprints to gather. See
[`iterativeplan.md`](iterativeplan.md) → "Library scan — implemented and validated" for the full
design and validated numbers.

```sh
dotnet run --project VBR.CLI -- scan --library "D:\Media\Show" --library-name Show
```

Key options:

- `--library` accepts the same semicolon-delimited multiple folders as `match`/`remove`/`commit`
  (e.g. `--library "D:\Media\Show;D:\Media\Extras"`), combined into one candidate list and
  deduplicated where folders overlap; `--exclude-folders` works the same way too. All of a
  multi-folder `--library`'s files land in the *same* database — there's still only one
  `--library-name`/one database file per `scan` invocation, not one per folder.
- `--edge-boundary`/`--sample-interval`/`--sparse-interval` (defaults 20s / 0.2s / 4s) — how deep
  from each true edge is sampled densely, and the dense/sparse intervals. These are **scan-specific
  defaults**, not the same options on `match`/`remove` (which are relative to a *known*
  `--clip-length`; the scan has no known bumper length, so it presumptively covers the first/last
  20s of every file instead).
- `--library-name <name>` / `--library-db-folder <folder>` — every named library gets its own
  independent database file, always named `{library-name}.vbrdb` (the file name is only ever derived
  from `--library-name`; `--library-db-folder` names the *containing folder*, not the file, and
  doesn't need to exist yet). Default name: `--library`'s own folder name (the *first* folder, if
  more than one is given — override with `--library-name` if that guess isn't the one you want);
  default location: a dedicated VBR state folder (`%LOCALAPPDATA%\VideoBumperRemover\database\` on
  Windows), never VDF's own database folder.
- `--include-vbr-outputs` — off by default: `name.vbr.ext` outputs from a prior `remove` are
  transitional staging artifacts (a review window before `commit`), usually redundant to include.
- `--rescan` (alias `--force`) — bypass change detection and re-sample every candidate, e.g. after
  changing `--edge-boundary`/interval defaults.
- Change detection mirrors VDF's own incremental-rescan logic: unchanged size+timestamps skip
  entirely (no decode); same size but a touched timestamp is verified via a content hash before
  trusting the cache; anything else re-samples. The database is checkpointed to disk periodically
  during a scan (not just at the end), so an interrupted run only loses the work since the last
  checkpoint.
- `--console-info quiet|info|debug|verbose|trace` — how much progress detail hits the console.
  `info` (default) is a single updating `x/total` counter. `quiet` is nothing but the final
  summary/errors, which always print regardless of this setting. `debug` prints each file's
  name+result plus an `x/total` progress line, one pair per file. `verbose` adds the underlying
  model-load/frame-count/checkpoint log detail on top of `debug`'s lines (`--verbose` is shorthand
  for `--console-info verbose`; an explicit `--console-info` wins if both are given). `trace` is
  reserved for finer-grained detail than anything logs today — same as `verbose` for now.
- `--log-file <path>` / `--log-level` — an independently leveled, appended-to log file (same five
  levels as `--console-info`, applied separately). Default level `verbose`, default location
  sibling to the database file with the same library name and a `.log` extension — so a quiet console
  plus a fully-detailed log file is the out-of-the-box default, not something you have to ask for.

**Verified live (2026-07-26)** against real media: a 49-minute episode scans in ~21s and produces
197 merged fingerprints; an unchanged re-scan takes 0.16s; a touched-mtime-but-same-content file
still skips via the content hash; `.vbr.` exclusion and `--include-vbr-outputs` both confirmed;
two independently-named libraries produce two independent database files. Full numbers:
[`iterativeplan.md`](iterativeplan.md).

Report rows: `CLEANED`, `BROKEN` (needs attention — original untouched), `PENDING` (cleaned, but
couldn't remove the old original/manifest — self-heals on a future run), `SKIPPED` (`--file` only:
no `.vbr.` output exists for the target), and `RECOVER` for anything the startup sweep reconciled.

### Run — `vbr add-bumper`

```sh
dotnet run --project VBR.CLI -- add-bumper --help
```

Adds one bumper to a named, persistent **catalog** — samples `--clip-from`'s requested region directly from source, extracts a reference clip and a native-resolution thumbnail, measures precise duration, and writes a new entry. Like `match`/`remove`, it never accepts a pre-cut clip file(`--clip-from`/`--region`/`--clip-length`, identical meaning). Unlike `vbr scan`, a catalog is**not** tied to a media folder at all — it's named directly (`--catalog-name`), so the same catalog can be built from one collection of videos and applied to a different one later. (An earlier version mirrored `scan`'s `--library`/`--library-name` pair here; that wrongly implied a catalog belongs to one scanned library, and `--library`'s value turned out to never be used for anything —see [`iterativeplan.md`](iterativeplan.md) → "Bumper catalog" for the full story.) Doesn't match or remove anything, and doesn't read the catalog back yet — see that same doc for what's still unbuilt(catalog-aware matching/"apply", curation, sub-bumper relationships, export/import).

```sh
dotnet run --project VBR.CLI -- add-bumper --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 8s --label "Studio ident" --catalog-name "my-bumpers"
```

Key options:

- `--label` (required, max 30 characters) — the one field you must supply yourself; no
  auto-suggestion from the filename/folder (considered and rejected during planning — too many
  edge cases to guess reliably, e.g. show name living in a grandparent folder, episode codes to
  strip). **Must be unique within the target `--catalog-name`, case-insensitively** — a duplicate
  is rejected before any sampling/extraction work starts; a different catalog may freely reuse the
  same label.
- `--description` (optional, max 255 characters) and `--tags` (optional, comma-separated) add
  curation context. All three (including `--label`) are enforced at the CLI, not the underlying
  data model.
- `--catalog-name` (required) — names the catalog itself (also its file, a `.vbrcat`) — a plain
  label you choose, independent of any `--library`/media folder.
- `--catalog-db-folder` — where this catalog's file lives; doesn't need to exist yet. Default: a
  dedicated folder under VBR's own state folder (`%LOCALAPPDATA%\VideoBumperRemover\catalog\` on
  Windows) — a sibling of, not shared with, `vbr scan`'s own database folder.
- `--verbose` — same logging convention as every other command: model path, sampled/usable frame
  counts, and exact ffmpeg commands run, to the console and `log.txt`.

**Verified live (2026-07-28)** against a real Daredevil episode's Netflix end-card (`--region end
--clip-length 8s`, the same length ADR 0007 independently measured for it): 17 usable fingerprints,
a real reference clip and native-resolution thumbnail, a real audio fingerprint, duration measured
at exactly 8s from the extracted clip. A second bumper added to the same catalog correctly
accumulated as a second entry without touching the first. Full numbers:
[`iterativeplan.md`](iterativeplan.md).

### Run — `vbr list-bumpers`

```sh
dotnet run --project VBR.CLI -- list-bumpers --help
```

Lists the bumpers in a catalog, one line each: `"label", region, length, "thumbnail location"`. Read-only — doesn't touch the catalog file itself, but materializes each entry's embedded thumbnail to a real JPEG under the system temp folder (`{temp}\.vbrthumbs\{label}-thumbnail.jpg`, rewritten on every call) since the catalog only ever stores thumbnail bytes inline (see `add-bumper` above).

```sh
dotnet run --project VBR.CLI -- list-bumpers --catalog-name "my-bumpers"
```

Key options:

- `--catalog-name` — which catalog to list. Default: `"default"` when omitted (unlike `add-bumper`, where it's required).
- `--catalog-db-folder` — where that catalog's file lives; same default and same "must be a folder, not a file" guard as `add-bumper`.
- `--show-guids` — prints each bumper's `Id` on its own line immediately before that bumper's regular output line.

An empty or nonexistent catalog prints `Catalog '<name>' has no bumpers.` and exits 0, rather than erroring — same "a fresh catalog isn't an error" convention `add-bumper` already established.

**Verified live (2026-07-29)** against a scratch catalog with two real bumpers added via `add-bumper`: output matched the format above exactly, both thumbnails written as real, loadable JPEGs matching the byte counts `add-bumper` reported at capture time, and a label containing filesystem-unsafe characters (`:`, `/`) was sanitized in the thumbnail filename while still shown verbatim in the console line. Full numbers: [`iterativeplan.md`](iterativeplan.md).

### Test

```sh
dotnet test VBR.Tests
dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests"
```

`AudioBumperMatcherTests` and `ClipRemoverTests`' real-media case only run against real video
files, gated by environment variables — they skip cleanly when unset, so a normal `dotnet test`
run never needs them. Each header comment has the exact recipe; representative examples:

`LibraryCleanerTests` (`vbr commit`'s mark/promote/delete/recovery logic) is different: it's pure
filesystem manipulation with no video content involved, so almost all of it runs as ordinary,
always-on tests against temp directories with plain dummy files — no environment variables, no
curated library. The two `--validate-files` tests are the exception, shelling out to ffmpeg/
ffprobe on PATH directly (a hard dependency of this whole project either way, not optional test
setup).

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
