# ADR 0015: Native FFmpeg binding for scanning/sampling

- **Status:** accepted, implemented and live-verified (2026-08-03). `SampleFrames` (the dense,
  closely-spaced case) has real native decode wired in with CLI fallback; `SampleKeyframes` was
  deliberately left CLI-only during implementation (see "Live-verified" below — not the plan's
  original assumption). A real bug was found and fixed during live verification (a double-EOF-flush
  crash-to-fallback on every file's tail end) — see that section for details. **Important caveat
  found during verification, not anticipated in the original plan: most typical ffmpeg installs
  (static builds — e.g. Chocolatey's, this project's own dev machine) have no shared libraries at
  all, so native decode silently never activates for them out of the box** — see "Open questions."
- **Date:** 2026-08-02 (design finalized 2026-08-03, implemented and live-verified 2026-08-03)
- **Related:** [`0006-edge-focused-fingerprinting.md`](0006-edge-focused-fingerprinting.md) (the
  sampling strategy this would accelerate, not replace), [`0013-gpu-acceleration.md`](0013-gpu-acceleration.md)
  (ffmpeg `-hwaccel` decode acceleration — a different, already-shipped lever this is additive to,
  not a replacement for), [`0005-code-organization.md`](0005-code-organization.md) (the
  `InternalsVisibleTo` grant this decision's reuse depends on)

## Context

Comparing VDF's own GUI settings against VBR's scanning/matching pipeline surfaced a real,
already-built optimization VBR has never adopted: VDF.Core ships a native FFmpeg binding
(`FFmpeg.AutoGen` calling `libavformat`/`libavcodec` directly, in-process) that decodes frames
without ever spawning `ffmpeg.exe`. VDF's own settings text states this plainly: *"For scan speed
this is usually the biggest win, bigger than GPU decoding."* Confirmed live via the maintainer's
own VDF.GUI screenshots (2026-08-02) — VDF exposes this as "Use native FFmpeg binding," a toggle
gating a real, already-shipped, production-hardened feature, not a new idea.

VBR's own sampling code (`VBR.Core/Fingerprinting/DenseFrameSampler.cs` — the single choke point
every sampler in this project funnels through: `MixedDensitySampler`, `WholeFileSampler`, and
`VisualBumperMatcher` indirectly) exclusively spawns `ffmpeg.exe` via `ProcessStartInfo` for every
sampling call. Confirmed via grep: zero `FFmpeg.AutoGen` references anywhere in `VBR.Core`. Since
`VDF.Core` (which already contains this native binding) is already a direct project dependency of
`VBR.Core` — not a separate application requiring integration — adopting it is reuse of code
already present in this project's own dependency tree, not a forklift from an unrelated codebase.

This ADR is being written *before* implementation, at the maintainer's explicit request, so the
scope and design tradeoffs are agreed before any code changes — matching this project's own
established norm (see `AGENTS.md`) of capturing a durable, broad decision as an ADR rather than
letting it accumulate implicitly across commits.

## Decision (proposed)

1. **Adopt VDF.Core's existing native FFmpeg binding for VBR's decode-only sampling paths.** The
   relevant building blocks already exist and are production-hardened in VDF: `VideoStreamDecoder`
   (open + seek + decode via `libavformat`/`libavcodec`, with keyframe-seek fallback, bad-packet
   tolerance, still-image draining, and a per-call interrupt timeout), `VideoFrameConverter` (a
   `sws_scale` wrapper for pixel format/size conversion, e.g. to 224×224 RGB24 for
   `OnnxEmbedder.InputSide`), and `FfmpegEngine`'s orchestration layer (`UseNativeBinding` master
   toggle, a per-scan session circuit breaker that disables native decode after 5 consecutive
   per-file failures and falls back to the CLI process path, `GetConfiguredHardwareDeviceType()`
   mapping `FFHardwareAccelerationMode` → `AVHWDeviceType` for native hardware decode via
   `av_hwdevice_ctx_create`, and an explicit Vulkan guard against a known native-path crash,
   issue #799). `FfmpegEngine` is `internal` in VDF.Core; `VBR.Core` already has compiler-level
   access via the existing `InternalsVisibleTo` grant (ADR 0005) — no new bridge mechanism is
   needed beyond extending the existing `HardwareAcceleration` public bridge class VBR.CLI already
   uses to reach VDF.Core's other `internal` GPU-related state.

2. **Scope is decode-only: `DenseFrameSampler`'s two internal sampling methods
   (`SampleFrames`/`SampleKeyframes`), nothing else.** VDF's native binding never writes an output
   video file — every native call reads frames for in-process analysis (hashing, embedding) only.
   `ClipExtractor` (writes a real `.mkv` for the bumper catalog, via stream-copy or re-encode) and
   `ClipRemover` (writes the actual `.vbr.` cut output) both mux/encode real files — outside this
   native path's scope entirely, and stay on the `ffmpeg.exe` CLI path unconditionally. This is a
   deliberate boundary, not an oversight left for a future pass.

3. **Not a uniform swap — the two `DenseFrameSampler` methods need different treatment.**
   `SampleKeyframes` (the whole-file sparse pass `WholeFileSampler` uses, keyframe-only decode at a
   multi-second interval) looked architecturally near-identical to VDF's own already-built
   `FfmpegEngine.GetDenseAiFrames`, suggesting a close-to-direct reuse. **Live-verified 2026-08-03
   this assumption was wrong**: `GetDenseAiFrames` turns out to be CLI-only even with native
   binding enabled, a deliberate VDF choice (one `ffmpeg.exe` spawn per file is fine when it's a
   single sequential pass, not many small per-position calls) — the same reasoning applies to
   `SampleKeyframes`, so it was left CLI-only, unchanged (see "Live-verified" below). `SampleFrames` (the
   dense, closely-spaced full-window decode `MixedDensitySampler`/`VisualBumperMatcher` use) does
   **not** map cleanly onto VDF's existing `TryDecodeFrame(position)`-per-call pattern: that method
   seeks on every call, which suits VDF's own sparse, spread-out sampling positions but would mean
   reseeking on every closely-spaced position in VBR's dense windows — plausibly *slower* than the
   current CLI approach, which decodes a window forward once and picks frames via an `fps=` filter
   with no repeated seeking at all. **Resolved 2026-08-03**: a new `TryDecodeNextFrame` primitive
   on `VideoStreamDecoder` (decodes forward without seeking, reusing the existing packet/bad-packet/
   draining loop `TryDecodeFrame` already has) plus a new orchestration method in `FfmpegEngine.cs`
   (seek once to the region start, then loop `TryDecodeNextFrame`, converting via
   `VideoFrameConverter` whenever a frame's PTS crosses the next interval threshold) — matching
   `TryGetGrayBytesFromVideoNativeBatch`'s existing low-level-primitive-vs-orchestration split
   rather than putting the whole loop inside `VideoStreamDecoder` or duplicating its packet-handling
   logic in a fully separate class.

4. **A new `--native-ffmpeg-binding` CLI flag, defaulting to `true`** (decided 2026-08-03,
   overriding this ADR's original "default off" recommendation — an opt-out flag, not opt-in).
   This carries a meaningfully different kind of risk than ffmpeg decode/encode acceleration (ADR
   0013): a native decode failure that isn't cleanly caught can crash the whole process (the same
   class of uncatchable-native-failure risk ADR 0013 hit with DirectML) — VDF's own circuit
   breaker mitigates *repeated* failures within a scan, but does not eliminate a first crash, and a
   default-on posture exposes every user to that first-crash risk, not just opt-in early adopters.
   VDF itself defaults this off in a mature, already-shipped product; the maintainer's explicit
   decision here accepts that broader exposure in exchange for the feature actually helping people
   who never think to look for a flag — which raises the bar on Step 9's live verification
   (`docs/iterativeplan.md`) mattering before this ships, not lowers it.

5. **Native encode/mux is explicitly deferred, not bundled with this decision.** VDF.Core has no
   native encode/mux code at all to build on — `FFmpegNative/JpegFrameEncoder.cs` handles single
   still-image thumbnails only, never video output, because VDF (a duplicate finder) never writes
   video files. A native encode path for `ClipRemover`'s re-encode step would be building an
   entirely new `avcodec_send_frame`/`avcodec_receive_packet`/muxer/audio-passthrough/GPU-encoder
   subsystem from scratch, with no fork head start — a fundamentally different, larger, and
   independently riskier effort than reusing VDF's already-hardened decode path. It's also not
   obviously the same class of win: `vbr remove` issues one (or a handful of) ffmpeg invocations
   per file, not the many small per-position calls where process-spawn overhead dominates a scan
   — the process-overhead rationale motivating this decision's decode-side work doesn't obviously
   carry over to encode. Tracked as a follow-up TODO in `docs/iterativeplan.md`, to be scoped as
   its own separate ADR once real timing data from this decode-side work exists — not evaluated
   alongside it.

## Consequences

Positive: VBR gets a real, already-hardened performance improvement essentially for the cost of
new orchestration code around existing primitives, not a new native-interop subsystem built from
scratch — the hard parts (native library loading, per-file health tracking, hardware-decode device
context creation, several already-fixed native-decode edge cases: still-image draining, bad-packet
tolerance, HW decode format detection) are already solved in code this project already depends on.
Decode is decode regardless of what consumes the frames afterward, so this benefits scanning,
matching, and `add-bumper` uniformly, without changing any of VBR's own sampling *strategy*
(edge-focused density, mixed-density profiles — ADR 0006 stays fully in force; this ADR is purely
about how frames get decoded, not which ones get requested).

Negative / watch-outs: a real, not-yet-fully-quantified risk that VBR's edge-focused, shorter-window
sampling pattern won't see the same magnitude of improvement VDF measured on its own whole-file/
keyframe sampling pattern — the "biggest win" claim is VDF's, on VDF's own workload, not yet
independently confirmed for VBR's. The dense sequential-decode method (Decision 3) is new code,
not reused code, so it inherits none of VDF's own native-decode edge-case hardening automatically
— it needs its own live verification against the same corpus of tricky real files (10-bit HEVC,
still-image/JPEG edge cases, corrupt files) VDF's own native path was hardened against, not an
assumption that "built from the same primitives" means "equally robust." Defaulting to `true` (Decision 4) means every user is exposed to the first-crash risk above by
default, not just people who opt in — the tradeoff this ADR now accepts, in the other direction
from its original recommendation.

## Live-verified (2026-08-03)

Implemented per the resolved plan (`docs/iterativeplan.md`'s matching entry has the full
step-by-step). Two findings worth recording that the plan didn't anticipate:

- **`GetDenseAiFrames` (Step 3/4's planned reuse for `SampleKeyframes`) turned out to already be
  CLI-only, even with native binding enabled — a deliberate VDF design choice** ("a sequential
  single-pass keyframe sweep rather than seek-heavy" doesn't need native, since it's one
  `ffmpeg.exe` spawn per file either way, not many small per-position calls). Rewiring
  `SampleKeyframes` to call it would have gained nothing. Since `SampleKeyframes` has the exact
  same access-pattern shape (one file, one sequential decode, no per-position seeking), the same
  reasoning applies — **`SampleKeyframes` was left on its existing CLI implementation, unchanged.**
  Only `SampleFrames` (the dense, closely-spaced case — the actual target of the "avoid
  per-position spawn overhead" rationale) got native wiring. This narrows Decision 1/3's scope
  slightly from the original plan but doesn't change the core goal.
- **A real bug was found and fixed during verification, not anticipated in the design.** Live
  testing (after acquiring real shared FFmpeg libraries — see the acquisition-gap note below)
  showed native decode failing on *every* file's tail end, falling back to CLI every time despite
  the code appearing correct. Root cause: `DecodeNextRawFrame`'s "draining" flag was a per-call
  local variable — correct for `TryDecodeFrame` (which always seeks+flushes before decoding,
  implicitly resetting any prior draining state) but wrong for `TryDecodeNextFrame`'s whole
  purpose (repeated calls with **no** seek/flush between them): once EOF was reached and the
  codec entered draining mode in one call, the *next* call's fresh local `draining = false` tried
  to send FFmpeg's null "start draining" packet a second time to an already-draining codec —
  which FFmpeg correctly rejects with `AVERROR_EOF`, which `.ThrowExceptionIfError()` then turned
  into a thrown exception instead of the benign "already draining, nothing more to send" it
  actually meant. **Fixed** by promoting `draining` to an instance field (`_draining`) that
  persists across `TryDecodeNextFrame` calls, explicitly reset to `false` only where
  `TryDecodeFrame` already resets other seek-related state (`avcodec_flush_buffers`). After the
  fix: native decode completed successfully end-to-end, correct frame count, correct match result.
- **Frame-level output is not byte-identical between native and CLI, but functionally
  equivalent.** Same file, same sample positions (confirmed via `--dump-frames`: both produced
  exactly 5 raw sampled frames at the same index positions before filtering) — but corresponding
  PNG dumps differed slightly in file size (a few percent), and one run's "low-information" filter
  dropped a frame the other run kept. Visual inspection of the dumped frames side-by-side showed
  no discernible difference. Both runs scored the same `bestCos=100%` and reached the same match
  decision. Likely a minor color-range/rounding difference between ffmpeg's own CLI filter-graph
  color handling and this project's direct `sws_scale` call — not investigated further, since the
  plan's own correctness bar ("byte-identical **or equivalent-within-tolerance**") was met, and
  chasing sub-perceptual pixel differences that don't change any match outcome wasn't judged worth
  the time against the actual goal (verifying correctness, not chasing bit-exactness for its own
  sake).
- **Acquisition gap, not anticipated in the original plan: this project's own dev machine's ffmpeg
  install (Chocolatey) — very likely representative of most real users' installs — is a *static*
  build with no separate shared libraries at all (`avformat-*.dll` etc.), so
  `FFmpegHelper.CanLoadNativeLibraries` returns false and native decode silently never engages,
  regardless of the new default-on flag.** This was only discovered because live verification
  required manually downloading a real shared FFmpeg build (BtbN's Windows shared build) to test
  against at all — confirmed by direct inspection (`ls` on the Chocolatey install directory: only
  `ffmpeg.exe`/`ffprobe.exe`/`ffplay.exe`, no `av*.dll`/`sw*.dll`). VDF.GUI/VDF.Web have
  `FfmpegDownloader` (which specifically fetches shared builds) wired in; **VBR.CLI does not, and
  this ADR/plan didn't build one.** The fallback behavior is completely safe (silent, correct CLI
  fallback, exactly as designed) — but it means the default-on native binding currently provides
  **zero benefit for most real-world installs** until either VBR.CLI gains its own shared-FFmpeg
  acquisition path, or this requirement is documented clearly enough that users can self-serve it
  (e.g. pointing `--hardware-accel`-adjacent docs at a shared build download). Tracked as a new
  open question below — genuinely out of this pass's original scope, not something to silently
  patch over.

## Open questions — being resolved with the maintainer one at a time (2026-08-03)

- **New (found during live verification, not in the original design): VBR.CLI has no way to
  acquire a shared FFmpeg build, so the default-on native path is a no-op for most real installs**
  (static builds, e.g. Chocolatey's) — see "Live-verified" above. Candidates for a follow-up, not
  decided or scoped yet: build a VBR-side equivalent of VDF's own `FfmpegDownloader` (a real,
  possibly substantial effort — that downloader already exists and is VDF.GUI/Web-specific
  plumbing, not something VBR.CLI currently has any of); document the shared-build requirement
  clearly for users willing to self-serve it; or accept native binding as an
  already-have-the-right-ffmpeg-build bonus for now and revisit acquisition later.
- Exact `FfmpegEngine.GetDenseAiFrames` return shape — an implementation-time verification task,
  not a decision; not part of the walkthrough. **Superseded**: `SampleKeyframes` was not rewired
  to use it at all (see "Live-verified" above), so this question no longer applies.
- ~~Whether the new dense sequential-decode method belongs as a new instance method on
  `VideoStreamDecoder` itself, or as a separate orchestration function/class~~ — **resolved
  2026-08-03: a small `TryDecodeNextFrame` (no-seek decode) primitive on `VideoStreamDecoder`,
  reusing its existing packet/bad-packet/draining loop, with the interval/window/conversion
  orchestration living in a new `FfmpegEngine.cs` method matching `TryGetGrayBytesFromVideoNativeBatch`'s
  existing shape — keeps VDF's own established low-level-primitive-vs-orchestration split rather
  than introducing a second convention.**
- ~~Whether `--hardware-accel auto` combined with `--native-ffmpeg-binding` needs an explicit,
  documented resolution or should reject the combination~~ — **resolved 2026-08-03: probe-by-attempting
  a platform-appropriate list of `AVHWDeviceType` candidates (`d3d11va`/`dxva2` on Windows,
  `vaapi` on Linux, `videotoolbox` on macOS), falling back to `none`, when `auto` is requested
  under native binding — reuses the same probe-by-attempting pattern already established for GPU
  encoders (`GpuEncoderProbe`, ADR 0013) and DirectML device selection, giving `auto` the same
  real meaning under native binding it already has via the CLI path (where ffmpeg itself picks).
  Rejecting the combination outright was ruled out: since native binding now also defaults to
  `true` (Decision 4), both defaults collide by default — a bare zero-flag `vbr scan` must not
  hard-fail on first run.**
- ~~Whether `--native-ffmpeg-binding` should eventually default to on/auto or stay opt-in~~ —
  **resolved 2026-08-03: defaults to `true`, see Decision 4.**
- ~~Whether to share `FfmpegEngine`'s existing static per-scan native-health circuit-breaker state
  directly, or maintain VBR's own separate instance~~ — **resolved 2026-08-03: share it directly.**
  Zero new tracking code, and more correct: Decision 3's new dense-window method and Step 4's
  `GetDenseAiFrames` reuse both run in the same VBR.CLI process during a single scan, so a native
  failure in one very likely shares a root cause with the other (same underlying library/driver) —
  backing off together is the safer behavior, and each `vbr` invocation being its own OS process
  already lines up naturally with the existing "reset at start of scan" semantics
  (`UseNativeBinding`'s setter), no adjustment needed.

**All open questions are now resolved as of 2026-08-03** — nothing left in this ADR blocking
implementation except the one flagged verification task (`GetDenseAiFrames`'s return shape).
