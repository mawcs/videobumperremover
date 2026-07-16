# Glossary — fingerprinting, hashing & embeddings

Plain-language overview of the matching concepts this project uses. Not deep — just enough to
follow the design and the probes. Ordered roughly from general to specific.

## Fingerprint

The umbrella idea: reduce something big (a song, a video frame) to a small, comparable
**signature** that survives re-encoding, resizing, etc. Like a human fingerprint — tiny, but it
identifies the whole. Everything below is a *kind* of fingerprint.

## Audio fingerprint (Chromaprint)

Turns a sound into a sequence of compact codes describing which musical pitches are present over
time. Same sound → same codes, even after re-encoding. This is our **audio matcher**.
**Blind spot:** if the audio differs between copies (e.g. an end-card with fading music on some
episodes, silence on others), the codes differ and it's useless.

## Perceptual hash (pHash)

A fingerprint for a *single image*. Shrink the frame to a tiny grayscale thumbnail, capture its
overall light/dark structure as a short code. Similar-looking frames → similar codes. Cheap and
robust to resize, but crude — its blind spot is "all dark frames look alike" (the false positives
we saw early on).

## Embedding (the AI / DINOv2 part)

The smart version. A neural network *looks at* a frame and outputs a list of ~384 numbers (a
**vector**) that captures **what the image is**, semantically — not just its pixels. Two frames of
the same logo get near-identical vectors even if one is cropped, zoomed, letterboxed, or
recolored. This is our **visual matcher**, and it's why resolution/letterbox differences don't
fool it. The embedding *is* the frame's fingerprint — just far richer than a pHash.

## Cosine similarity

How you compare two embedding vectors. Picture each vector as an arrow in space; cosine measures
the **angle** between two arrows. **1.0 (0°) = same direction = same content**; lower = more
different. This is literally the `bestCos` number in our visual probe — "how identical is the
clip's frame to the closest frame in the episode." Rough feel: ~0.95+ = same thing; ~0.4–0.5 =
unrelated.

## Hamming distance

The comparison method for *bit-based* fingerprints (audio, pHash). Just count how many bits differ
between two codes; fewer = more alike. Our audio matcher slides the clip's code along the file and
finds the position with the fewest differing bits (the best-aligned spot).

## Quantization (int8)

A space-saver. Embedding numbers are precise decimals; quantizing squishes each into a small whole
number (−127…127). Tiny accuracy loss, ~4× less storage. That's the "Quantized" in
`EmbedBatchQuantized` — compression, nothing conceptual.

## The three matchers in this project

| Matcher | Signal | How it decides | Best at | Blind spot |
|--------|--------|----------------|---------|-----------|
| **Audio** | Chromaprint fingerprint | sliding-window Hamming (best-aligned lowest difference) | audible bumpers, long clips | silent / varying-audio bumpers |
| **VDF visual (rigid)** | DINOv2 embeddings | ≥4 *distinct* frames agree on one time offset | a moving clip inside a longer file | short / near-static bumpers (too few distinct frames) |
| **Presence (ours)** | DINOv2 embeddings | is any distinctive frame present at high cosine? (no temporal rule) | short bumpers | needs a good cosine threshold to avoid false positives |

## Handy mental models

- **Audio vs. visual:** audio matches *what it sounds like*; visual embeddings match *what it
  looks like*. Some junk bumpers have neither reliable audio nor motion — those are the hard ones.
- **Why short clips are hard:** most matchers need several *distinct* frames or enough fingerprint
  bits. A 3s static logo gives almost no variety, so temporal matchers starve — hence the
  "presence" approach (find one distinctive frame), and positional windows (search only the edges,
  which shrinks the false-positive floor).
- **Offsets:** when a matcher reports an offset, it's *where* in the target the clip was found —
  which is exactly the cut point for removal.
