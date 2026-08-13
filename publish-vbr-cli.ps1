<#
Builds a redistributable, self-contained VBR.CLI package for copying to other machines --
originally for benchmarking GPU/ONNX hardware-acceleration performance across different hardware
(docs/decisions/0013-gpu-acceleration.md). See docs/decisions/0014-vbr-cli-redistribution.md for
the decisions behind this script's defaults.

Deliberately does NOT build or package any other VDF/VBR artifact (VDF.GUI, VDF.Web, etc.) --
VBR.CLI's own ProjectReference chain (VBR.CLI -> VBR.Core -> VDF.Core) already pulls in
everything this project's CLI functionality needs; nothing else in the solution is required.

Two layouts, for two different purposes:
  -Layout SingleFile (default): one .exe, nothing else -- for just running `vbr` commands on
   another machine. Microsoft.ML.OnnxRuntime.dll and the native runtimes are bundled INSIDE the
   exe (self-extracting to a temp cache at startup), so there's no loose Microsoft.ML.OnnxRuntime.dll
   on disk for test-onnx-directml.ps1 to Add-Type -Path against.
  -Layout Loose: VBR.CLI.exe plus its managed DLLs sitting loose next to it (no bundling) -- for
   running test-onnx-directml.ps1 on another machine. Also copies that script itself, plus this
   machine's already-downloaded ai\ folder (model + CPU/DirectML/CUDA runtimes, whichever are
   present locally), into the output root, so the pair is immediately self-sufficient on a target
   machine with no network access and no prior `vbr` run to trigger the normal auto-download.

Usage:
  pwsh -File publish-vbr-cli.ps1                       # SingleFile -> .\publish\vbr-cli-win-x64\
  pwsh -File publish-vbr-cli.ps1 -Layout Loose          # -> .\publish\vbr-cli-win-x64-loose\
  pwsh -File publish-vbr-cli.ps1 -Rid win-arm64
  pwsh -File publish-vbr-cli.ps1 -BundleFfmpeg          # see ffmpeg note below
  pwsh -File publish-vbr-cli.ps1 -Zip                   # also produce a .zip of the output
  pwsh -File publish-vbr-cli.ps1 -Aot                   # see Native AOT note below (SingleFile only)

Native AOT (-Aot, SingleFile layout only): smaller/faster-starting exe, same mechanism VDF.CLI's
own release workflow (.github/workflows/releases.yml) already uses successfully for the same
dependency set (System.CommandLine, MemoryPack, Microsoft.ML.OnnxRuntime.Managed). Requires the
Visual Studio "Desktop development with C++" workload (the platform linker) --
see https://aka.ms/nativeaot-prerequisites. Not installed on the machine this script was written
on, so the SingleFile default (self-contained + PublishSingleFile +
IncludeNativeLibrariesForSelfExtract) needs no extra prerequisites and still produces exactly one
.exe (~78MB on win-x64) that does NOT require .NET to be installed on the target machine.

ffmpeg note (-BundleFfmpeg): unlike VDF.GUI/VDF.Web, VBR.CLI never auto-downloads ffmpeg/ffprobe
-- it only *locates* an existing one (next to the exe, in a bin\ subfolder next to the exe, or on
PATH -- see VDF.Core/FFTools/FFToolsUtils.cs GetPath). Target machines need ffmpeg available one
of those ways. -BundleFfmpeg copies THIS machine's own ffmpeg.exe/ffprobe.exe (found via PATH)
into the output's bin\ subfolder for convenience -- off by default since that copies a
third-party binary into the package rather than just this project's own build output; on for
personal machine-to-machine copying, it's your own already-licensed local install.

AI/ONNX runtime and DirectML native libraries, and the DINOv2 model itself, are NOT bundled into
the SingleFile layout -- VBR downloads those into its own state folder on first run, same as any
other build (see VDF.Core/AI/AiComponents.cs). Each target machine needs network access once, the
first time it runs any AI-matching command. The Loose layout DOES copy them (see above), since
that layout's whole point is running test-onnx-directml.ps1 without needing that first run.
#>
param(
    [string]$Rid = "win-x64",
    [ValidateSet("SingleFile", "Loose")]
    [string]$Layout = "SingleFile",
    [switch]$Aot,
    [switch]$BundleFfmpeg,
    [string]$FfmpegPath,
    [string]$FfprobePath,
    [switch]$Zip,
    [string]$OutputRoot = (Join-Path $PSScriptRoot "publish")
)

if ($Aot -and $Layout -eq "Loose") { throw "-Aot and -Layout Loose don't mix: Native AOT has no loose managed DLLs to point test-onnx-directml.ps1 at (everything is compiled into the native exe)." }

$ErrorActionPreference = "Stop"
$dirSuffix = if ($Layout -eq "Loose") { "-loose" } else { "" }
$outDir = Join-Path $OutputRoot "vbr-cli-$Rid$dirSuffix"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$publishArgs = @(
    "$PSScriptRoot/VBR.CLI/VBR.CLI.csproj", "-c", "Release", "-v", "q",
    "--self-contained", "-r", $Rid, "-o", $outDir
)
if ($Aot) {
    $publishArgs += "-p:PublishAot=true"
}
elseif ($Layout -eq "SingleFile") {
    $publishArgs += "-p:PublishSingleFile=true"
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
}
# Loose (non-AOT): no extra publish properties -- plain self-contained folder publish already
# leaves Microsoft.ML.OnnxRuntime.dll and everything else as loose files next to the exe.

Write-Host "Publishing VBR.CLI ($Rid, $Layout$(if ($Aot) { ' / Native AOT' }))..."
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# vbr.config.json (docs/iterativeplan.md, "File-path DB options" entry, Part 3) is discovered via
# the CURRENT directory at runtime, not the exe's own folder -- copying it into the output root
# means a target machine running vbr-cli.exe from inside this published folder (the common case for
# both layouts, e.g. test-onnx-directml.ps1's own benchmarking workflow) picks it up automatically.
# Warns rather than throws: an absent config file is a valid state (built-in defaults apply), same
# convention as the ai\ folder's own "skip if not found" handling below.
$configSrc = Join-Path $PSScriptRoot "vbr.config.json"
if (Test-Path $configSrc) {
    Copy-Item $configSrc $outDir -Force
    Write-Host "Copied vbr.config.json into the output."
}
else {
    Write-Warning "vbr.config.json not found at $configSrc -- skipping. The published exe will use built-in defaults until one is placed alongside it."
}

if ($Layout -eq "Loose") {
    $testScriptSrc = Join-Path $PSScriptRoot "test-onnx-directml.ps1"
    if (Test-Path $testScriptSrc) {
        Copy-Item $testScriptSrc $outDir -Force
        Write-Host "Copied test-onnx-directml.ps1 into the output (run it there -- it defaults to its own folder)."
    }
    else {
        Write-Warning "test-onnx-directml.ps1 not found at $testScriptSrc -- skipping."
    }

    # Prefer whichever local build output's ai\ folder is more complete (has directml/cuda
    # subfolders already populated from earlier local testing), Debug over Release since that's
    # what `dotnet run` populates day-to-day on this machine.
    $aiCandidates = @(
        (Join-Path $PSScriptRoot "VBR.CLI/bin/Debug/net10.0/ai"),
        (Join-Path $PSScriptRoot "VBR.CLI/bin/Release/net10.0/ai")
    ) | Where-Object { Test-Path $_ }
    $aiSrc = $aiCandidates | Select-Object -First 1
    if ($aiSrc) {
        Copy-Item $aiSrc (Join-Path $outDir "ai") -Recurse -Force
        Write-Host "Copied ai\ (model + runtimes) from $aiSrc"
    }
    else {
        Write-Warning "No local ai\ folder found (run 'vbr match'/'vbr scan' at least once first so there's something to copy) -- test-onnx-directml.ps1 will need network access on the target machine to self-download on first use instead."
    }
}

if ($BundleFfmpeg) {
    $resolvedFfmpeg = $FfmpegPath
    $resolvedFfprobe = $FfprobePath
    if (-not $resolvedFfmpeg) { $resolvedFfmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source }
    if (-not $resolvedFfprobe) { $resolvedFfprobe = (Get-Command ffprobe -ErrorAction SilentlyContinue)?.Source }

    # Chocolatey (and some other package managers) installs a tiny redirect "shim" exe on PATH,
    # not the real binary -- copying the shim elsewhere breaks it, since it locates the real
    # exe via a path relative to ITS OWN original location. Live-verified: choco's ffmpeg.exe
    # shim is ~390KB; a real ffmpeg build is 60-150MB. Anything implausibly small is almost
    # certainly a shim, not a false positive -- warn instead of silently bundling a broken exe.
    $shimSizeThresholdBytes = 5MB
    function Test-LooksLikeShim($path) {
        return (Test-Path $path) -and ((Get-Item $path).Length -lt $shimSizeThresholdBytes)
    }

    if (-not $resolvedFfmpeg -or -not $resolvedFfprobe) {
        Write-Warning "ffmpeg/ffprobe not found on this machine's PATH -- skipping bundle. Pass -FfmpegPath/-FfprobePath explicitly if they're installed somewhere PATH doesn't cover."
    }
    elseif ((Test-LooksLikeShim $resolvedFfmpeg) -or (Test-LooksLikeShim $resolvedFfprobe)) {
        Write-Warning "'$resolvedFfmpeg' and/or '$resolvedFfprobe' looks like a package-manager shim (under 5MB), not the real binary -- skipping bundle. Find the real one (e.g. for Chocolatey: C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin\) and pass it via -FfmpegPath/-FfprobePath."
    }
    else {
        $binDir = Join-Path $outDir "bin"
        New-Item -ItemType Directory -Force -Path $binDir | Out-Null
        Copy-Item $resolvedFfmpeg (Join-Path $binDir "ffmpeg.exe") -Force
        Copy-Item $resolvedFfprobe (Join-Path $binDir "ffprobe.exe") -Force
        Write-Host "Bundled ffmpeg/ffprobe from $resolvedFfmpeg"
    }
}

# Debug symbols -- useful locally, dead weight on a benchmarking target machine.
Get-ChildItem $outDir -Filter "*.pdb" | Remove-Item -Force

if ($Zip) {
    $zipPath = "$outDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath
    Write-Host "Zipped: $zipPath"
}

Write-Host "`nDone: $outDir"
Get-ChildItem $outDir -Recurse | ForEach-Object {
    [PSCustomObject]@{ File = $_.FullName; SizeMB = [math]::Round($_.Length / 1MB, 1) }
} | Format-Table -AutoSize
