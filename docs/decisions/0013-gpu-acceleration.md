# ADR 0013: GPU acceleration — ffmpeg decode/encode and ONNX inference

- **Status:** accepted, implemented and live-verified on a real machine (2026-07-30) — with one
  real design gap found and fixed in the process (see "Live-verified" below): DirectML
  initialization can crash the process with a native access violation that no managed
  `try`/`catch` can contain, which the original design didn't account for. Full GPU-encoder and
  DirectML-success verification on hardware that actually has a working NVENC/QSV/AMF encoder or a
  real GPU/display driver still hasn't happened — the machine this was verified on has neither
  (every GPU path correctly and safely fell back to CPU, which is exactly what's checked below,
  but it doesn't exercise the "GPU path actually engages" direction).
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
   encode (`-f lavfi -i color=... -c:v <candidate> -f null -`) — **not** a static
   `ffmpeg -encoders` list check, since a build can list an encoder as compiled-in with no
   compatible driver/GPU present, which only fails at real invocation time. Cached once per
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

**Still not verified: the actual GPU-engaged path** — no machine with a working NVENC/QSV/AMF
encoder or a functioning DirectML-capable GPU has run this yet. Decode `-hwaccel` actually
reproducing identical fingerprints at higher speed, `GpuEncoderProbe` actually picking a working
encoder and producing correct comparable-quality output, and `AppendExecutionProvider_DML()`
actually succeeding rather than falling back — none of that is confirmed yet. This is the next
step (on the maintainer's own RTX 3080 machine), not a gap the design left unaddressed.

## Open questions

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
