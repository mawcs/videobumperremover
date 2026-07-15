# Video Bumper Remover

A tool to find, verify, and remove repeated "bumpers" (channel/studio promos, banners,
"coming soon" segments, mid-video interstitials) from a large personal video library.

> **This repository is a fork of [Video Duplicate Finder](https://github.com/0x90d/videoduplicatefinder)**
> (AGPLv3), whose matching engine we build on. VDF's original README is preserved at
> [`README.vdf.md`](README.vdf.md), and this project inherits its AGPLv3 license (see
> [`LICENSE`](LICENSE)).

## The problem

Many videos in the library carry bumpers at the start, the end, or both. They:

- Vary in length.
- Repeat across many videos (the same promo appears in dozens of files).
- Nest — a short bumper can be a **sub-bumper** embedded inside a longer one, so a 60s
  bumper can be misidentified as only 10s.
- Sometimes aren't at the edges at all, but sit mid-video like a commercial
  (**interstitials**).
- Appear across videos of **mixed resolution**, and some copies have **letterbox bars with
  studio text burned onto them** — both must be normalized for reliable matching.

Today the workflow is manual: watch each video, measure the bumper, sort it into a
trim bucket (e.g. a `front05sec` folder), and batch-trim with FFmpeg. It's slow and
error-prone.

## The goal

1. **Identify** a bumper in one video — its exact length and position.
2. **Fingerprint** that snippet and **scan the whole library** for videos containing the
   same snippet (edge or mid-video).
3. **Queue** the matches for removal.
4. **Preview & verify** the matches before anything is cut (guard against wrong matches
   and sub-bumper mistakes).
5. **Remove** the snippet from all confirmed videos.
6. **Review** processed videos to confirm the correct snippet was removed and nothing was
   over- or under-trimmed.

## Status

Early planning. **Stack decided:** C#/.NET, forking
[Video Duplicate Finder](https://github.com/0x90d/videoduplicatefinder) (reusing its
matching engine) with an Avalonia UI — see
[`docs/decisions/0002-tech-stack.md`](docs/decisions/0002-tech-stack.md). Phased plan:
[`docs/ROADMAP.md`](docs/ROADMAP.md). Prior art and terminology:
[`docs/research/prior-art.md`](docs/research/prior-art.md).

## Environment notes

- Videos live on a **TrueNAS** server (no GPU).
- A **desktop with a GPU** is available and can reach the files over SMB/Windows share.
- Deployment shape (GPU desktop app vs. Dockerized server service) is an open decision.

## Working with agents

See [`AGENTS.md`](AGENTS.md) for how automated agents should work in this repo.
