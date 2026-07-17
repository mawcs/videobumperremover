# ADR 0002: Tech stack — C#/.NET, fork of VDF, Avalonia UI

- **Status:** accepted (stack decision stands); **throughput thesis partially superseded — see
  Amendment below**
- **Date:** 2026-07-14
- **Supersedes:** the open stack question in [0001](0001-tech-stack.md)

> **Amendment (2026-07-17) — "audio-first" is superseded as a *matching* priority.** This ADR was
> written before the matching spike. Its **stack decisions remain fully in force** (C#/.NET, fork
> of VDF, Avalonia, ONNX, SQLite, desktop-over-SMB). But its **"audio-first pass"** idea below
> reflected an early throughput assumption that audio would catch *most* bumpers cheaply. Validation
> disproved that for our targets: the bumpers this project removes are largely **silent** idents,
> which audio fingerprinting **cannot see**. The runtime matcher priority is now **visual DINOv2
> presence matching primary, audio a secondary accelerator for *audible* bumpers only** — see
> [`../design/matcher-spec.md`](../design/matcher-spec.md) (authoritative) and
> [`0006-edge-focused-fingerprinting.md`](0006-edge-focused-fingerprinting.md) (edge-focused
> sampling). Treat "audio-first" below as *historical throughput reasoning*, not a build directive:
> an audio prefilter would prune away exactly the silent bumpers we care about.

## Context

Decision drivers established during exploration:

- **Ship fast *and* run fast.** A quick build is worthless if it can't process the library
  in reasonable time.
- **Large library:** >~5TB, many thousands of files, stored on a **TrueNAS** server (no
  GPU), reached from a **Windows desktop with a GPU over SMB**.
- **Keep the ML door open** without committing to it now.
- **Mixed resolution + letterbox bars with burned-in studio text** must be normalized
  (see [`../research/matching-approaches.md`](../research/matching-approaches.md)).
- **Strong prior art:** Video Duplicate Finder (VDF, `0x90d/videoduplicatefinder`) already
  solves the near-identical problem at scale — ffmpeg-based frame extraction, 32×32
  grayscale fingerprints, matching across differing resolution/frame-rate/watermarks — and
  is split into a headless `VDF.Core` engine plus an Avalonia GUI. The maintainer has used
  VDF and likes it, and values code reuse.

## Decision

- **Language / runtime:** C# on modern .NET.
- **Starting point:** **fork Video Duplicate Finder.** Reuse `VDF.Core` as the matching
  engine base and extend it for bumper-specific needs (snippet localization, sub-bumper
  boundary growing, interstitial/mid-video matching, removal + audit).
- **UI:** **Avalonia** — fork VDF whole so there is a running end-to-end app on day one,
  then redesign the UI incrementally to fix known UX nitpicks. (WPF was considered; rejected
  because it would discard VDF's UI and require wiring a fresh front-end to `VDF.Core`. The
  one WPF advantage — slightly more mature video playback — is surmountable with LibVLCSharp
  on Avalonia.)
- **Video playback (verification UI):** LibVLCSharp for frame-accurate preview/scrubbing.
- **ML (door open — partly inherited, no sidecar):** VDF **already ships optional AI** —
  ONNX Runtime (MIT) plus a **DINOv2-small embedding model (Apache-2.0)** for visual
  similarity, downloaded on first use. So an embedding-based near-duplicate path is likely
  **inherited from the fork**, not merely left open. Any further ML runs via **ONNX Runtime
  in-process** with the CUDA execution provider on the desktop GPU (ONNX is a portable model
  format; ONNX Runtime is the C#-native engine that executes it). Because it's in-process,
  **no separate sidecar process is required.**
- **Index storage:** SQLite.
- **Deployment shape:** GPU **desktop application** reaching media over SMB. Revisit a
  desktop-GPU-worker + always-on-NAS hybrid later only if warranted.

## Throughput architecture (why "runs fast" holds on >5TB)

- **Network I/O over SMB is the likely real bottleneck**, not CPU/GPU. Design to move as
  few bytes as possible.
- **Audio-first pass:** fingerprint the (tiny, resolution/letterbox-immune) audio stream to
  catch most shared bumpers cheaply before touching video. *(Superseded as a matching priority —
  see the Amendment at the top. Audio is now a secondary accelerator; visual is primary because
  our target bumpers are often silent. Retained here as the original throughput reasoning.)*
- **Sparse frame sampling:** sample frames at a handful of positions with keyframe/input
  seeking (`-ss`) rather than fully decoding every file.
- **Parallel ffmpeg orchestration:** run many ffmpeg jobs concurrently; use `-progress`
  for progress, with cancellation and backpressure.
- **GPU where it pays:** reserve NVDEC (decode) / NVENC (encode) for heavy video-hash
  confirmations and re-encoded trims. Prefer stream-copy (`-c copy`) trims where
  frame accuracy allows — those are nearly free on any hardware.

## Consequences

Positive:

- Inherits VDF's proven, at-scale matching engine → large head start on the risky Phase 2
  spike; serves both "ship fast" and "runs well."
- C# gives mature ffmpeg wrappers, first-class ONNX/CUDA, and strong threading for job
  orchestration; ML needs no sidecar.
- Avalonia gives a running app immediately plus a designer-friendly (CSS-like) styling model
  for iterating on UX.

Negative / watch-outs:

- Avalonia's video integration is slightly less mature than WPF's; validate LibVLCSharp
  frame-accurate scrubbing early.
- C# is not the maintainer's favorite (acceptable; Java-adjacent and fine).
- Smaller Avalonia troubleshooting community than WPF.

## Follow-ups

- **VDF license — resolved:** VDF is **AGPLv3** (moved from GPLv3 in earlier versions). This
  project will be open source and adopts AGPLv3 accordingly. Note AGPL §13 (network-use =
  distribution) would only bite if this ever runs as a remote network service (e.g. a NAS
  web app); it's moot for a desktop app and satisfied by open-sourcing regardless. Bundled
  AI components carry their own permissive licenses (ONNX Runtime MIT, DINOv2-small
  Apache-2.0).
- Confirm the desktop's ffmpeg build has **NVDEC/NVENC** enabled.
- Measure **SMB throughput** (1GbE vs 10GbE) — it sizes every full-scan estimate.
- Prototype the **audio-first + sparse-sampling** pass on the Phase 0 test corpus to confirm
  the throughput model. *(Done — and the audio-first half was superseded; see the Amendment.
  Sparse sampling evolved into edge-focused variable-density sampling,
  [`0006-edge-focused-fingerprinting.md`](0006-edge-focused-fingerprinting.md).)*
