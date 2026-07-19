# Design note: the removal pipeline (and how enhancements compose)

How the removal engine (ROADMAP Phase 5) actually cuts segments, and why the optional
per-video enhancements (Phase 8) share the same pass. Answers the question: "removing bumpers
from the front, end, and middle — is that a multi-pass ffmpeg job?"

**2026-07-19 update — re-encode is now the *default*, not just the enhancement fallback.** See
[ADR 0007](../decisions/0007-removal-command.md). Investigation found ffmpeg's stream-copy path
does a poor job realigning subtitle cues when trimming; re-encoding the video doesn't by itself
fix this (subtitle streams are typically copied regardless of video codec choice), but the
re-encode pass is where proper cue realignment is committed to happening. `vbr remove` therefore
defaults `--re-encode` to `true` (Mode B below); Mode A (stream-copy) remains available via
`--re-encode false` as an explicit, documented v1 exception, not a recommended default.

## Short answer

It depends on stream-copy vs. re-encode:

- **Lossless stream-copy** (`-c copy`): removing multiple segments is **multi-step** — extract
  each *keep*-segment, then concatenate them. Fast and no quality loss, **but** cuts can only
  land on keyframes, so they aren't frame-accurate unless the cut happens to fall on one.
- **Re-encode:** it's a **single ffmpeg run** — a `filter_complex` graph trims each keep-segment
  and `concat`s them in one pass, frame-accurately.

## Mode A — Lossless stream-copy (`--re-encode false`, opt-in exception)

- Extract keep-segments with `-c copy`, then join with the **concat demuxer** (only needed for
  multi-segment/interstitial removal — a single edge cut, front *or* end, is one `-c copy`
  extraction, no concat required).
- Pros: fast, zero quality loss, low CPU/GPU.
- Cons: **keyframe-bound cut points** → not frame-accurate mid-GOP; audio/video sync and
  timestamp continuity need care at joins; **unreliable subtitle cue realignment** (see the
  2026-07-19 update above) — the reason this is no longer the default.
- **Smart-cut** option: re-encode only the boundary GOPs around each cut and stream-copy the
  rest, to get frame accuracy without re-encoding the whole file. Real but fiddly/fragile;
  treat as an enhancement to Mode A, not the default.
- Note: for an **end-region** bumper specifically, no subtitle cue shifting is needed at all —
  nothing before the cut point moves. Begin-region and middle removal are where cues must shift,
  independent of Mode A/B — see the "Chapters, scenes & timestamps" section below.

## Mode B — Single re-encode pass (`--re-encode true`, the default; required for any enhancement)

- One ffmpeg invocation: `filter_complex` with `trim`+`setpts` (video) and `atrim`+`asetpts`
  (audio) for each keep-segment, feeding a `concat` filter (`n=<segments>, v=1, a=1`).
- Removes the front bumper, end bumper, and every interstitial together in **one pass**.
- Frame-accurate cut points.
- Cost: full re-encode (use NVENC/NVDEC on the GPU desktop to keep it fast); some quality loss
  inherent to re-encoding.

## Why enhancements compose into Mode B

Every optional per-video enhancement is a **filter that already requires re-encoding**
(aspect fix, letterbox/text crop, deinterlace, flip, logo/text removal, frame interpolation).
So the moment a user enables *any* of them, we're in Mode B anyway — and the enhancement
filters simply join the same `filter_complex` graph as the trim/concat. This means:

- N segments + M enhancements ≠ N+M passes. It's **one** re-encode pass with a composed graph.
- The engine should be built as a **filter-graph builder**: given the keep-segments and the
  set of enabled enhancements, emit one ffmpeg command. Choose Mode A (stream-copy) only when
  no enhancement is requested *and* the user prefers lossless.

Typical composition order: apply per-frame enhancement filters (deinterlace → crop → scale/
aspect → flip → delogo → interpolate) to the source, then trim the keep-segments, then concat.
(Exact ordering is a tuning detail; deinterlace generally goes first, interpolation last.)

## Chapters, scenes & timestamps

- Cutting shifts every downstream timestamp, so any existing **chapter/scene marks must be
  recomputed** relative to the new timeline (and marks that fell inside a removed segment
  dropped).
- Chapter/scene **marking** (Phase 8) is metadata, not a filter — written via container
  metadata, independent of the trim, but computed against the *post-cut* timeline.

## Output & transcode options

Since Mode B already re-encodes, the *output encoding* is a free choice to expose there. One
important split:

- **Container change is a remux, not a re-encode.** Changing mp4↔mkv by itself is stream-copy
  (`-c copy` into a new container) and is therefore available even in **Mode A** — nearly free.
  Caveat: containers differ in what they accept — subtitle formats especially (mp4's `mov_text`
  vs. mkv's SRT/ASS), and some codecs aren't legal in mp4. A remux may need subtitle conversion
  or will otherwise fail.
- **Codec change and quality/size optimization are re-encodes** → **Mode B only**, composed
  into the same pass as removal + enhancements at little extra cost.

Encoding knobs worth exposing (kept focused, not a full transcoder — see scope note):

- **Codec:** H.264 → **H.265/HEVC** (smaller at equal quality, slower, some playback-compat
  concerns) or **AV1** (better still, much slower; SVT-AV1 or NVENC-AV1 on newer GPUs;
  compatibility still maturing). Preserve **10-bit/HDR** where present.
- **Quality/size target:** **CRF** (quality-targeted, variable size) vs. **bitrate** (size-
  targeted). Encoder **preset** trades speed for efficiency. Note the **GPU vs CPU** tradeoff:
  NVENC is fast but lower quality-per-bitrate than CPU x265/x264 at slow presets — offer both.
- **Audio:** passthrough (`-c:a copy`) vs. re-encode; preserve multichannel.
- **Preserve** subtitles, chapters, and metadata across container/codec changes.

**Scope guardrail:** we are not rebuilding HandBrake. Because we re-encode during removal
anyway, a *focused* set of sensible output options is a low-cost, high-value add — but resist
growing this into a general-purpose transcoding UI.

> **Parked future idea:** a friendlier, standalone transcoding tool (HandBrake's UI is
> notoriously hard to explain to non-technical users) could be a worthwhile *separate* project
> someday, reusing this engine. Explicitly out of scope for now — noted so it isn't lost.

## Implications for the engine

- Build a filter-graph builder that composes trim/concat + enhancements into one command.
- Keep a clean **Mode A (lossless) vs Mode B (re-encode)** switch, exposed as `--re-encode`
  (default `true` → Mode B, per [ADR 0007](../decisions/0007-removal-command.md)). Mode A is an
  explicit opt-out (`--re-encode false`), not the default — subtitle cue realignment reliability
  now outweighs the speed/quality-preservation case for defaulting to stream-copy.
- Record the exact ffmpeg command per file in the **removal manifest** (audit + reproducibility
  + undo), alongside the catalog entry that triggered the cut.
- GPU: Mode B is the decode/encode-heavy path — this is where NVDEC/NVENC earn their keep.
