# ADR 0003: Repository structure — fork VDF as the main repo

- **Status:** accepted
- **Date:** 2026-07-14
- **Related:** [0002](0002-tech-stack.md) (decision to fork Video Duplicate Finder)

## Context

The product will *be* a modified fork of Video Duplicate Finder (VDF); most real code work
happens inside that C#/Avalonia codebase. The initial planning repo (`videobumperremover`)
currently holds only meta/planning: `README.md`, `AGENTS.md`, `CLAUDE.md`, `docs/`
(roadmap, ADRs, research), and markdown-lint config. The question was how the planning repo
and the code fork relate.

Options considered: (1) make a real GitHub fork the main repo and migrate docs into it;
(2) `git subtree` VDF into a subdirectory of the planning repo; (3) `git submodule` the fork
under an umbrella repo; (4) keep two unlinked repos.

## Decision

**Make a real GitHub fork of `0x90d/videoduplicatefinder` the single canonical repo, and
migrate the planning docs into it.**

Rationale:

- The deliverable *is* the fork — co-locating code and plan matches reality and gives one
  source of truth.
- A proper GitHub fork preserves the upstream link, so VDF updates can be pulled via an
  `upstream` remote (VDF is actively maintained).
- Keeps the AGPLv3 lineage correct and visible.
- Supports contributing UI fixes back upstream: keep a clean branch off `upstream` for
  PRs to VDF, and the bumper divergence on the main branch.
- Submodule was rejected (fiddly for solo work; the umbrella would be a thin docs wrapper).
  Subtree was rejected (more manual upstream pulls) though it was the runner-up.

## Migration plan

1. **Fork on GitHub:** fork `0x90d/videoduplicatefinder`; optionally rename the fork to
   `videobumperremover`. Keep it as a fork (do not detach) to preserve the upstream link.
2. **Clone + wire upstream:**
   - `git clone <your-fork-url>`
   - `git remote add upstream https://github.com/0x90d/videoduplicatefinder.git`
   - Verify with `git remote -v` (origin = your fork, upstream = VDF).
3. **Migrate planning files** from the old repo into the fork: `docs/`, `AGENTS.md`,
   `CLAUDE.md`, and the markdown-lint config. **Merge** (do not overwrite) VDF's existing
   `.gitignore` with the media/artifact ignores.
4. **README + license:** keep VDF's `LICENSE` (AGPLv3, inherited). Rewrite `README.md` to
   describe this project while **crediting VDF** (preserve VDF's original README as
   `README.vdf.md` or a NOTICE, and add a "fork of Video Duplicate Finder" attribution).
5. **Branching:** work the bumper divergence on the default branch; create feature branches
   off `upstream`'s default branch for any fixes intended as upstream PRs.
6. **Retire the old planning repo** once migration is verified (archive or delete).

## Ongoing upstream sync

- `git fetch upstream`
- `git merge upstream/<default-branch>` (confirm the name with `git remote show upstream`;
  VDF currently uses `master`).
- Resolve conflicts (most likely in `README.md`/`.gitignore`, which we intentionally
  diverged).

## Consequences

- One upstream-pullable repo holding code + plan; clean AGPL lineage; upstream-PR path
  preserved.
- Cost: the planning repo's short history is left behind (docs are copied forward), and
  README/`.gitignore` will occasionally conflict on upstream merges — an accepted, minor
  price.
