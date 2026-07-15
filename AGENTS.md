# Agent instructions

Instructions for automated agents (Claude, etc.) working in this repository.

## Project

Video Bumper Remover — detect, verify, and remove repeated bumpers/interstitials across a
large personal video library. Read [`README.md`](README.md) for the full problem statement
and [`docs/ROADMAP.md`](docs/ROADMAP.md) for the plan.

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
- **Why:** this repo lives on a Windows drive exposed to the agent sandbox through a FUSE
  file bridge. Ordinary file writes survive it, but git's temp-file + fsync + atomic-rename
  pattern (used for `config`, `index`, refs, and lockfiles) gets corrupted crossing that
  boundary, producing null/zero-byte files. The maintainer's native Windows git writes
  straight to NTFS and is unaffected. So: **agents edit files, the maintainer runs git.**

## Repo conventions

- Documentation lives in `docs/`. Research/exploration notes go in `docs/research/`.
- Keep `CLAUDE.md` as a one-line pointer to this file; put actual instructions here.
- Prefer small, focused commits with clear messages (made by the maintainer — see above).

## Safety around media & FFmpeg

- Prefer stream-copy trims (`-c copy`) where frame-accuracy allows; note when re-encoding
  is required and why.
- Always keep a manifest of what was cut from each file (source, snippet start/end, method)
  so removals can be audited and undone.
