# ADR 0014: VBR.CLI redistribution — self-contained single-file publish

- **Status:** accepted, implemented and live-verified (2026-07-31).
- **Date:** 2026-07-31
- **Related:** [`0013-gpu-acceleration.md`](0013-gpu-acceleration.md) (the direct motivation —
  GPU/ONNX-DirectML behavior needs testing across machines with different hardware, not just the
  maintainer's own), [`0005-code-organization.md`](0005-code-organization.md) (the
  VBR.CLI → VBR.Core → VDF.Core project-reference chain this publish relies on)

## Context

Diagnosing ADR 0013's open DirectML issue needs data from machines other than the maintainer's
own — different GPUs, drivers, and Windows configurations. That requires a way to run VBR.CLI's
actual functionality on a machine with no development environment at all: no .NET SDK, no
repository checkout, ideally nothing to install beyond copying a file over.

VDF (the upstream project this is forked from) already publishes redistributables via
`.github/workflows/releases.yml` — Native AOT builds of `VDF.GUI`/`VDF.CLI`/`VDF.Web`, one per RID,
zipped per OS. VBR.CLI had no equivalent. The explicit ask was to reuse as much of that existing
mechanism as possible, without building or packaging any VDF artifact (GUI, Web, or VDF.CLI itself)
— VBR.CLI's own functionality only.

## Decision

**A new script, `publish-vbr-cli.ps1` (repo root), wraps `dotnet publish` for `VBR.CLI.csproj`
alone.** `VBR.CLI → VBR.Core → VDF.Core` is a plain `ProjectReference` chain (confirmed: no other
project in the solution is needed), so publishing `VBR.CLI.csproj` directly — without touching the
solution file or any GUI/Web project — is sufficient. This deliberately mirrors
`releases.yml`'s command shape (`dotnet publish <csproj> -c Release -r <rid> ...`) rather than
inventing a new build mechanism, per the "leverage VDF as much as possible" instruction.

**Default publish mode: self-contained + `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`,
not Native AOT — a fallback, not the preferred choice.** `PublishAot=true` (VDF.CLI's own proven
CI mode, same dependency set: System.CommandLine, MemoryPack, `Microsoft.ML.OnnxRuntime.Managed`)
was tried first and failed on the machine this was built on: `error : Platform linker not found` —
Native AOT needs the Visual Studio "Desktop development with C++" workload installed, which this
machine doesn't have. Installing a multi-GB C++ toolchain wasn't done unilaterally (a real system
change, not something to decide without asking). The script exposes `-Aot` as an opt-in for a
machine that *does* have the prerequisite — same command VDF.CLI's CI already runs successfully,
so no reason to expect it wouldn't also work here once the toolchain exists.

**The self-contained/PublishSingleFile fallback still meets the "single exe" goal.** Without
`IncludeNativeLibrariesForSelfExtract=true`, publish produced one `VBR.CLI.exe` (76MB) plus two
small native side-files (`MonoPosixHelper.dll`/`libMonoPosixHelper.dll`, from the
`Mono.Posix.NETStandard` package — not RID-filtered cleanly by that package, effectively unused on
Windows). Setting that flag folds them into the exe too (self-extracting to a temp cache at
startup): genuinely one file, ~78MB, self-contained (no .NET install required on the target
machine).

**A real, unrelated bug surfaced during the AOT attempt and was fixed regardless of which publish
mode ships:** `HardwareAcceleration.ProbeOneDevice` (`VBR.Core/Extraction/HardwareAcceleration.cs`)
reads `Assembly.GetEntryAssembly()?.Location` to re-invoke itself as a subprocess when launched via
the `dotnet` shared host — a call that returns empty under single-file publish generally (`IL3000`),
not just AOT. In practice this is a false-positive for our usage: that line only runs inside the
"`processPath`'s filename is literally `dotnet`" branch, which is unreachable once VBR.CLI is
published single-file (there, `Environment.ProcessPath` already *is* the app's own exe, so the
branch is skipped). Suppressed in `VBR.Core.csproj` with a comment explaining why, mirroring
`VDF.CLI.csproj`'s existing `IL2104`/`IL3053` suppression precedent for the same class of
trimmer-can't-prove-it-statically warning.

**ffmpeg is not bundled by default; `-BundleFfmpeg` is opt-in.** Unlike VDF.GUI/VDF.Web (which
have `FfmpegDownloader`), VBR.CLI never auto-downloads ffmpeg/ffprobe — it only *locates* an
existing one (`VDF.Core/FFTools/FFToolsUtils.cs GetPath`: next to the exe, in a `bin\` subfolder
next to the exe, or on PATH). A target machine either needs ffmpeg already present one of those
ways, or `-BundleFfmpeg` copies the *publishing* machine's own copy into the output's `bin\`. Off
by default deliberately — it copies a third-party binary into the package rather than just this
project's own build output, worth a conscious choice rather than a silent default. **Live-verified
gotcha:** on the machine this was built on, ffmpeg was installed via Chocolatey, whose `ffmpeg.exe`
on PATH is a ~390KB redirect shim (not the real ~100MB binary) that locates the real exe via a path
relative to *its own* original location — copying the shim elsewhere breaks it silently. The script
detects this heuristically (anything under 5MB is almost certainly a shim, not a real ffmpeg build)
and warns instead of bundling a broken exe; `-FfmpegPath`/`-FfprobePath` let the real binaries be
pointed at explicitly.

**AI components (ONNX runtime, DirectML runtime, the DINOv2 model) are not bundled either** — same
as every other build, VBR downloads those into its own state folder
(`VDF.Core/AI/AiComponents.cs`) on first use. Each target machine needs network access once, the
first time it runs a matching/scanning command. This wasn't worth special-casing for
redistribution: it's the existing, already-tested acquisition path, and baking a ~100MB download
into the package would work against the "small, easy to copy around" goal without buying much
(machines being used for benchmarking already need network access to receive the exe itself).

## Consequences

Positive: one command (`publish-vbr-cli.ps1`) produces a genuinely single, self-contained ~78MB
`.exe` that runs on a bare Windows machine with nothing pre-installed but ffmpeg (or
`-BundleFfmpeg` to skip even that). Live-verified end-to-end on a synthetic test clip in a
clean-room folder containing *only* the published output (no reliance on the dev machine's PATH or
any other installed tooling): AI component download, DirectML probing (reproducing the same known
device-index behavior from ADR 0013), CPU fallback, ffmpeg frame extraction, and a correct visual
match all worked from the packaged exe exactly as they do from a `dotnet run` invocation. Reuses
VDF's own proven publish command shape rather than inventing a new mechanism.

Negative / watch-outs: the fallback publish mode (not Native AOT) means ~78MB instead of the
smaller/faster-starting AOT binary VDF.CLI ships; revisit once the C++ toolchain prerequisite is
available. `-BundleFfmpeg`'s shim-detection is a size heuristic (under 5MB), not a real shim
detector — a genuinely tiny non-shim ffmpeg build (unlikely, but not impossible) would be
incorrectly rejected; `-FfmpegPath`/`-FfprobePath` are the escape hatch either way. Every target
machine still needs its own network access for the first AI-component/DirectML-runtime download,
which also means results from a machine with no internet access, or hitting the download partway
through a benchmarking session, won't be directly comparable without accounting for that one-time
cost.

## Open questions

- Whether to add Native AOT to the script as CI-driven (a proper GitHub Actions job alongside
  `releases.yml`'s existing GUI/CLI/Web matrix) once there's a machine with the C++ toolchain
  available — not attempted here; this ADR only covers the local, on-demand publish path the
  immediate benchmarking need requires.
- Whether other RIDs (`win-arm64`, `linux-x64`, etc.) are worth publishing given this project's
  GPU-acceleration work (ADR 0013) is Windows-focused — the script accepts `-Rid` for any target
  `dotnet publish` supports, but only `win-x64` has been live-verified.
