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

Finds a bumper's presence across a library of videos (`--detection-mode visual|audio|phash|both|all`,
default `all`) — **every signal that runs must agree, not just whichever ran first** (revised
2026-08-13): visual, plus pHash unconditionally, plus audio whenever the bumper actually has real
audio to check (a silent bumper is exempt — audio never vetoes a match it structurally can't judge).
`visual`/`audio`/`phash` alone are for deliberately singling out one detector (debugging, comparing
signals against each other), not the normal case. The reference clip is always sampled internally
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
- `--matching-strategy` (added 2026-08-14, ad hoc only — invalid together with `--bumper-label`,
  whose catalog entry already has its own stored strategy) — the ad hoc counterpart to
  `add-bumper`'s own `--matching-strategy` (see that section below): which signal(s) must agree for
  *this run's* bumper, overriding `--detection-mode` outright when given (same values:
  `corroborated`/`visualonly`/`audioonly`/`phashonly`/`novisual`/`noaudio`/`nophash`). Default:
  unset — `--detection-mode` alone decides, today's exact behavior for every run that doesn't pass
  this flag. For a one-off `--clip-from` investigation where a bumper needs a different strategy
  than the rest of a catalog would use, without adding it to a catalog first.
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
- `--hardware-accel none|auto|...` (default `auto`) — the single knob for every GPU code path: ffmpeg
  decode (`-hwaccel`), GPU re-encode (H.264/HEVC only, probe-verified NVENC/QSV/AMF — other codecs
  stay CPU-only), and ONNX inference (DirectML, Windows only). `none` disables all of it; `auto`
  lets ffmpeg/ONNX Runtime pick the best available device, with a silent, safe fallback to CPU at
  every layer if GPU acceleration isn't actually available. See
  [ADR 0013](decisions/0013-gpu-acceleration.md).
- `--no-native-ffmpeg-binding` — opt-out: by default (2026-08-03) sampling decodes in-process via
  VDF.Core's native FFmpeg binding instead of spawning `ffmpeg.exe` per sampling call, falling back
  to the CLI process automatically and safely on any native failure. Pass this flag to rule out the
  native path entirely (e.g. while troubleshooting) rather than trust that fallback. **Currently a
  no-op for most real ffmpeg installs** (e.g. this project's own dev machine's Chocolatey install)
  — native decode needs shared libraries (`avformat-*.dll` etc.) that typical static ffmpeg builds
  don't ship; VBR.CLI has no acquisition mechanism for those yet (tracked in
  [`PROGRESS.md`](PROGRESS.md) → "Open / next steps"). See
  [ADR 0015](decisions/0015-native-ffmpeg-binding.md).
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

**Both the bumper and the library can each independently be ad hoc or persisted** (docs/iterativeplan.md,
"Utilizing Databases" — extended from `remove`-only to `match` too, 2026-08-13), exactly the same four
combinations `vbr remove` supports (see that section below) minus the removal step itself:

```sh
# ad hoc library + a named bumper from a catalog -- no re-extraction of the bumper
dotnet run --project VBR.CLI -- match --library "D:\Media\Show" --bumper-label "Netflix ident" --catalog-db "my-bumpers.vbrcat"

# a vbr scan'd library database + an ad hoc bumper clip -- no re-scan of any candidate
dotnet run --project VBR.CLI -- match --library-db "my-show.vbrdb" --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 8s

# both persisted -- zero ffmpeg/ONNX work for matching at all
dotnet run --project VBR.CLI -- match --library-db "my-show.vbrdb" --bumper-label "Netflix ident" --catalog-db "my-bumpers.vbrcat"
```

- `--bumper-label`/`--catalog-db` and `--library-db` — same options, same defaults, same
  frameQuality staleness warning, as `vbr remove` (see that section below for the full
  description) — this is the natural way to dry-run/investigate a catalog-and-scanned-library
  setup before committing to an actual `remove`.

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
dotnet run --project VBR.CLI -- remove --library "D:\Media\Show" --bumper-label "Netflix ident" --catalog-db "my-bumpers.vbrcat"

# a vbr scan'd library database + an ad hoc bumper clip -- no re-scan of any candidate
dotnet run --project VBR.CLI -- remove --library-db "my-show.vbrdb" --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 8s

# both persisted -- the fastest combination: zero ffmpeg/ONNX work for matching, only the final cut decodes
dotnet run --project VBR.CLI -- remove --library-db "my-show.vbrdb" --bumper-label "Netflix ident" --catalog-db "my-bumpers.vbrcat"
```

- `--bumper-label` — look up a named bumper instead of an ad hoc `--clip-from` clip (case-insensitive,
  looked up in `--catalog-db`, defaulting to `default.vbrcat` under VBR's own state folder if that's
  omitted). `--clip-from`/`--region`/`--clip-length` are invalid together with this — the catalog
  entry's own region and precisely-measured duration are used instead. `--catalog-db` must be
  accompanied by `--bumper-label`.
- `--library-db` — search a `vbr scan`'d database's files instead of walking `--library`'s folder(s)
  live; invalid together with `--library`. No default path in this mode — the flag's presence is
  itself what selects it. Tombstoned/missing database entries are silently skipped (nothing on disk
  to remove a bumper from); `--exclude-folders` and the existing `.vbr.`-output filter still apply.
  `--no-recurse` has no effect in this mode.
- When both `--bumper-label` and `--library-db` are given, matching touches no ffmpeg/ONNX at all —
  every fingerprint on both sides is already persisted; only files that actually match get decoded,
  for the removal cut itself.
- If either side's file was saved under different fingerprint-recipe config settings than are
  currently active — `frameQuality` or `audio.bucketSeconds` (added 2026-08-14, see
  [`iterativeplan.md`](iterativeplan.md)'s "Audio bucket phase-alignment" entry) — `remove` prints an
  unconditional `Warning:` line naming what drifted (docs/iterativeplan.md, "File-path DB options"
  entry, Part 3) — re-scan/`--rescan` or re-add the bumper to clear it.

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

### Run — `vbr trim`

```sh
dotnet run --project VBR.CLI -- trim --help
```

Cuts a fixed `--length` from the `--region` edge of every file under `--paths`, **unconditionally —
no matching, no fingerprints, no catalog concept at all** (docs/iterativeplan.md, "Per-bumper
matching strategy" entry, Change 1). A standalone top-level command, not a `remove` mode — `remove`'s
entire matching surface (`--detection-mode`, `--presence-threshold`, `--catalog-db`, ...) is
meaningless here, so it doesn't exist on this command at all. For when you already know exactly what
needs to come off (e.g. a fixed-length intro every file in a folder shares) and don't need presence
detection. Uses the same `ClipRemover.Remove` cut mechanism `remove` does, so everything in that
section above about `--re-encode`'s two modes, stream-copy's keyframe-bound cut points, and
re-encode's frame-accuracy applies here unchanged.

```sh
dotnet run --project VBR.CLI -- trim --length 4.5s --region begin --paths "D:\Media\Show\S01E01.mkv;D:\Media\Extras"
```

Key options:

- `--length` (required) / `--region begin|end` (required) — how much to cut, and from which edge.
- `--paths` (required) — semicolon-delimited list of files **and/or** folders, replacing
  `--library`/`--file`/`--library-db` entirely for this command: a file entry is trimmed as-is (no
  extension filter, same trust convention `--file` uses elsewhere); a folder entry is walked for
  recognized video files, same as `--library`. Every entry must exist (a missing path is a
  parse-time error). `--no-recurse`/`--exclude-folders` apply to any folder entries, same meaning as
  everywhere else.
- Every resolved candidate is trimmed **with zero content verification** — a folder entry
  containing files that don't actually have the described segment is silently truncated too.
  That's inherent to "no matching, on purpose," not a flaw to design around.
- `--output <file>` — write the trim report to a file, same shape as `match`/`remove`'s own.

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

Or a `vbr scan`'d library database (added 2026-08-05, same option `vbr remove` already supports on
its candidate side — see `vbr remove`'s "Utilizing Databases" combinations above):

```sh
dotnet run --project VBR.CLI -- commit --library-db "my-show.vbrdb"
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
  inside them for `.vbr.` pairs. Exactly one of `--library`, `--file`, or `--library-db` is
  required.
- `--library-db` — search a `vbr scan`'d database's files instead of walking `--library`'s
  folder(s) live; invalid together with `--library`/`--file`. No default path — the flag's presence
  is itself what selects this mode. The database only supplies candidate *paths* —
  pairing/promotion is still the same filename-derived logic either way, and each candidate gets
  the exact same per-file recovery-sweep-then-pair handling `--file` already uses.
  Tombstoned/missing database entries are silently skipped, same as `vbr remove`'s own
  `--library-db` handling; `--exclude-folders` and the `.vbr.`-entry filter still apply;
  `--no-recurse` has no effect in this mode. **Only a database entry with a `.vbr.` output or a
  stray recovery marker actually waiting on disk produces any output at all** — a file `vbr remove`
  never touched is skipped with nothing printed and doesn't count toward the summary, matching
  `--library`'s own directory walk (which likewise only ever looks at `.vbr.`-suffixed files, never
  every file in a directory) rather than flooding the report with "nothing to do" rows across a
  large library.
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
dotnet run --project VBR.CLI -- scan --library "D:\Media\Show" --library-db "Show.vbrdb"
```

Key options:

- `--library` accepts the same semicolon-delimited multiple folders as `match`/`remove`/`commit`
  (e.g. `--library "D:\Media\Show;D:\Media\Extras"`), combined into one candidate list and
  deduplicated where folders overlap; `--exclude-folders` works the same way too. All of a
  multi-folder `--library`'s files land in the *same* database — there's still only one
  `--library-db`/one database file per `scan` invocation, not one per folder.
- `--edge-boundary`/`--sample-interval`/`--sparse-interval` (defaults 20s / 0.2s / 4s) — how deep
  from each true edge is sampled densely, and the dense/sparse intervals. These are **scan-specific
  defaults**, not the same options on `match`/`remove` (which are relative to a *known*
  `--clip-length`; the scan has no known bumper length, so it presumptively covers the first/last
  20s of every file instead).
- `--library-db <path>` — absolute or relative path to this library's database file, any extension
  (docs/iterativeplan.md, "File-path DB options" entry, Part 2 — replaced the old
  `--library-name`/`--library-db-folder` pair 2026-08-12). Default when omitted: derived from
  `--library`'s own folder name (the *first* folder, if more than one is given), under a dedicated
  VBR state folder (`%LOCALAPPDATA%\VideoBumperRemover\database\` on Windows), never VDF's own
  database folder.
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
- `--hardware-accel` / `--no-native-ffmpeg-binding` — same shared GPU/native-decode options as
  `match` (see above).

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

Adds one bumper to a named, persistent **catalog** — samples `--clip-from`'s requested region directly from source, extracts a reference clip and a native-resolution thumbnail, measures precise duration, and writes a new entry. Like `match`/`remove`, it never accepts a pre-cut clip file(`--clip-from`/`--region`/`--clip-length`, identical meaning). Unlike `vbr scan`, a catalog is**not** tied to a media folder at all — it's named by its file path (`--catalog-db`), so the same catalog can be built from one collection of videos and applied to a different one later. (An earlier version mirrored `scan`'s `--library`/`--library-name` pair here; that wrongly implied a catalog belongs to one scanned library, and `--library`'s value turned out to never be used for anything —see [`iterativeplan.md`](iterativeplan.md) → "Bumper catalog" for the full story.) Doesn't match or remove anything, and doesn't read the catalog back yet — see that same doc for what's still unbuilt(catalog-aware matching/"apply", curation, sub-bumper relationships, export/import).

```sh
dotnet run --project VBR.CLI -- add-bumper --clip-from "D:\Media\Show\S01E01.mkv" --region end --clip-length 8s --label "Studio ident" --catalog-db "my-bumpers.vbrcat"
```

Key options:

- `--label` (required, max 30 characters) — the one field you must supply yourself; no
  auto-suggestion from the filename/folder (considered and rejected during planning — too many
  edge cases to guess reliably, e.g. show name living in a grandparent folder, episode codes to
  strip). **Must be unique within the target `--catalog-db`, case-insensitively** — a duplicate
  is rejected before any sampling/extraction work starts; a different catalog may freely reuse the
  same label.
- `--description` (optional, max 255 characters) and `--tags` (optional, comma-separated) add
  curation context. All three (including `--label`) are enforced at the CLI, not the underlying
  data model.
- `--catalog-db <path>` — absolute or relative path to the catalog file, any extension
  (docs/iterativeplan.md, "File-path DB options" entry, Part 1 — replaced the old
  `--catalog-name`/`--catalog-db-folder` pair 2026-08-12, and dropped the requiredness that pair
  had). Default when omitted: `default.vbrcat` under a dedicated folder in VBR's own state folder
  (`%LOCALAPPDATA%\VideoBumperRemover\catalog\` on Windows) — a sibling of, not shared with, `vbr
  scan`'s own database folder.
- `--verbose` — same logging convention as every other command: model path, sampled/usable frame
  counts, and exact ffmpeg commands run, to the console and `log.txt`.
- `--hardware-accel` / `--no-native-ffmpeg-binding` — same shared GPU/native-decode options as
  `match` (see above).
- **Per-bumper overrides, stored on the new entry and read back by `match`/`remove` whenever this
  bumper is resolved via `--bumper-label`** (docs/iterativeplan.md, "Per-bumper matching strategy"
  entry, 2026-08-13) — all optional, all default to "inherit the global behavior," so a bumper added
  without any of these behaves exactly as before:
  - `--matching-strategy` (default `corroborated`) — which signal(s) must agree for *this* bumper to
    count as present, overriding `--detection-mode` outright when it's resolved (not intersecting
    with it): `corroborated` (today's behavior) / `visualonly` / `audioonly` / `phashonly` /
    `novisual` / `noaudio` / `nophash`. E.g. `audioonly` for a bumper visual detection can't reliably
    identify (thin/flowing motion graphics) but that has clear, distinguishing audio.
  - `--removal-length` — how much to actually cut on `remove`, when it differs from `--clip-length`
    (the region used to *identify* the bumper) — e.g. a cross-fade needing a few extra seconds
    stripped beyond what's needed to match reliably. Default: same as `--clip-length`.
  - `--presence-threshold-override` / `--rigid-hit-threshold-override` /
    `--phash-presence-threshold-override` / `--audio-min-similarity-override` (each `(0, 1]`) —
    per-bumper overrides of the matching global values in `vbr.config.json`, for a bumper whose real
    characteristics don't fit the same threshold every other bumper in the catalog uses (e.g. an
    audio veto that's too strict for one specific bumper's music/audio profile).
  - The `frameQuality` values active at the moment this bumper was sampled are captured
    automatically (no flag) as pure provenance — never read back to influence matching, only to let
    `match`/`remove`'s staleness warning compare against *this entry's own* recipe rather than the
    whole catalog's.

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
dotnet run --project VBR.CLI -- list-bumpers --catalog-db "my-bumpers.vbrcat"
```

Key options:

- `--catalog-db` — path to the catalog to list; same option, same `default.vbrcat`-under-state-folder
  default, as `add-bumper` (docs/iterativeplan.md, "File-path DB options" entry, Part 1).
- `--show-guids` — prints each bumper's `Id` on its own line immediately before that bumper's regular output line.

An empty or nonexistent catalog prints `Catalog '<name>' has no bumpers.` and exits 0, rather than erroring — same "a fresh catalog isn't an error" convention `add-bumper` already established.

**Verified live (2026-07-29)** against a scratch catalog with two real bumpers added via `add-bumper`: output matched the format above exactly, both thumbnails written as real, loadable JPEGs matching the byte counts `add-bumper` reported at capture time, and a label containing filesystem-unsafe characters (`:`, `/`) was sanitized in the thumbnail filename while still shown verbatim in the console line. Full numbers: [`iterativeplan.md`](iterativeplan.md).

### Test

```sh
dotnet test VBR.Tests
dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests"
```

`VBR.Tests/CLI/Commands/` (added 2026-08-17, docs/iterativeplan.md's "CLI test coverage" entry)
covers `MatchingSession`/`RemoveCommand`/`MatchCommand`/`TrimCommand`/`AddBumperCommand` — the layer
this week's real bugs (native-binding gating, Ctrl+C cancellation, sync-vs-async `SetAction`) all
lived in, previously with zero automated coverage. Every one of these is plain, always-on, in-memory
logic — no video/audio content, no files ever touching disk, no environment variables — so they run
identically on every machine in every `dotnet test` invocation, unlike the two paragraphs below.

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

### Publish a redistributable package — `publish-vbr-cli.ps1`

```powershell
pwsh -File publish-vbr-cli.ps1
```

Builds a self-contained `VBR.CLI` package for copying to another machine — originally for
benchmarking GPU/ONNX hardware acceleration across different hardware
([ADR 0013](decisions/0013-gpu-acceleration.md)); see
[ADR 0014](decisions/0014-vbr-cli-redistribution.md) for the decisions behind its defaults. It
only builds `VBR.CLI` and what its own `ProjectReference` chain pulls in
(`VBR.CLI` → `VBR.Core` → `VDF.Core`) — no other VDF/VBR artifact (`VDF.GUI`, `VDF.Web`, etc.).

Two layouts, for two different purposes:

- **`-Layout SingleFile` (default)** — one `.exe`, nothing else, for running `vbr` commands on
  another machine. Self-contained + `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`
  (~78MB on win-x64), no .NET install required on the target. `Microsoft.ML.OnnxRuntime.dll` and
  its native runtimes are bundled *inside* the exe (self-extracting to a temp cache at startup) —
  there's no loose `Microsoft.ML.OnnxRuntime.dll` on disk for `test-onnx-directml.ps1` to
  `Add-Type -Path` against, so this layout can't pair with that script.
- **`-Layout Loose`** — `VBR.CLI.exe` plus its managed DLLs sitting loose next to it (no
  bundling), for running [`test-onnx-directml.ps1`](#standalone-onnxdirectmlcuda-probe--test-onnx-directmlps1)
  on another machine. Also copies that script itself, plus this machine's already-downloaded `ai\`
  folder (model + whichever of the CPU/DirectML/CUDA runtimes are present locally) into the output
  root, so the pair is immediately self-sufficient on a target machine with no network access and
  no prior `vbr` run to trigger the normal auto-download.

```powershell
pwsh -File publish-vbr-cli.ps1                       # SingleFile -> .\publish\vbr-cli-win-x64\
pwsh -File publish-vbr-cli.ps1 -Layout Loose          # -> .\publish\vbr-cli-win-x64-loose\
pwsh -File publish-vbr-cli.ps1 -Rid win-arm64
pwsh -File publish-vbr-cli.ps1 -Zip                   # also produce a .zip of the output
pwsh -File publish-vbr-cli.ps1 -BundleFfmpeg -FfmpegPath "C:\ffmpeg\bin\ffmpeg.exe" -FfprobePath "C:\ffmpeg\bin\ffprobe.exe"
```

Key options:

- `-Rid` (default `win-x64`) — target runtime identifier.
- `-Aot` — Native AOT (smaller/faster-starting exe; SingleFile layout only, errors if combined
  with `-Layout Loose` since AOT has no loose managed DLLs left for `test-onnx-directml.ps1` to
  point at). Requires the Visual Studio "Desktop development with C++" workload (the platform
  linker; see <https://aka.ms/nativeaot-prerequisites>) — not installed on the machine this script
  was written on, which is why `SingleFile` (non-AOT) is the default rather than requiring it.
- `-BundleFfmpeg` (with optional `-FfmpegPath`/`-FfprobePath` overrides) — unlike VDF.GUI/VDF.Web,
  `VBR.CLI` never auto-downloads ffmpeg/ffprobe, only *locates* an existing one (next to the exe,
  in a `bin\` subfolder next to the exe, or on PATH). Off by default since it copies a third-party
  binary into the package; on for personal machine-to-machine copying, where it's your own
  already-licensed local install. Detects and warns on Chocolatey-style PATH shims (tiny redirect
  exes, ~390KB, that break if copied elsewhere since they locate the real binary relative to their
  own original location) rather than silently bundling a broken exe — pass `-FfmpegPath`/
  `-FfprobePath` explicitly pointing at the real binary in that case (e.g. Chocolatey's is under
  `C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin\`).
- `-Zip` — also produce a `.zip` of the output folder.
- `-OutputRoot` (default `.\publish`) — where the `vbr-cli-<rid>[-loose]\` output folder is
  created.

The AI/ONNX runtime, DirectML native libraries, and the DINOv2 model itself are **not** bundled
into the `SingleFile` layout — VBR downloads those into its own state folder on first run, same as
any other build, so each target machine needs network access once, the first time it runs any
AI-matching command. The `Loose` layout's `ai\` copy (above) is what avoids that requirement for
`test-onnx-directml.ps1` specifically.

### Standalone ONNX/DirectML/CUDA probe — `test-onnx-directml.ps1`

```powershell
.\test-onnx-directml.ps1 -Mode DirectML
```

A standalone diagnostic script for testing which ONNX Runtime execution providers actually work on
a given machine — independent of `vbr` itself, so it can be copied to another machine (see
`-Layout Loose` above) to test hardware before/without a full VBR install. Loads
`Microsoft.ML.OnnxRuntime.dll` directly via `Add-Type`/P/Invoke and runs real inference, reporting
whether the requested execution provider actually attached — not just whether the process didn't
crash (`OnnxEmbedder` itself catches DirectML/CUDA failures internally and silently falls back to
CPU with no exception, so a naive check here would be a false positive; see
[ADR 0013](decisions/0013-gpu-acceleration.md)'s live-verification notes).

```powershell
.\test-onnx-directml.ps1 -Mode Cpu
.\test-onnx-directml.ps1 -Mode DirectML -DeviceId 0
.\test-onnx-directml.ps1 -Mode Cuda -Iterations 20
```

Key options:

- `-Mode Cpu|DirectML|Cuda` — which execution provider to test.
- `-DeviceId` — GPU adapter index, for `DirectML`/`Cuda`.
- `-Iterations` (default a small handful) — how many inference passes to time.
- `-Root` (default `$PSScriptRoot`) — where to find the model/runtime `ai\` folder; matches what
  `-Layout Loose` above copies alongside the script.
- `-NativeFolder`/`-ManagedDllPath` — explicit overrides for the native runtime folder and the
  managed `Microsoft.ML.OnnxRuntime.dll` path, for a layout that doesn't match the script's
  defaults.

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
