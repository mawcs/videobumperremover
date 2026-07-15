# ADR 0001: Platform shape and tech stack

- **Status:** superseded by [0002](0002-tech-stack.md)
- **Date:** 2026-07-14

## Context

The tool needs to fingerprint video/audio snippets, scan a large library for matches,
present a verification UI, and drive FFmpeg to trim files. Two hard constraints shape the
choice:

- Videos live on a **TrueNAS** server with **no GPU**.
- A **desktop with a GPU** exists and can reach the media over **SMB/Windows share**.

The platform shape (where the tool runs, and whether it has a UI) must be decided *before*
the language/framework, because it constrains both.

Maintainer preference: **Python is disfavored** and should not be assumed. It will only be
used if explicitly approved for a specific task.

## Options

### A. GPU desktop application

Runs on the GPU desktop, reads media over SMB. GPU is local, so decode/feature extraction
and any re-encoding are fast. Simplest path to rich local video playback in the UI.
Trade-off: tied to one machine; SMB throughput becomes the I/O ceiling for scans.

### B. Dockerized service on TrueNAS

Runs where the files are (no network hop for I/O). Portable and always-on. Trade-off: no
GPU on the server, so fingerprinting/re-encoding are CPU-bound and slower; video preview in
a browser UI is more awkward.

### C. Hybrid: headless engine + thin UI

A headless detection/removal engine (could run on either box) plus a separate thin client
for verification. Most flexible and lets the GPU box do heavy compute while the UI stays
light. Trade-off: more moving parts and an API boundary to design.

## Decision drivers

- Where the GPU actually pays off (decode, feature extraction, re-encode).
- SMB throughput vs. local disk for full-library scans.
- Quality of in-UI video preview/playback (central to verification).
- Maintainer's language/framework preferences (not Python).
- Always-on vs. run-when-needed.

## Status / next step

Undecided. Resolve the Phase 0 open questions (library size, codecs, throughput) first,
then pick a platform shape here, then choose the language/framework and record it (either
by extending this ADR or adding ADR 0002). Tracked as an active project task.
