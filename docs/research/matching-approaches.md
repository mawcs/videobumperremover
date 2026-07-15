# Research: snippet matching approaches

Exploring how to represent a bumper snippet so the *same* snippet can be found across the
library. This is the technical heart of the project. Nothing here is decided — it's a menu
of options with trade-offs to prototype in the Phase 2 spike.

The matching problem has three sub-parts, and different techniques suit each:

1. **Represent** a snippet as a fingerprint.
2. **Search** the library for that fingerprint (and locate its offset within each file).
3. **Determine the full extent** of a bumper (start + end) to avoid the sub-bumper trap.

## Approach 1 — Audio fingerprinting

Match on the audio track using landmark/constellation fingerprints (Chromaprint/AcoustID
style, or an audfprint-style approach).

- **Pros:** cheap, fast, and very robust to video re-encoding, resolution changes, and
  letterboxing. Bumpers frequently reuse *identical* audio, so this alone may catch most
  cases. Naturally gives a time offset (where the snippet occurs).
- **Cons:** fails when the same visual bumper has different/no audio, or when audio was
  re-recorded. Silent or music-bed-only bumpers may collide.
- **Good first bet** — likely the highest signal per unit of compute.

## Approach 2 — Perceptual video hashing

Compute per-frame perceptual hashes (pHash/dHash) and match sequences of frame hashes.

- **Pros:** works on the visual content directly; robust to re-encoding and mild scaling.
  Catches bumpers that share no audio.
- **Cons:** more compute than audio; sensitive to letterboxing/cropping and overlays
  (channel logos) unless normalized; frame-rate differences need alignment.
- Pairs well with GPU decode.

## Approach 3 — Combined audio + video

Use audio fingerprinting as a fast first pass to generate candidates, then confirm with
perceptual video hashing (and vice versa for audio-less bumpers).

- **Pros:** best precision/recall; each modality covers the other's blind spots.
- **Cons:** more to build and tune; two thresholds to manage.

## Approach 4 — Scene-boundary-assisted detection

Use shot/scene-change detection (e.g. FFmpeg scene filters or histogram cuts) to propose
candidate segment boundaries, then fingerprint between boundaries.

- **Pros:** helps find *where* a bumper starts/ends, which directly attacks the sub-bumper
  and interstitial problems.
- **Cons:** scene detection is imperfect; bumpers may not align to clean cuts.
- Best used as a helper for boundary-finding rather than the primary matcher.

## Approach 5 — Learned embeddings (later option)

Video/audio neural embeddings with nearest-neighbor search.

- **Pros:** most robust to variation; can catch near-duplicates.
- **Cons:** heaviest to build and run; likely overkill if cheaper methods suffice. Revisit
  only if Approaches 1–4 miss real cases.

## Approach 6 — Unsupervised repeated-segment mining (auto-discovery)

Instead of the maintainer identifying a bumper first, *discover* bumpers automatically by
finding segments that repeat across many files. This directly answers "can it find bumpers
without me pointing at one?"

- **How:** fingerprint every file into a stream of features (audio landmarks and/or frame
  hashes), then mine for **recurring subsequences** that appear in many otherwise-unrelated
  videos. A segment that shows up verbatim across dozens of files, especially near the head
  or tail, is almost certainly a bumper. Cluster the recurrences to propose candidate
  bumpers ranked by frequency and position.
- **Pros:** removes the manual identification step; surfaces bumpers you didn't know were
  shared; naturally measures a bumper's full extent (the recurring region's boundaries).
- **Cons:** heavier up-front indexing; needs a good threshold to avoid flagging common
  content (e.g. shared music); still wants human confirmation per cluster.
- This is essentially the same machinery as commercial-detection systems that mine repeated
  ad audio across a broadcast archive — see [`prior-art.md`](prior-art.md).

## Normalization: resolution, letterboxing & baked-in text

The library is **mixed resolution**, and some files have **letterboxed bars with studio
text burned into them**. Both break naive frame matching and must be handled before/inside
fingerprinting:

- **Resolution / aspect:** downscale every frame to a common working size before hashing so
  a 480p and a 1080p copy of the same bumper produce the same fingerprint. Perceptual hashes
  are somewhat scale-tolerant but explicit normalization is safer.
- **Letterbox detection & cropping:** detect black (or near-black) bars — FFmpeg's
  `cropdetect` filter is the standard tool — and crop to the active picture *before*
  hashing. This aligns a letterboxed copy with a full-frame copy of the same content.
- **Burned-in studio text on the bars:** text overlaid on the letterbox bars is a real
  hazard. If we crop the bars away we drop the text (good for matching the underlying
  video); but the *presence* of that text may itself mark a bumper, so keep it as an
  optional signal rather than always discarding it. Consider fingerprinting both the
  cropped active region and the full frame, and treat a match on either as a candidate.
- **Audio is unaffected** by all of the above, which is another reason to lean on audio
  fingerprinting as the resolution/letterbox-robust first pass.

## The sub-bumper problem

A short match may actually be a fragment of a longer bumper. Candidate mitigation to test:

- After finding a match, **grow the boundaries** outward and check whether the extended
  region *still* matches across all containing files. The true bumper is the largest region
  that agrees everywhere. This turns "how long is it?" into a measurable, automatable test
  rather than a guess.

## Interstitials (mid-video)

Matching must not assume snippets sit at the edges. The search should locate a snippet at
any offset, and the UI must clearly distinguish edge bumpers from mid-video interstitials,
since removal differs (front/end trim vs. split-and-concat).

## What to prototype first

Start with **Approach 1 (audio)** on the test corpus for cheap wins, add **Approach 2
(video hashing)** to cover audio-less cases, and use **Approach 4 (scene boundaries)** plus
the boundary-growing test to nail exact bumper extent. Measure precision/recall, re-encode
tolerance, and speed before committing to a design.
