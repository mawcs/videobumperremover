# Research: prior art & terminology

Notes on existing work and the right vocabulary to search with. Short version: the concept
is real and well-studied — the term is **bumper**, and the broader category is
**interstitial**. No single off-the-shelf tool does exactly what we want (discover shared
bumpers across a mixed library and batch-remove them with verification), but several pieces
exist to borrow from.

## Terminology (you didn't make it up)

- **Bumper (broadcasting):** a brief (≈2–15s) announcement/ident placed between program and
  commercial break, often with signature music; can be simple text or a short film. This is
  exactly the word you were reaching for.
- **Interstitial:** the umbrella category — the "bits in between" programs (idents,
  trailers, bumpers). A bumper is a *type* of interstitial. Your mid-video "commercial-like"
  segments are interstitials too.

Better search terms to use going forward: *intro/outro detection*, *interstitial detection*,
*commercial detection*, *repeated segment mining*, *near-duplicate video detection*,
*video/audio fingerprinting*, *acoustic fingerprint repeated content*.

## Related tools & research

- **Video Duplicate Finder** (open source, C#/.NET, cross-platform, runs 100% locally) —
  detects duplicates and can find when a shorter video is a partial clip of a longer one via
  audio fingerprinting (Chromaprint-style chroma extraction + sliding-window Hamming
  similarity). Closest existing building block to our matching step.
- **Comskip** — commercial detector; analyzes video for cues like black frames. Good example
  of edge/interstitial detection, but it targets broadcast commercials, not arbitrary shared
  bumpers, and is heuristic rather than fingerprint-based.
- **audfprint / Chromaprint (AcoustID)** — audio fingerprinting toolkits usable to find
  repeated audio (bumper music/voiceover) across a library. Strong fit for the
  resolution/letterbox-robust first pass.
- **Intro/outro detection research** — classic approaches use cheap cues (silence gaps,
  blank-screen transitions, shot-boundary histograms) reporting ~76–82% detection with <~2s
  error. Newer deep-learning work extracts 1 FPS frames, encodes with CLIP, and uses a
  multihead-attention temporal model (reported F1 ≈ 0.91). Useful reference points for the
  auto-discovery approach.
- **Commercial detection via audio fingerprinting** (patents/literature) — the same "mine
  repeated audio across an archive" idea we sketch in
  [`matching-approaches.md`](matching-approaches.md) Approach 6.
- **Consumer batch tools** (Wondershare UniConverter's Intro & Outro, AutoCut) — only handle
  fixed-length, same-position trims; no cross-library discovery. Not sufficient, but confirm
  the demand.

## Takeaway

There's no drop-in tool for "auto-discover shared bumpers across a mixed-resolution library,
verify, and batch-remove." But the matching core (audio + perceptual-hash fingerprinting)
and the auto-discovery idea (repeated-segment mining, as used in commercial detection) are
both established. We'd be assembling proven parts around our specific workflow and safety/
verification needs rather than inventing the hard algorithms from scratch.

## Sources

- Bumper (broadcasting) — https://en.wikipedia.org/wiki/Bumper_(broadcasting)
- Interstitial television show — https://en.wikipedia.org/wiki/Interstitial_television_show
- Automatic Video Intro and Outro Detection on Internet Television — https://www.researchgate.net/publication/288472517_Automatic_Video_Intro_and_Outro_Detection_on_Internet_Television
- Automatic Detection of Intro and Credits in Video using CLIP and Multihead Attention — https://arxiv.org/html/2504.09738v1
- Video Duplicate Finder (0x90d) — https://github.com/0x90d/videoduplicatefinder
- video_fingerprinting (funzoneq) — https://github.com/funzoneq/video_fingerprinting
- Commercial detection based on audio fingerprinting (patent) — https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/9503781
- Python data analysis to detect/mute TV commercials — https://beepscore.com/website/2019/04/21/automatically-detecting-television-commercials.html
