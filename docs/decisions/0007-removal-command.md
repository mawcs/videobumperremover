# ADR 0007: The `remove` command — non-destructive output, re-encode-by-default, arithmetic cut points

- **Status:** accepted (core decisions below; re-encode algorithm/container/codec specifics are
  explicitly deferred — see Open questions)
- **Date:** 2026-07-19
- **Implementation status (2026-07-19):** `vbr remove` built and verified against real media —
  **stream-copy (`--re-encode false`) only.** Re-encode (`--re-encode true`, the *decided*
  default) is not implemented yet; the command errors clearly and tells the user to pass
  `--re-encode false` if it resolves to true. This split build order was the maintainer's
  explicit choice (stream-copy first, for faster iteration while testing) — it does not change
  the decision above; `--re-encode` still defaults to `true` in the CLI, matching the target
  design once Mode B lands.
- **Related:** [`../design/removal-pipeline.md`](../design/removal-pipeline.md) (Mode A/B
  mechanics), [`../design/bumper-catalog.md`](../design/bumper-catalog.md) (catalog duration
  fields), [`0004-bumper-catalog.md`](0004-bumper-catalog.md), [`0006-edge-focused-fingerprinting.md`](0006-edge-focused-fingerprinting.md),
  [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md) (duration-consistency finding),
  [`../ROADMAP.md`](../ROADMAP.md) Phase 5, [`../iterativeplan.md`](../iterativeplan.md) (the
  matcher-fix session this design discussion followed)

## Context

The visual matcher (validated, see `iterativeplan.md`) tells us a bumper is present in a
candidate file and gives an approximate offset (`bestCos@time`). Turning that into a precise cut
point was assumed to require **per-file boundary detection** — growing/searching for the exact
content→junk transition in each candidate. Design discussion concluded this is unnecessary:

- **Bumper duration is empirically constant.** The maintainer manually compared bumper
  boundaries across ~70 spot-checked videos in DaVinci Resolve — multiple studios, multiple
  bumper lengths, including personally-ripped DVD sources from before this project existed —
  and found them consistent to within **~0.02s**. This holds because a studio ident is a single
  rendered asset spliced in by the same pipeline every time; what varies across episodes (e.g.
  the Daredevil end-stack's 8–12s offset drift, see `vdf-evaluation.md`) is the *position*
  (credits-roll length before it), not the bumper's own duration.
- **Therefore the cut point is arithmetic, not searched:** given a precisely-known length,
  `cutPoint = fileDuration − length` (end region) or `cutPoint = length` (begin region). No
  per-file content-transition detection is needed. This **retires the "boundary-growing / edge
  detection" mechanism** previously described in `bumper-catalog.md`'s "Precision is the tool's
  job" section (updated by this ADR) and the "Boundary detection" open item in `AGENTS.md` /
  `PROGRESS.md` (also updated) — those described finding the transition per file; this
  supersedes that with a fixed-length arithmetic cut, with precision now front-loaded into
  *determining the length once*, not spent per file.
- **Precision now lives entirely in clip selection**, which is a UI/UX problem (a scrubber that
  assists frame-accurate boundary picking), not a matching or removal problem — explicitly
  deferred; not addressed by this ADR.
- **A separate, unrelated finding:** ffmpeg's stream-copy path does a poor job realigning
  subtitle cues when trimming (surfaced via a prior Claude Cowork investigation; the maintainer
  can do this manually with better results). This — not frame-accuracy alone — is why removal
  defaults to re-encode rather than stream-copy (see Decision 4 below). Re-encoding the video
  stream doesn't by itself fix subtitle timing (subtitle cues are typically stream-copied
  regardless of video codec decisions), but the re-encode path is where we commit to doing
  proper cue realignment as part of the pass.
- **Per AGENTS.md, originals are never modified/deleted without explicit confirmation.** This
  ADR's `remove` command satisfies that structurally (it only ever writes a new file); the
  actual "replace/delete the original" step is reserved for a separate, not-yet-built `cleanup`
  command, so `remove` itself carries no destructive risk requiring a confirmation gate.

## Decision

1. **New CLI command: `vbr remove`.** For this v1, it bundles clip extraction + matching +
   removal into a single invocation — the same scope `vbr match` already covers, plus the cut.
   It reuses `match`'s existing parameter surface unchanged: `--clip-from`, `--region`,
   `--clip-length`, `--search-length`, `--sample-interval`, `--presence-threshold`,
   `--rigid-hit-threshold`, `--min-similarity`, `--detection-mode`, `--library`, `--no-recurse`,
   `--output`, `--dump-frames`. Catalog-aware and index-aware variants (reuse a previously
   enrolled bumper; reuse a previously scanned library) are explicitly future work — new
   parameters will be **additive**, and per the standing "clip extraction is the tool's job"
   rule (AGENTS.md), any future clip-reuse mechanism must route through the catalog (once it
   exists) rather than accept a raw pre-cut clip file.

2. **One length, two jobs, no separate parameter.** `--clip-length` is reused as both (a) how
   much of `--clip-from` to extract as the match reference, and (b) the exact removal length
   (the arithmetic cut point above). No second "removal length" flag exists. This depends on
   the length being accurate at selection time — a UI/UX responsibility, not this command's.

3. **Non-destructive by default; sibling-file naming.** `remove` never modifies or deletes the
   source file. Output is written as a sibling file in the **same directory** as the source,
   named by inserting `.vbr.` before the original extension (e.g. `S01E01.mkv` →
   `S01E01.vbr.mkv`). This is the only supported output behavior for now. *(This supersedes
   `ROADMAP.md` Phase 5's earlier "write outputs to a staging area" language, updated
   accordingly.)* User-configurable output location/naming is explicitly deferred — noted as a
   real future need ("I will probably want to give the user an option..."), not designed here.
   **Rationale (maintainer):** co-located files are far easier to verify — select both and drop
   them into MPC/VLC for a quick side-by-side check — than hunting across a separate staging
   folder. A future option for separate folders/subfolders (as some batch ffmpeg front-ends
   offer) is planned but not built.

4. **A future `cleanup` command (name reserved, not built) will handle promoting or replacing
   originals with verified `.vbr.` outputs.** This is where "verification before destruction"
   is actually enforced. `remove` does not require a pre-cut confirmation gate — nothing
   destructive happens until `cleanup` runs.

5. **`--re-encode <true|false>`, default `true` when omitted.**
   - `true` (default) — Mode B re-encode (per `removal-pipeline.md`): frame-accurate cut point,
     plus proper subtitle cue realignment as part of the pass. **Exact codec/container/quality
     choices are explicitly deferred** ("future development") — v1 can start with a minimal,
     sane re-encode and grow the options surface later, per `removal-pipeline.md`'s existing
     output/transcode section.
   - `false` — Mode A stream-copy: fast, zero quality loss, but keyframe-bound cut points *and*
     unreliable subtitle cue realignment (the defect motivating the `true` default). Treated as
     an explicit, documented v1 UX exception/opt-out, not a recommended path.
   - This updates `removal-pipeline.md`'s framing: re-encode is now the default because of
     subtitle correctness, not merely a fallback for when optional enhancements are requested.

6. **A manifest recording each cut remains required**, per AGENTS.md's standing rule (source,
   matched offset, length used, re-encode mode, output path). Exact schema is deferred (Open
   questions).

## Consequences

Positive: no per-file boundary-search subsystem to build or validate; the cut mechanism is a
one-line arithmetic operation once a length is trusted; non-destructive output means `remove`
carries near-zero risk and needs no confirmation UX yet; re-encode-by-default sidesteps a known
ffmpeg subtitle defect up front instead of discovering it per-user later.

Negative / watch-outs: correctness now depends entirely on clip-selection precision (garbage
length in → garbage cut out, with no per-file check to catch it); re-encode-by-default means v1
`remove` is not the fast/lossless path by default, even though that path exists; sibling-file
output means the working `.vbr.` file sits live next to the original (a media server or casual
browsing could surface both) until `cleanup` exists to resolve that; the fixed-timeslot-DVD-padding
scenario (raised and set aside below) remains a theoretical gap if it's ever hit on unusual media.

## Implementation findings (2026-07-19, stream-copy build)

Two ffmpeg behaviors were verified empirically against real media (Daredevil S01E01's Netflix
ident) before trusting them in code — see `VBR.Core.Removal.ClipRemover`'s doc comments for the
full detail:

- **Begin-region seeking is safe in the "output seeking" direction** (`-ss` placed *after* `-i`):
  it snaps FORWARD to the next keyframe at or after the requested time — verified by seeking to
  5s (inside the Netflix card, which starts at 2.607s) and landing on the *next* ident (6.027s),
  never backward into the card. The kept segment can only start at or after the true boundary.
- **End-region cutting is NOT safe with naive `-t`/`-to`.** It overshoots the requested duration
  by a small, roughly constant amount (~0.2s observed, independent of the target — confirmed by
  targeting three different points and seeing the same overshoot each time) to complete the
  current frame-reorder buffer. Targeting an exact keyframe timestamp still overshot into that
  keyframe's own content. `ClipRemover` compensates by finding a keyframe **at least 1 full
  second before** the computed cut point (`EndCutOvershootSafetyMarginSeconds`), never the
  nearest one — trading a bit of extra trimmed content for the guarantee that no bumper content
  ever leaks into the kept output.

**A live end-to-end test surfaced the exact risk this ADR's Consequences section flagged**
("correctness now depends entirely on clip-selection precision"). The first test used
`--clip-length 10s` — the figure validated earlier in this project for *matching* the Daredevil
end-stack — and it left part of the stack (the "abc studios"/"MARVEL" cards) in the removed
output, because the true stack (DeKnight → Goddard → ABC Studios → Marvel → Netflix → black) is
~20.5s, not 10s. **A length sufficient to match is not necessarily sufficient to remove** — 10s
of a ~20.5s stack is more than enough distinctive content for the presence matcher to find, but
leaves the earlier ~10.5s of the stack sitting in the "kept" output. Corrected to 20.5s, the cut
landed cleanly before the stack (present=70/70, source untouched, output duration confirmed by
independent ffprobe). Separately, a begin-region test against the ~5s Netflix ident removed
*exactly* 5.000s and landed precisely at the boundary with a **second, separate** intro sequence
(a Marvel comic-page animation) that follows it — correct behavior, not a defect: that second
sequence is a distinct bumper requiring its own catalog entry/invocation, not part of the one
just measured.

## Open questions

- **Manifest schema** — fields, file format (JSON vs. text), and location are not yet specified.
- **Re-encode algorithm specifics** — codec choice, container handling, CRF/bitrate defaults,
  GPU (NVENC) vs. CPU encode. Deferred per this ADR's Decision 5.
- **`cleanup` command design** — verification gate, replace-vs-delete-original policy, backup
  retention. Not scoped here.
- **Catalog/index-aware `remove` parameters** — additive; must preserve the no-pre-cut-clip rule.
- **User-configurable output naming/location** — sibling `.vbr.` suffix is the only supported
  behavior for now; staging directory or custom pattern are real future options, not designed.
- **Optional lightweight post-cut sanity check** — verify (via the existing presence matcher, at
  just the one frame on each side of the computed cut point) that the kept side isn't bumper
  content and the removed side is. Proposed during design discussion as a cheap alternative to
  full boundary detection; not required, not yet built.
- **Fixed-timeslot DVD padding** (bumper region padded to a fixed episode runtime independent of
  the ident itself, which would break the constant-duration assumption) — raised and explicitly
  set aside: not observed across the maintainer's spot-check corpus, which included personally
  ripped DVD sources. Revisit only if evidence emerges.
