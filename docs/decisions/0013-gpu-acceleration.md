# ADR 0013: GPU acceleration — ffmpeg decode/encode and ONNX inference

- **Status:** accepted, implemented and live-verified on multiple real machines with working GPUs
  (2026-07-30 to 2026-08-02). GPU re-encode (Decision 3) is confirmed actually engaging, not just
  falling back. **DirectML inference (Decision 5): a measurement bug in this project's own probe
  was found and fixed (2026-08-02) that invalidates most of the "cross-process handle instability"
  narrative below — see "Live-verified (2026-08-02, the probe itself was lying)".** The corrected,
  honest signal is simpler and worse than previously believed: DirectML has not been confirmed to
  actually attach on ANY device index, on EITHER real machine tested, at any point in this whole
  investigation — every earlier "device index N works" report was a false positive caused by
  `OnnxEmbedder` catching DirectML's own failure internally and falling back to CPU with no
  exception, which the probe mistook for success since it only checked "did construction throw."
  DirectML now falls back to CPU safely, correctly, and (as of the fix) *transparently* — but a
  real, still-unexplained DirectML-level failure remains: it does not actually engage anywhere.
- **Date:** 2026-07-30 (cross-machine DirectML finding added 2026-07-31, CUDA diagnostic +
  DirectML version-bump added 2026-07-31, DXGI adapter filtering implemented and ruled out as the
  fix 2026-07-31)
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

## Live-verified (2026-07-31, cross-machine — DirectML confirmed broken, not RDP-specific)

Using the standalone `test-onnx-directml.ps1` script (independent of VBR entirely — see ADR 0014)
and the `-Layout Loose` redistributable package, the maintainer tested DirectML on several
additional real machines, each with a discrete GPU, **none of them RDP sessions** — directly
testing whether the dev machine's RDP/phantom-adapter theory was the actual cause. Results from
one such machine (RTX 5080, native/local session, `directml_test_machine.txt`), trying every
device index the probe range covers:

| Device index | Result |
| --- | --- |
| 0 | **Uncatchable crash** (`AccessViolationException` in `InferenceSession.Init`) |
| 1 | **Uncatchable crash** (same) |
| 2 | Catchable `RuntimeException`: `C0262002 Specified display adapter handle is invalid` — **the exact same error and error code as the original RDP dev machine** |
| 3 | Catchable `RuntimeException`: `887A0002 The object was not found... no adapter with the specified ordinal` (this machine simply has fewer DXGI adapters than the index tried) |
| 4 | Same as 3 |

**This falsifies the working theory that the original failure was specific to the dev machine's
RDP session or its phantom "Microsoft Remote Display Adapter."** A completely different machine —
local session, no RDP, a current-generation GPU — fails at *every* device index tried, including
two hard crashes and a repeat of the identical "invalid adapter handle" error code at index 2. The
maintainer's assessment, which the evidence supports: **DirectML is not being invoked correctly by
this project, full stop** — this is a bug in how VBR/VDF calls the DirectML execution provider
(API usage, version pairing, or missing setup step), not an environment quirk of any one machine.
The device-index-probing mechanism (Decision "Root cause found for DirectML device selection"
above) still provides real value — it avoids the phantom-adapter case and the crash-safety
subprocess isolation still works exactly as designed on every machine tested, including this one —
but it was treating a symptom, not the actual defect.

Not yet investigated: whether `AppendExecutionProvider_DML(deviceId)` (the simple index-based
overload, what this project uses throughout) is the wrong API to use at all — ONNX Runtime also
exposes device-explicit overloads (e.g. passing a pre-created `ID3D12Device`/command queue rather
than an index for the DML EP to enumerate itself) that may be the actually-supported path for this
ORT/DirectML version pairing; whether the redistributed `DirectML.dll` (pinned to
`Microsoft.AI.DirectML 1.15.4`, the *minimum* version `Microsoft.ML.OnnxRuntime.DirectML 1.23.0`
declares, not necessarily a version Microsoft actually validated that combination against) is
itself the mismatch, versus using the DirectML.dll that ships in the Windows OS itself (Windows 10
1903+) instead of a redistributed one; and whether a newer/older `Microsoft.ML.OnnxRuntime.DirectML`
pin behaves differently. CUDA is being tested next (separately, diagnostic-only per Decision 5 —
not a reconsideration of the DirectML-only shipping decision) as a further data point on whether
GPU ONNX inference works at all via any execution provider on these machines, which would help
isolate whether this is DirectML-specific or a broader pattern in how this project drives ONNX
Runtime's GPU execution providers generally.

## Live-verified (2026-07-31, CUDA diagnostic + DirectML version-bump — DirectML ruled NOT stale)

Two more data points, using `test-onnx-directml.ps1`'s new `-Mode Cuda` and `-NativeFolder`/
`-ManagedDllPath` override flags (diagnostic-only, no VBR/VDF code involved — isolates whether
*this project's code* is at fault versus something about the pinned package versions):

- **CUDA, diagnostic-only per Decision 5, tested on the maintainer's RTX 5080 machine (a
  completely different, non-RDP machine with other GPU-accelerated tools — Ollama, AnythingLLM,
  ChatRTX, etc. — already working correctly on the same hardware).** First attempt
  (`Microsoft.ML.OnnxRuntime.Gpu.Windows 1.23.0`, matching this project's pinned CPU/DirectML
  version) failed with `cudaErrorNoKernelImageForDevice: no kernel image is available for
  execution on the device` — a real, well-documented, widely-reported gap: official ONNX Runtime
  CUDA builds historically lagged behind brand-new NVIDIA architectures (RTX 50-series /
  "Blackwell" / compute capability sm_120), confirmed against multiple external reports
  ([microsoft/onnxruntime#26181](https://github.com/microsoft/onnxruntime/issues/26181),
  [#27600](https://github.com/microsoft/onnxruntime/issues/27600), and a
  [third-party Blackwell-targeted rebuild](https://github.com/Natfii/onnxruntime-gpu-blackwell)
  that exists specifically because of this gap). **Re-tested with
  `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.28.0` (current latest stable) — succeeded outright, no
  error, real inference ran on the CUDAExecutionProvider.** This is a clean, fully-explained
  failure with a known cause and a known fix (a newer pinned version) — not evidence of anything
  wrong in how this project invokes ONNX Runtime's GPU execution providers.
- **DirectML, re-tested with the newest available `Microsoft.ML.OnnxRuntime.DirectML` release
  (`1.24.4` — that package's version list tops out well below the CPU/CUDA packages' `1.28.0`,
  confirmed against NuGet directly) on the original RTX 3080 dev machine, which already reproduces
  the failure.** Identical results to `1.23.0` at every device index: uncatchable
  `AccessViolationException` at 0 and 1, `C0262002 Specified display adapter handle is invalid` at
  2, `887A0002 ... no adapter with the specified ordinal` at 3 and 4 — same error codes, same
  messages, same crash/no-crash pattern. **This rules out a stale DirectML package version as the
  cause**, in direct contrast to the CUDA result above: DirectML's failure is not explained by
  "needs a newer release," which narrows the real cause to either the specific API call this
  project uses (`AppendExecutionProvider_DML(int deviceId)`, the simple index-based overload) or
  something about the `.NET`/DXGI hosting environment both test runs share.
- **Grounded against ONNX Runtime's own DirectML documentation**: `device_id` is documented to
  "correspond to the enumeration order of hardware adapters as given by `IDXGIFactory::EnumAdapters`"
  and "a `device_id` of 0 always corresponds to the default adapter" — meaning index 0 crashing on
  *two unrelated real machines* (one RDP with a known phantom adapter, one a plain local RTX 5080
  desktop with no obvious reason to have a non-functional adapter at the default slot) is
  genuinely anomalous, not expected behavior for "index 0." The docs also mention a
  device-explicit alternative, `SessionOptionsAppendExecutionProvider_DML1` (taking a caller-created
  `IDMLDevice`/`ID3D12CommandQueue` instead of an index for the EP to enumerate itself), without
  detailed guidance on when the index-based overload is known to be insufficient. Separately,
  [microsoft/onnxruntime#9708](https://github.com/microsoft/onnxruntime/issues/9708) documents a
  real (if not fully resolved) issue class where DirectML mishandles software/WARP adapters
  encountered during enumeration — a plausible explanation for low indices behaving badly if a
  non-hardware adapter happens to sit there, though this hasn't been directly confirmed on either
  of this project's two test machines yet (would need a raw DXGI adapter enumeration dump,
  independent of ONNX Runtime, cross-referenced against which index crashes/fails on that specific
  machine).

## Live-verified (2026-07-31, real DXGI adapter filtering — phantom-adapter theory ruled OUT as root cause)

Confirmed the phantom-adapter finding was real by finding the *same* class of virtual adapter on
the maintainer's separate RTX 5080 test machine: `dxdiag /t` there shows a `Meta Virtual Monitor`
(`Manufacturer: Meta Inc.`, `Chip type: Unknown`, `Device Type: Display-Only Device`,
`Adapter Attributes: Unknown`, driver `virtualscreendriver.dll`) — structurally identical to the
dev machine's `Microsoft Remote Display Adapter` (same "Display-Only Device, Chip type Unknown,
falsely-advertised full Feature Levels" shape), left behind by Meta Quest Link software despite no
headset being attached (confirmed with the maintainer — not currently connected, hasn't been for
years, yet the virtual display driver persists). Windows' Indirect Display Driver (IDD) framework
is the common mechanism — used by RDP, VR/AR headset software, and most screen-streaming tools —
so this isn't specific to either RDP or Meta; any such software can produce this class of phantom
adapter.

**Built and shipped a real fix for this class of problem**: `VBR.Core.Extraction.DirectMlAdapterEnumerator`
(new file), using `Vortice.DXGI` (a thin, actively-maintained managed DXGI wrapper — new
`PackageReference` in `VBR.Core.csproj`, chosen over hand-rolled COM interop) to enumerate real
DXGI adapters directly via `IDXGIFactory1.EnumAdapters1`, filtering out anything flagged
`AdapterFlags.Software` (catches WARP) *and* anything whose `VendorId` isn't a known real GPU
vendor (NVIDIA/AMD/Intel/Qualcomm — catches IDD virtual adapters, which live-verified are **not**
flagged `AdapterFlags.Software` even though they're not real hardware, so that flag alone
wouldn't have caught either the RDP or Meta phantom adapter). `HardwareAcceleration.ProbeDirectMlInSubprocess`
now tries only these filtered real-GPU indices instead of blindly trying 0 through 4, falling back
to the old blind range only if enumeration itself fails for any reason.

**Live-verified this change is correct but does NOT fix the actual observed failure.** Re-ran
`vbr match --hardware-accel auto --verbose` on the dev machine after the fix: the probe still
selects device index 2 (now via real filtering, not luck), and the real `OnnxEmbedder`
construction in the actual `vbr` process **still fails at that same index 2 with the identical
`C0262002 Specified display adapter handle is invalid` error** — unchanged from before this fix.
This is an important negative result, not a wasted effort: it **rules out the phantom-adapter
theory as the root cause of the specific "invalid handle" failure**, even though the underlying
phantom-adapter problem is real and worth having fixed defensively (it still protects against
literally handing `AppendExecutionProvider_DML` a virtual adapter's index, which — per
`AccessViolationException`s observed at low indices on both test machines — can crash outright,
not just fail cleanly). The actual blocking defect is specifically that **a DXGI adapter handle
obtained by the probe subprocess does not remain valid by the time a separate, later process (the
real `vbr` run) opens "the same" index moments after** — a cross-process handle-lifetime issue,
not an index-selection issue. This creates a real architectural tension: crash safety requires
process isolation (a native access violation can't be caught any other way — see the earlier
"Fixed with real process isolation" finding), but DXGI adapter handles apparently don't survive
that isolation boundary reliably in this environment. Probing and constructing the real embedder
in the *same* process would sidestep the handle-lifetime issue but reintroduce the uncaught-crash
risk the subprocess design exists to prevent — not a decision to make unilaterally; the two
matter differently depending on how much running-out-of-process overhead this project is willing
to accept for real DirectML acceleration.

## Live-verified (2026-08-02, the probe itself was lying — corrected)

Investigating a specific question ("does the probe finding index N really mean N is safe to trust
later, or does adapter ordering change between the probe and real use?") led to systematically
testing every plausible mechanism difference between "the probe subprocess" (reports success) and
"the real embedder construction" (reports failure) — repeated same-process construction, spawning
a child probe then constructing in the parent, `dotnet run` vs the muxer vs the apphost `.exe`,
PowerShell vs a compiled console app, elapsed delay, thread-pool vs main thread, the full VBR.CLI
dependency graph loaded, and invocation through `System.CommandLine`'s real `InvokeAsync()`
pipeline. **Every single one of these succeeded when reproduced in isolation** — a result that
kept getting more suspicious as each hypothesis was ruled out, until testing the *actual* production
method (`VisualBumperMatcher.PrepareClip`/`MixedDensitySampler.EnsureEmbedder`) directly revealed
why: a diagnostic call reported "SUCCESS" (no exception) in the same breath as a logged DirectML
failure warning.

**Root cause of the whole confusing pattern: `OnnxEmbedder`'s constructor catches a DirectML
attach failure internally and falls back to a working CPU session with NO exception at all** —
by design, so a caller never hard-fails just because GPU inference wasn't available. But
`HardwareAcceleration.RunDirectMlProbe` (and therefore `ProbeDirectMlInSubprocess`, and therefore
every device-index-selection decision this ADR has been chasing since 2026-07-30) only ever
checked "did construction throw" as its success signal — never whether DirectML actually attached.
Since CPU fallback ALSO doesn't throw, **the probe had been reporting every non-crashing device
index as a false "success," on every machine, this entire investigation.** The "probe succeeds at
index 2, real construction fails at index 2" pattern that drove the cross-process-handle-lifetime
theory above was never actually a cross-process inconsistency — the probe was never testing the
right thing to begin with.

**Fixed**: `OnnxEmbedder` now exposes `UsedDirectML` (true only when `AppendExecutionProvider_DML`
itself succeeded, set right after that call, before the catch could ever run). `RunDirectMlProbe`
now returns nonzero when `UsedDirectML` is false, even though construction didn't throw — turning
"gracefully did not attach" into a real, distinguishable probe failure. Re-ran the real `vbr match`
command after this fix: it now correctly and honestly exhausts every candidate device index,
finds that DirectML does not attach at any of them, and falls back to CPU **before ever
constructing a real embedder** — one clean `Warning: DirectML acceleration did not initialize
correctly on this machine ... falling back to CPU inference` message, no more false "DirectML
acceleration ready" followed by a confusing mid-run failure. Build clean, `VBR.Tests` (72) and
`VDF.Core.Tests` (480) both pass.

This does not identify *why* DirectML fails to attach — that remains exactly as unexplained as
before. What it changes is confidence in the data: the "cross-process handle instability" theory,
the "same index behaves differently in different processes" observation, and the implied
architectural tension between crash-safety and handle lifetime (all in the section above) were
built on a false signal and should not be trusted as accurate characterizations of the underlying
DirectML failure. The only things that survive this correction as solid, re-confirmed facts: GPU
re-encode via ffmpeg genuinely works (verified independently of ONNX entirely); a real DirectML
init failure can crash the whole process uncatchably at some device indices (this is a hard crash,
not a false positive — a crashed process can't have printed a false "success"); and DirectML, at
every index tried so far, on both real machines tested, has never been confirmed to actually
attach.

## Open questions

- **Whether DirectML can attach at all, on any machine, given a probe that finally measures this
  correctly** — the entire premise of "device index 2 sort-of works" is retired per "Live-verified
  (2026-08-02)" above; no device index has been confirmed to actually engage DirectML anywhere.
  Re-running the corrected probe on the RTX 5080 machine (and any other future test machine) is the
  natural next step — it may reveal a genuinely different picture now that the signal is honest,
  rather than continuing to chase theories (cross-process handle lifetime, wrong API overload,
  redistributed vs. OS-provided `DirectML.dll`) built on the old, misleading "success" reports.
- GPU quality-flag numeric values (Decision 3) — starting guesses, not tuned.
- Whether `-preset`/`-global_quality`/`-quality` choices for the three GPU encoder families are
  well-chosen — same "not yet finalized" status ADR 0012 left its own CPU preset value in.
- win-arm64 DirectML support — not attempted.
- CUDA execution provider for ONNX inference — explicitly rejected for this pass (Decision 5),
  reaffirmed even after confirming CUDA 1.28.0 works on a real machine: the "download-free for
  every user" goal is unrelated to whether CUDA *can* work, and CUDA still requires a
  multi-GB, version-pinned toolkit install most users won't have. The maintainer's own machines
  would benefit from it, but this remains a diagnostic finding, not grounds to revisit Decision 5.
- Whether the GPU re-encode quality target should be independently exposed/configurable at all —
  no CLI surface for it exists (matches ADR 0012's own "no user-facing configuration in v1"
  stance), inherited here without re-litigating it.
