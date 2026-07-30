# ADR 0012: Removal re-encode defaults — match output codec to source codec

- **Status:** accepted (decision only) — **not yet implemented.** `ClipRemover`'s re-encode path
  still uses the fixed `libx264 CRF 18 preset medium` placeholder ADR 0007 shipped with; this ADR
  is the design to build against, next.
- **Date:** 2026-07-27 (retroactively promoted to an ADR 2026-07-30 — see the note on
  [ADR 0009](0009-library-scan-database.md), same situation)
- **Related:** [`0007-removal-command.md`](0007-removal-command.md) (Open Questions already
  pointed here, but never incorporated the decision), [`../design/removal-pipeline.md`](../design/removal-pipeline.md)
  (updated encoding-defaults section), [`../iterativeplan.md`](../iterativeplan.md) → "Removal
  re-encode defaults" (full reasoning, the codec/CRF table, and the HDR analysis this ADR
  summarizes)

## Context

The maintainer's own manual `vbr remove` testing found default re-encode output running **2-3x**
the source file size — large enough on a real library to matter. Root cause, confirmed against
real files: `ClipRemover` forces every source through a fixed `libx264 CRF 18 preset medium`
regardless of what the source actually is (already flagged in that file's own doc comment as "not
a considered choice," and listed as an explicit open item in ADR 0007). CRF 18 alone is
deliberately large ("visually near-lossless"). The bigger factor specifically for this
maintainer's library: a real codec mix confirmed via `ffprobe` — some content is H.264, but the
broader library skews H.265/AV1, some from 10-bit/HDR sources. Re-encoding an HEVC or AV1 source to
H.264 at a near-lossless CRF stacks a codec-family bitrate penalty (HEVC needs roughly half AVC's
bitrate for equivalent quality; AV1 typically needs less again) on top of an already-generous
quality target — a fully expected, non-buggy cause of the 2-3x growth.

## Decision

1. **Match the output codec/bit-depth to the source; don't build a general transcoder.** Per the
   maintainer's own framing: either the library owner already tuned their rip's format carefully,
   or it's as-extracted and this project's job is not to replace HandBrake — matching what's
   already there is the sensible default either way, for both codec and bit depth.

   | Source codec | Output encoder | CRF | Confidence |
   | --- | --- | --- | --- |
   | H.264 | `libx264` | 22 | Solid — matches HandBrake's own default |
   | H.265/HEVC | `libx265` | 24 | Solid — matches HandBrake's own default |
   | VP9 | `libvpx-vp9` | 31 | First-class, at the maintainer's request (a real VP9-heavy library from YouTube downloads exists in the wild) |
   | AV1 | *(deferred — see below)* | — | Not built this pass |
   | Anything else (MPEG-2, VC-1, XviD, etc.) | `libx264` | 22 | Universal fallback — matches today's existing default, so an old/unrecognized source doesn't break |

   **The CRF-scale trap, worth recording explicitly** (this cost real re-encodes before being
   caught): CRF is not a shared scale across encoders — a given CRF number means a different
   quality target in different encoders. HandBrake itself uses CRF 22 for H.264 and CRF 24 for
   HEVC, not the same number, for exactly this reason; the table above deliberately mirrors that
   precedent rather than inventing new numbers. The same trap applies to **presets**: x264/x265
   use named presets (`slow`, `medium`, ...) where "slower" is unambiguous, but SVT-AV1 uses
   numeric presets 0-13 where *lower* is slower/higher-quality — the opposite direction "slow"
   suggests. Any future AV1 work needs its own preset value, never a reused string or an
   assumed-equivalent number.

2. **AV1 is explicitly deferred, not "unsupported."** AV1's CRF scale (0-63) is genuinely less
   standardized than x264/x265's — `libaom-av1` and `libsvtav1` don't agree with each other at the
   same CRF number. Recommended when this is picked back up: empirically test 2-3 real AV1 samples
   at candidate CRF/preset values (size + eyeballed quality) rather than trust a number from
   general lore. **Encoder availability is a real risk, not just a quality question:**
   `libsvtav1` (the fast, modern encoder most tools now prefer) must be compiled into the user's
   ffmpeg build (`--enable-libsvtav1`) — not guaranteed present; `libaom-av1` (the reference
   encoder) is believed to be in default ffmpeg builds more reliably, making it a safer universal
   baseline; a runtime check (`ffmpeg -encoders`) with fallback from `libsvtav1`→`libaom-av1`→
   `libx264` (with a warning) is the recommended shape, not yet built. **Until AV1 support exists,
   an AV1 source falls through to the generic fallback row** (`libx264` CRF 22) — a known size
   regression for AV1 sources specifically, accepted until AV1 support is actually built. Whether
   this fallback path should print an explicit warning is not yet decided.

3. **HDR handling: preserve what can be confidently preserved; refuse or warn rather than silently
   downgrade what can't.** Matching `pix_fmt` (8-bit vs. 10-bit) is straightforward — same
   probe-and-mirror mechanism as codec-matching. But HDR needs more than bit depth: color metadata
   (`color_primaries`/`color_trc`/`colorspace`) must be explicitly carried through as output flags
   (skipping this can produce a technically-10-bit output a player displays as SDR — arguably worse
   than a clean 8-bit SDR encode, not just "not as good"); HDR10's mastering-display/content-light-
   level metadata (`-master_display`/`-max_cll`) is extractable via ffprobe's `side_data_list` and
   re-injectable via ffmpeg. **Dolby Vision is a separate, harder case** — its RPU metadata isn't
   preserved by a standard re-encode pipeline at all; proper handling needs external tooling (e.g.
   `dovi_tool`) to extract and reinject it around the encode, out of scope for this pass. Decision:
   detect what can be detected, preserve HDR10-style color + mastering-display metadata with
   confidence, and explicitly refuse or warn rather than silently produce a corrupted-looking
   Dolby Vision output. **Needs real empirical verification before shipping** (encode a real HDR
   sample, inspect the output's metadata via ffprobe, confirm a player actually reads it as HDR) —
   not just flag-passing that looks right on paper. Not yet done.

4. **No user-facing configuration in v1.** No CLI flags, no config file, no named presets
   (`slow`/`fast`/`HQ` etc. were considered and explicitly rejected). The only escape hatch remains
   `--re-encode false` (stream-copy) — a different trade-off entirely (keyframe-bound cuts, no
   frame accuracy), not a size/quality knob. A config file for user overrides (codec/container/CRF)
   was discussed and explicitly deferred for the same "not replacing HandBrake" reasoning.

5. **Preset for the baked-in defaults — recommended, not yet a confirmed decision.** Re-encode is
   already the deliberately-slow, frame-accurate path, and with no user override the one preset
   value picked matters more than a "default among options" normally would. Leaning toward `slow`
   over `medium` for the x264/x265 rows (better compression at the same CRF) — not finalized; VP9's
   own speed mechanism (`-deadline`/`-cpu-used`) still needs its own value picked separately.

## Consequences

Positive: directly fixes the maintainer's own observed 2-3x bloat for the H.264 and HEVC cases
(the bulk of most real libraries); matches this project's stated "not replacing HandBrake"
philosophy by respecting a source's own already-chosen format rather than imposing one codec on
everything.

Negative / watch-outs: still no user override for someone who genuinely wants a different
size/quality trade-off, or GPU encoding, in v1; AV1 sources get no improvement from this decision
at all (same fallback behavior as before, now just an explicitly-acknowledged gap rather than an
unexamined one); the HDR preservation claims are unverified until the empirical pass happens — a
real risk if shipped before that verification, since a subtly-wrong HDR flag combination could be
worse than no HDR handling at all (silently-wrong metadata vs. an honestly-SDR fallback).

## Open questions

- **GPU (NVENC) vs. CPU encode** — not addressed by this ADR at all; a real future lever for
  re-encode throughput, noted but not designed.
- **Whether VBR should bundle its own ffmpeg** (guaranteeing `libsvtav1` and known-good encoder
  availability generally) rather than relying on the user's system ffmpeg — raised, not
  investigated or decided.
- **Container handling** — fully open, not addressed.
- **The exact preset values** (`slow` vs. `medium` for x264/x265; VP9's `-deadline`/`-cpu-used`) —
  leaning but not finalized, per Decision 5.
- **Whether the AV1-falls-through-to-libx264 path should warn explicitly** — pending until AV1
  support itself is built.
