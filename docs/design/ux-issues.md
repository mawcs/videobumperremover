# UX issues to fix (running list)

A running log of usability problems observed in VDF, kept so our redesign fixes them instead
of inheriting them. Many stem from VDF being a *duplicate-finder* (dedup jargon, whole-file
framing); our bumper-focused tool reframes several of these away entirely. Newest at the top;
each entry: **observation → why it matters → fix direction.**

## 2026-07-15 — seed entries (from hands-on evaluation)

### The primary visual pass silently starves the partial-clip pass

- **Observation:** partial-clip detection only considers files *not already grouped* by the
  primary whole-file visual compare. With the Matching similarity threshold set very low (1%),
  the visual pass grouped all 13 episodes, which excluded them from the partial pass — leaving
  a single ungrouped file, so partial detection reported "fewer than 2 eligible videos" and did
  nothing. Nothing indicated that one setting (visual threshold) had disabled a different
  feature (partial detection).
- **Why it matters:** two independent-looking settings interact invisibly; a reasonable value
  in one silently turns off the other. Very hard to diagnose without reading source.
- **Fix direction:** surface the interaction (warn when grouped files are being excluded from
  partial detection), or decouple the passes so partial detection can still consider grouped
  files, and explain eligibility counts in the results (“N of M files eligible; K excluded
  because already grouped”).
- **Recurred (2026-07-15), now starving *feature extraction*:** the audio pass grouped our test
  clip, so the AI visual pass never even **embedded** it (only the 13 ungrouped episodes got
  DINOv2 embeddings). **Architecture takeaway for our tool:** extract features (audio
  fingerprints + visual embeddings) for **every** file up front, *independent of grouping*;
  gate only at the match/report stage. Never let one pass's grouping starve another pass's
  extraction.

### Enabling a feature doesn't invalidate stale cache → silent no-op

- **Observation:** turning on Partial Clip Detection and rescanning a previously-scanned folder
  reused cached entries that had no audio fingerprint (the earlier scan didn't compute them).
  The feature silently skipped with "fewer than 2 eligible videos" — nothing prompted that a
  re-hash was required, and the hashing pass finished in 0.2s (all cache).
- **Why it matters:** the user reasonably believes the feature ran; it produced no results and
  no explanation, costing a long debugging detour.
- **Fix direction:** when a feature needs data the cache lacks, detect it and either
  auto-recompute the missing data or clearly tell the user "these files need re-fingerprinting
  — rescan/clear cache to enable." Never silently no-op.

### Toggle switches give no clear on/off signal

- **Observation:** the on/off toggles don't change color between states — only the knob
  position moves, and it's subtle. The only reliable tell that a toggle is *off* is that its
  dependent fields grey out. Both the maintainer and Claude misread the Partial Clip Detection
  toggle as off when it was on.
- **Why it matters:** users can't tell whether a critical feature is enabled, and run scans
  with the wrong configuration (we did — twice).
- **Fix direction:** strong on/off differentiation — distinct color/fill for the "on" track,
  an explicit On/Off (or checkbox) label, and clear disabled styling for the whole dependent
  group.

### Two different, easily-conflated "1%" settings

- **Observation:** *Matching → "Similarity threshold"* (whole-file visual compare) and
  *Partial clips → "Min clip / source ratio"* (audio partial detection) are both percentages on
  adjacent tabs. Setting one thinking it was the other produced a misleading whole-file result
  (16 equal-length episodes grouped at ~50%) with no partial-clip matches.
- **Why it matters:** the two knobs control completely different pipelines; conflating them
  wastes a full scan and produces confusing output.
- **Fix direction:** disambiguating names, group each knob visibly with the feature it drives,
  contextual help/examples, and (for us) surface only the settings relevant to the current
  mode/workflow.

### "Match %" column doesn't say *what* it matched

- **Observation:** the maintainer couldn't tell what "Match" was measuring. It's whole-file
  visual similarity to the group's reference file — but a row could also be an audio partial
  match or an AI match, and the column doesn't distinguish them.
- **Why it matters:** the number is meaningless without knowing the match *type* and what it's
  relative to; users can't judge whether a match is trustworthy.
- **Fix direction:** label the match *type* (visual / audio-partial / AI) per row, show what
  it's measured against, and (for us) center the UI on "this is bumper X, found at HH:MM:SS."

### "Wasted space" default sort is dedup jargon

- **Observation:** the default sort is "Wasted space" (disk reclaimable by deleting all-but-one
  in a group) — unclear on first encounter, and irrelevant to bumper removal.
- **Why it matters:** the default framing assumes a delete-duplicates goal that isn't ours.
- **Fix direction:** default sorts that match the actual task (e.g. by bumper, by occurrence
  count, by confidence), with plain-language labels.

### "Deeper" scans add matches but don't remove obvious false positives

- **Observation:** AI/partial passes are *additive* — enabling them never removes the earlier
  whole-file false positives (e.g. unrelated dark-scene matches), so a heavier scan can look
  like it "didn't fix" the junk.
- **Why it matters:** users expect a deeper/smarter scan to be *better*, not just *more*; the
  leftover false positives erode trust.
- **Fix direction:** show match provenance/confidence, let users filter or hide by match type,
  and make the additive nature explicit rather than surprising.
