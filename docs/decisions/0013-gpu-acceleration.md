# ADR 0013: GPU acceleration — ffmpeg decode/encode and ONNX inference

- **Status:** accepted, implemented and live-verified on a real machine with a working GPU
  (RTX 3080, 2026-07-30). GPU re-encode (Decision 3) is now confirmed actually engaging, not just
  falling back — see the second "Live-verified" section below. DirectML inference (Decision 5)
  still does not actually engage on this machine even though decode/encode GPU paths both do on
  the same hardware; it falls back to CPU safely and correctly, but the underlying cause (a DXGI
  adapter handle that validates in a probe subprocess but is rejected in the real process) remains
  unresolved — see "Open questions."
- **Date:** 2026-07-30
- **Related:** [`0009-library-scan-database.md`](0009-library-scan-database.md) (scanning speed),
  [`0010-database-backed-removal.md`](0010-database-backed-removal.md) (matching speed),
  [`0012-removal-reencode-defaults.md`](0012-removal-reencode-defaults.md) (the CPU codec table
  this ADR's encode tier sits on top of — supersedes that ADR's "GPU (NVENC) vs. CPU encode — not
  addressed" open question), [`../research/vdf-evaluation.md`](../research/vdf-evaluation.md)
  (the ~6.3x `-hwaccel cuda` decode measurement and the flagged-but-unmeasured "CPU-only ONNX
  inference may be the bigger bottleneck" finding this ADR's inference tier responds to)

## Context

Three named pain points as the project approaches its next milestone: scanning speed
(`vbr scan`), encode speed (`vbr remove`'s re-encode step), and bumper/matching speed
(`vbr add-bumper`, `vbr match`). All three ultimately bottleneck on ffmpeg decode/encode and ONNX
embedding inference — every one of them CPU-only in every VBR code path, despite this project's
own forked engine (VDF) already having a working, wired decode hardware-acceleration mechanism
(`FFHardwareAccelerationMode`/`FfmpegEngine.HardwareAccelerationMode`) that VBR had simply never
plugged into. No encode-side GPU support existed anywhere (`ClipRemover` hardcoded `libx264`
unconditionally), and no ONNX execution provider beyond the default CPU one existed either.

## Decision

1. **One knob for all three layers, not three independent flags.** `VBR.Core.Extraction.HardwareAcceleration`
   (a new, thin bridge around VDF.Core's existing `internal FfmpegEngine.HardwareAccelerationMode`
   — VBR.Core can see it via the ADR 0005 `InternalsVisibleTo` grant, but `VBR.CLI` can't, hence
   the bridge) is the single source of truth. `--hardware-accel <mode>` (reusing VDF's own
   `FFHardwareAccelerationMode` enum and CLI convention — `none`/`auto`/`cuda`/`qsv`/etc.), added
   to `scan`/`match`/`remove`/`add-bumper`, sets it once at CLI startup. `none` disables ffmpeg
   decode `-hwaccel`, GPU re-encode probing, and ONNX DirectML alike; anything else enables
   best-effort GPU use everywhere applicable, with a silent, safe fallback to CPU at every single
   layer if GPU acceleration isn't actually available. **VBR's own commands default this to
   `auto`** — VDF.CLI's own `--hardware-accel` still defaults to `none` (unchanged, out of this
   ADR's scope) — matching this project's explicit "use GPU wherever possible" goal rather than
   VDF's opt-in default.

2. **Decode: reuse VDF's mechanism, don't rebuild it.** `DenseFrameSampler.SampleFrames` (the one
   shared decode path behind `WholeFileSampler`/`MixedDensitySampler`/`BumperCatalogBuilder` —
   i.e. scanning, matching, and `add-bumper` all at once), `ClipExtractor.RunFfmpegExtract`, and
   all four of `ClipRemover`'s ffmpeg builders now add `-hwaccel <mode>` exactly the way
   `FfmpegEngine`'s own CLI-path methods already do — same flag, same placement convention.
   Harmless (a no-op) wherever no decode actually happens (stream-copy); real benefit everywhere
   else.

3. **Encode: H.264/HEVC GPU tier on top of ADR 0012's CPU table, detected by probing a real
   encode.** `VBR.Core.Removal.GpuEncoderProbe` tries `h264_nvenc`/`h264_qsv`/`h264_amf` (or the
   `hevc_*` equivalents), in that priority order, by actually running a trivial synthetic 1-frame
   encode (`-f lavfi -i color=c=black:s=256x256:r=1 -frames:v 1 -c:v <candidate> -f null -`) —
   **not** a static `ffmpeg -encoders` list check, since a build can list an encoder as compiled-in
   with no compatible driver/GPU present, which only fails at real invocation time. The synthetic
   frame is 256×256, not smaller — live-verified (see below) that a 64×64 probe frame makes a
   genuinely working NVENC encoder report failure (`Frame Dimension less than the minimum
   supported value`), so the probe frame must clear every candidate encoder's minimum-dimension
   floor, not just be cheap. Cached once per
   process run (hardware doesn't change mid-run; re-probing each new `vbr` invocation is cheap
   insurance against a stale answer surviving a driver update). Scope is deliberately narrow: only
   H.264 and HEVC — the two codecs every mainstream GPU vendor supports maturely, and the two rows
   ADR 0012 already gives "solid" confidence to. VP9 stays CPU-only (GPU VP9 encode is rare/recent
   hardware only); AV1 stays fully deferred, unchanged from ADR 0012. `ClipRemover.SelectVideoEncoder`
   now implements ADR 0012's CPU codec-matched table (H.264→`libx264` CRF22, HEVC→`libx265` CRF24,
   VP9→`libvpx-vp9` CRF31, unrecognized→`libx264` CRF22 fallback) as the tier the GPU choice falls
   back to — built together, not separately, since it's the same code region either way. GPU
   quality knobs (`-cq`/`-global_quality`/`-qp_i`+`-qp_p` for NVENC/QSV/AMF respectively) are
   mirrored at the same numeric target as the adjacent CPU CRF row — a reasonable starting point,
   not a verified equivalence.

4. **Bit depth is matched; full HDR color-metadata passthrough is explicitly deferred, with a
   warning instead of a silent downgrade.** `MediaInfo.StreamInfo.PixelFormat`/`HdrFormat` (both
   already parsed by `FFProbeEngine`/`FFProbeJsonReader` — no new ffprobe parsing needed) drive a
   `-pix_fmt yuv420p10le` output flag when the source is 10-bit, and a console warning (re-encode
   path only — stream-copy never touches stream contents) whenever the source is any HDR format
   (`HLG`/`HDR10`/`HDR10+`/`Dolby Vision`) that color primaries/transfer/colorspace and HDR10
   mastering-display metadata aren't carried through yet. Matches ADR 0012's own stated HDR
   position ("detect what we can, preserve what we can confidently preserve, refuse or warn rather
   than silently downgrade") for the half of it this pass actually builds — full passthrough
   remains genuinely deferred, not silently dropped from scope.

5. **ONNX inference: DirectML only, no CUDA.** Chosen specifically because this tool is meant to
   be downloaded and used broadly, not only by the maintainer's own machine — DirectML works on
   any DirectX 12–capable GPU (NVIDIA, AMD, and Intel alike) with **no separate toolkit for a user
   to install** (unlike CUDA, which needs a multi-GB, version-pinned NVIDIA CUDA Toolkit + cuDNN
   the user would have to acquire themselves). Windows-only; every other platform (and any Windows
   machine where DirectML init fails for any reason — no compatible GPU, a driver issue) falls
   back to the exact original CPU-only `SessionOptions` path, never a hard failure.

   **Acquisition is a genuinely different mechanism from the existing CPU runtime download**,
   confirmed against the actual `microsoft/onnxruntime` v1.23.2 GitHub release asset listing: no
   DirectML archive is published there (only CPU and CUDA/TensorRT "gpu" builds for Windows).
   DirectML-enabled native binaries are only published via NuGet
   (`Microsoft.ML.OnnxRuntime.DirectML`, which itself depends on `Microsoft.AI.DirectML` for
   `DirectML.dll`) — `AiComponents.DownloadDirectMlRuntimeAsync` downloads both `.nupkg`s (a nupkg
   is a zip) via NuGet's direct-package-download endpoint, using the same extraction mechanism
   (`ArchiveUtils.Extract`) already used for the CPU archive. **The two packages turned out to use
   different internal layouts** (found live, see "Live-verified" below) — `Microsoft.ML.OnnxRuntime.DirectML`
   follows the standard `runtimes/win-x64/native/` convention every `Microsoft.ML.OnnxRuntime.*`
   package uses, but `Microsoft.AI.DirectML` is a native SDK-style package using `bin/x64-win/`
   instead. **Pinned to `Microsoft.ML.OnnxRuntime.DirectML 1.23.0`** (not
   `1.23.2`, matching `Microsoft.ML.OnnxRuntime.Managed`'s pin exactly) — that package has no
   `1.23.2` release at all (its version list skips `1.23.0` → `1.24.1`); `1.23.0` is the nearest
   available, minimizing ABI drift against the shared managed wrapper. `Microsoft.AI.DirectML` is
   pinned to `1.15.4` — the exact minimum `Microsoft.ML.OnnxRuntime.DirectML 1.23.0` depends on
   (confirmed against its published NuGet dependency group), which is also that package's own
   latest release.

   **Kept in a separate folder (`AiComponents.DirectMlFolder`, a `directml` subfolder of the
   existing `AiFolder`), not swapped in-place over the CPU runtime.** Both flavors produce a
   native library literally named `onnxruntime.dll` on Windows — sharing one flat folder would mean
   switching `--hardware-accel` between separate `vbr` invocations always re-downloads/overwrites
   whichever flavor isn't currently resident. Separate folders mean both can coexist, downloaded
   once each, switched between for free thereafter. `OnnxEmbedder`'s constructor gained an optional
   `preferDirectML` parameter that appends `AppendExecutionProvider_DML()` inside a try/catch
   falling back to the untouched original CPU-only `SessionOptions()` on any failure —
   `AiComponents.EnsureResolverInstalled`/`EnsureReady`/`GetState`/`DownloadAsync` all gained the
   same parameter, routing to the DirectML folder/version instead of the CPU one when requested,
   fully backward-compatible (default `false`, every pre-existing VDF caller unaffected).

## Consequences

Positive: three independently-bottlenecked subsystems (scanning, encoding, matching/bumper speed)
all get a real GPU path from one user-facing setting, with graceful, silent, tested-at-every-layer
fallback to today's exact CPU behavior whenever GPU acceleration isn't actually available —
nothing about this ADR changes any existing default-CPU-path behavior or output. Decode
acceleration was nearly free (VDF had already built and validated the mechanism; VBR just needed
to call it). The DirectML choice specifically means every future user of this tool gets a real
speedup path with zero extra installation burden, not just the maintainer's own NVIDIA hardware.

Negative / watch-outs: GPU quality-flag numeric mappings (NVENC `-cq`/QSV `-global_quality`/AMF
`-qp_i`+`-qp_p`) are a reasonable starting guess, not empirically validated against the adjacent
CPU CRF targets the way ADR 0012's own CPU numbers were (mirrored from HandBrake's precedent) —
worth revisiting once real output is inspected. The DirectML acquisition path depends on NuGet's
direct-package-download URL shape and the exact `runtimes/win-x64/native/` layout inside those two
packages remaining stable — more fragile than the GitHub-release-archive mechanism the CPU path
uses, and win-arm64 DirectML isn't attempted at all (Windows x64 only). GPU encoder probing adds a
small, one-time-per-run cost (a handful of sub-second synthetic encodes) even on machines that
turn out to have no working GPU encoder at all — negligible relative to the multi-minute
re-encodes it's trying to accelerate, but not exactly zero.

## Live-verified (2026-07-30)

Run against real media (`vbr remove --hardware-accel auto`, a real Daredevil episode + distractor,
re-encode mode) on a machine with **no working GPU** (real ffmpeg build with NVENC/QSV/AMF
encoders compiled in, but no functioning hardware behind them; no working D3D12 device for
DirectML) — i.e., this exercised every fallback path for real, not the "GPU actually engages"
path:

- **A real bug was found and fixed: `Microsoft.AI.DirectML`'s NuGet package does not use the
  `runtimes/<rid>/native/` layout the rest of this ADR assumed.** Downloading and inspecting the
  actual `.nupkg` (not just its NuGet.org listing page) showed it's a native SDK-style package
  (`bin/x64-win/DirectML.dll`, alongside `.lib`/`.pdb`/`Debug`-build files not needed here) —
  `Microsoft.ML.OnnxRuntime.DirectML` does use the standard layout, so the two packages needed two
  different extraction rules, not one shared helper. `AiComponents.DownloadDirectMlRuntimeAsync`
  fixed and re-verified: both native files land in `DirectMlFolder` with the correct real byte
  sizes (`DirectML.dll` 18,527,776 bytes, `onnxruntime.dll` 17,201,208 bytes, matching the
  authoritative package contents exactly).
- **A second, more serious bug was found and fixed: DirectML initialization can crash the whole
  process with a native access violation (`0xC0000005`) that no managed `try`/`catch` can
  contain** — not the graceful, catchable failure the original design assumed `AppendExecutionProvider_DML()`
  would produce on an incompatible machine. This bypassed `OnnxEmbedder`'s own internal
  `try`/`catch` entirely, taking down the whole `vbr remove` run. **Fixed with real process
  isolation**, the same principle `GpuEncoderProbe` already used for ffmpeg encoders:
  `HardwareAcceleration.ProbeDirectMlInSubprocess` re-invokes the same executable with a hidden
  `--internal-probe-directml <model>` argument (handled at the very top of `Program.Main`, before
  any normal CLI parsing) and constructs the real `OnnxEmbedder(preferDirectML: true)` **in that
  child process** — its crash or nonzero exit only ever affects the probe, never the real run.
  `SharedOptions.EnsureAiComponentsReadyAsync` runs this probe every time DirectML is requested
  (not only right after a fresh download — a prior run's already-downloaded files say nothing
  about this run's driver state), and marks DirectML unavailable for the rest of the process on
  any failure. Re-verified after the fix: the exact same crash-triggering machine now prints a
  clean `Warning: DirectML acceleration did not initialize correctly on this machine — falling
  back to CPU inference.`, no crash, and the run completes normally.
- **Every fallback path confirmed correct end-to-end**, not just individually: `--hardware-accel auto`
  with no working GPU produced *identical* match results to `--hardware-accel none`
  (`present=5/6 bestCos=98%` both ways) and to this project's own established baseline numbers from
  earlier sessions — GPU acceleration attempting-and-failing changes nothing about correctness, only
  (attempted) speed. `GpuEncoderProbe` correctly tried `h264_nvenc`→`h264_qsv`→`h264_amf` in order,
  logged each failure, and fell through to the ADR 0012 CPU table (`libx264 CRF 22`, confirmed via
  the logged exact ffmpeg command line — not the old `CRF 18` placeholder). `-hwaccel auto` was
  confirmed present in the logged ffmpeg command line for both decode and re-encode calls.

**Follow-up verification on the maintainer's real RTX 3080 machine confirmed decode/encode GPU
acceleration actually engages** (see next section) — the item above is resolved for Decisions 2/3.
DirectML (Decision 5) remains unresolved: see "Open questions."

## Live-verified (2026-07-30, real RTX 3080 machine, RDP session)

Follow-up verification on the maintainer's actual machine (headless, RDP-only access, RTX 3080 —
confirmed via `dxdiag`/WMI, not a sandbox) surfaced three more findings, one already fixed and
proven, two still open.

- **Incorrect intermediate theory, corrected by the maintainer with hard evidence.** Initial
  DirectML failures on this machine led to a hypothesis that RDP sessions block GPU access
  generally. The maintainer disproved this directly: a manual `ffmpeg -c:v h264_nvenc` run on this
  exact machine, this exact RDP session, on a real file, measured `speed=62x elapsed=0:00:50.83`
  versus the CPU encode's `speed=11.7x elapsed=0:04:29.57` — real NVENC hardware acceleration
  working over RDP. The investigation refocused on what's DirectML/D3D12-specific rather than a
  blanket RDP limitation.
- **Root cause found for DirectML device selection: `AppendExecutionProvider_DML()` with no
  explicit device index defaults to DXGI adapter 0, and adapter 0 on this machine is a phantom.**
  `dxdiag /t` lists two Display Devices: a "Microsoft Remote Display Adapter" *first*
  (`Device Type: Display-Only Device`, `Chip type: Unknown`, but falsely advertising
  `Feature Levels: 12_2,...`), and the real "NVIDIA GeForce RTX 3080" *second* (explicitly carries
  `Adapter Attributes: ...,D3D12_GENERIC_ML,...`). **Fixed**: `HardwareAcceleration.ProbeDirectMlInSubprocess`
  now tries device indices 0 through 4 (one probe subprocess per candidate — a crash kills that
  process before it could try the next index itself), records the first index whose probe
  subprocess neither crashes nor throws, and every real `OnnxEmbedder` construction site is passed
  that index instead of the implicit 0 default.
- **New, unresolved: the winning probe index still fails in the real (non-probe) process.** With
  the fix above, the probe subprocess consistently succeeds at device index 2 on this machine — but
  the real `OnnxEmbedder` construction in the actual `vbr` process, using that same index 2,
  consistently fails with a *different*, cleanly catchable error:
  `[ErrorCode:RuntimeException] ... C0262002 Specified display adapter handle is invalid.` No
  crash — `OnnxEmbedder`'s try/catch handles it exactly as designed, logs a WARN, and falls back to
  CPU correctly every time (`present=5/6 bestCos=98%`, matching the CPU-only baseline). The
  mechanism isn't understood: one theory is that DXGI adapter handles/indices aren't stable across
  separate process launches in this specific RDP-virtualized display environment, so a handle
  that's valid when the probe subprocess opens it is no longer valid by the time a *different*
  process (the real run) opens "the same" index moments later. Not confirmed. The net effect is
  safe (clean fallback, no crash, correct results) but DirectML acceleration does not actually
  engage on this machine.
- **Ruled out as the (sole) cause: ONNX Runtime version mismatch.** Hypothesized the
  `Microsoft.ML.OnnxRuntime.Managed 1.23.2` wrapper was ABI-mismatched against the DirectML native
  runtime (pinned `1.23.0`, since no `1.23.2` DirectML package exists). Downgraded the whole
  project's pin to `1.23.0` (confirmed via NuGet that `1.23.0` still ships a matching
  `onnxruntime-osx-x86_64-1.23.0.tgz`, so nothing about the original 1.23.2 rationale was lost) and
  re-tested — the identical "invalid adapter handle" failure persisted. Kept the downgrade anyway
  (still correct/safer, removes one variable) but it did not fix this issue.
- **Fixed and confirmed: `GpuEncoderProbe` was reporting real, working GPU encoders as failed.**
  The original 64×64 synthetic probe frame is below NVENC's minimum supported encode dimension —
  manually running the exact probe command reproduced `InitializeEncoder failed: invalid param
  (8): Frame Dimension less than the minimum supported value`, on the same machine where a real
  NVENC encode (see above) works fine. Bumped the probe frame to 256×256 (Decision 3, updated
  above) and re-ran `vbr remove --hardware-accel auto --verbose` end-to-end: `GpuEncoderProbe` now
  logs `'h264_nvenc' probed successfully`, `ClipRemover` logs `Re-encode video codec: h264_nvenc
  (GPU)`, and the real ffmpeg invocation shows `-c:v h264_nvenc -cq 22 -preset p5` — **GPU
  re-encode acceleration is now confirmed actually engaging**, not just falling back to CPU. (QSV
  and AMF still report probe failures on this machine at 256×256 too — expected and correct, since
  this machine has no Intel/AMD GPU for those encoders to run on at all.)

## Open questions

- **Why DirectML's real (non-probe) `OnnxEmbedder` construction fails with "invalid adapter
  handle" at the same device index its own probe subprocess just confirmed works** — the single
  open item blocking actual DirectML acceleration on this machine. Candidate next step: re-probe
  inside the *same* process immediately before constructing the real embedder, rather than trusting
  a result from an earlier, separate probe process, to test whether the handle instability is
  specifically a cross-process phenomenon.
- GPU quality-flag numeric values (Decision 3) — starting guesses, not tuned.
- Whether `-preset`/`-global_quality`/`-quality` choices for the three GPU encoder families are
  well-chosen — same "not yet finalized" status ADR 0012 left its own CPU preset value in.
- win-arm64 DirectML support — not attempted.
- CUDA execution provider for ONNX inference — explicitly rejected for this pass (Decision 5); the
  maintainer's own RTX 3080 would benefit from it, but the "download-free for every user" goal won
  out. Revisit only if DirectML's measured performance turns out meaningfully insufficient.
- Whether the GPU re-encode quality target should be independently exposed/configurable at all —
  no CLI surface for it exists (matches ADR 0012's own "no user-facing configuration in v1"
  stance), inherited here without re-litigating it.
