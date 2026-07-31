<#
Standalone ONNX Runtime + DirectML/CUDA smoke test -- independent of VBR/VDF entirely, using
model/runtime files sitting in VBR's build output folder (DirectML/CPU were downloaded by VBR
itself; the CUDA native runtime was pulled once by hand into ai\cuda\ for this script -- VBR
itself never downloads or ships CUDA, per docs/decisions/0013-gpu-acceleration.md's explicit
"DirectML only, no CUDA" decision for the shipped product. This script is diagnostic-only.)

CUDA prerequisite: unlike DirectML, the CUDA execution provider needs a system-installed NVIDIA
CUDA Toolkit + cuDNN already present (roughly CUDA 12.x + cuDNN 9.x for this ONNX Runtime
version) -- just having a driver capable of nvenc/nvdec (which is all the ffmpeg GPU paths need)
is NOT sufficient. If that's not installed, expect a clean "provider shared library failed to
load" style error, not a crash.

IMPORTANT: run each mode as a SEPARATE `pwsh -File ...` invocation, not back-to-back in one
session. Two reasons:
  1. Windows resolves a bare-named native DLL (e.g. "onnxruntime.dll") to an ALREADY-LOADED
     module of that name regardless of which folder it came from -- so testing two modes in the
     same process would silently reuse whichever native library loaded first.
  2. A real DirectML (or CUDA) init failure on some hardware/config can be an uncatchable native
     access violation -- if that happens here, THIS WHOLE pwsh PROCESS WILL DIE with no dialog,
     no stack trace, just the window closing/returning to prompt. That's not a bug in the
     script; it's the exact failure mode VBR's own probe isolates into a subprocess for.
     Losing one throwaway pwsh process is harmless and is itself diagnostic (crash = bad index).

Usage (run these one at a time, as separate invocations):
  pwsh -File test-onnx-directml.ps1 -Mode Cpu
  pwsh -File test-onnx-directml.ps1 -Mode DirectML -DeviceId 0   # ...through 4
  pwsh -File test-onnx-directml.ps1 -Mode Cuda -DeviceId 0
(VBR's own probe found index 2 "succeeds" in isolation on this machine but the real process
then gets a clean "invalid adapter handle" error at that same index -- see if you get the
same result here, or something different. CUDA has no such history here yet -- this is the
first test of it on this machine.)
#>
param(
    [ValidateSet("Cpu", "DirectML", "Cuda")]
    [string]$Mode = "Cpu",
    [int]$DeviceId = 0,
    [int]$Iterations = 30,
    # Defaults to the folder this script itself lives in -- when copied alongside the "loose"
    # publish layout (publish-vbr-cli.ps1 -Layout Loose), Microsoft.ML.OnnxRuntime.dll, ai\, and
    # this script all sit in the same folder, so no path needs to be passed on another machine.
    # Override explicitly for local dev use straight from a build output folder.
    [string]$Root = $PSScriptRoot,
    # Overrides for testing a DIFFERENT ONNX Runtime version than whatever's under ai\ -- e.g. a
    # newer Microsoft.ML.OnnxRuntime.Gpu.Windows package to check whether a GPU-architecture kernel
    # gap (a real, well-documented thing: official ORT CUDA builds have historically lagged behind
    # brand-new NVIDIA architectures -- see the cudaErrorNoKernelImageForDevice note below) is fixed
    # in a later release, without needing yet another -Mode value or folder-naming convention for
    # every version anyone might want to try. Both must point at files built for the SAME ORT
    # version as each other (mismatched managed/native versions is its own separate failure mode --
    # already ruled out as this project's root cause on the original dev machine, but still a real
    # thing to avoid introducing by accident here).
    [string]$NativeFolder,
    [string]$ManagedDllPath
)

$root = $Root

$env:Path += ";D:\Data\InvokeML\bin\.venv\Lib\site-packages\torch\lib"

$aiFolder = Join-Path $root "ai"
$managedDll = if ($ManagedDllPath) { $ManagedDllPath } else { Join-Path $root "Microsoft.ML.OnnxRuntime.dll" }
$modelPath = Join-Path $aiFolder "dinov2-small-int8.onnx"
$nativeFolder = if ($NativeFolder) {
    $NativeFolder
}
else {
    switch ($Mode) {
        "DirectML" { Join-Path $aiFolder "directml" }
        "Cuda"     { Join-Path $aiFolder "cuda" }
        default    { $aiFolder }
    }
}

$onnxRuntimeDllPath = Join-Path $nativeFolder "onnxruntime.dll"

if (-not (Test-Path $modelPath))   { throw "Model not found at $modelPath -- run 'vbr scan' or 'vbr match' at least once first so VBR downloads it." }
if (-not (Test-Path $managedDll))  { throw "Managed wrapper not found at $managedDll." }
if (-not (Test-Path $nativeFolder)) { throw "Native runtime folder not found at $nativeFolder." }
if (-not (Test-Path $onnxRuntimeDllPath)) { throw "onnxruntime.dll not found at $onnxRuntimeDllPath." }

# Resolve to absolute before comparing against the loaded module's path below -- Process.Modules
# always reports an absolute path, so a relative -NativeFolder (e.g. "ai\cuda128") would otherwise
# always "mismatch" a perfectly correct load and print a false "not trustworthy" warning.
$onnxRuntimeDllPath = (Resolve-Path $onnxRuntimeDllPath).Path

# Adding the folder to PATH is NOT enough on its own: a bare-name LoadLibrary("onnxruntime")
# checks the loading process's own directory and System32 BEFORE PATH, so whichever
# onnxruntime.dll happens to live/resolve there first wins -- live-verified this actually
# happens (a plain "-Mode Cpu" run crashed with an AccessViolationException in
# NativeMethods..cctor, an ABI mismatch, because some OTHER onnxruntime.dll got loaded
# instead of the one this script intends). Explicitly loading the exact file by full path
# FIRST fixes this: Windows resolves any later bare-name lookup of the same filename to the
# module already loaded at that path, so this one call pins the entire rest of the process to
# the intended file, regardless of what else is discoverable on PATH/System32.
[System.Runtime.InteropServices.NativeLibrary]::Load($onnxRuntimeDllPath) | Out-Null
$loadedModule = [System.Diagnostics.Process]::GetCurrentProcess().Modules |
    Where-Object { $_.ModuleName -ieq "onnxruntime.dll" } | Select-Object -First 1
Write-Host "Loaded onnxruntime.dll from: $($loadedModule.FileName)"
if ($loadedModule.FileName -ine $onnxRuntimeDllPath) {
    Write-Host "WARNING: that does not match the intended path ($onnxRuntimeDllPath) -- results below are not trustworthy."
}

# The rest (onnxruntime_providers_shared.dll, and DirectML.dll / onnxruntime_providers_cuda.dll
# for those modes) are loaded on demand by native code inside onnxruntime.dll itself, not by
# .NET P/Invoke resolution -- those names are unique per mode (no cross-mode collision like
# onnxruntime.dll has), so plain PATH search for them is fine.
$env:PATH = "$nativeFolder;$env:PATH"

Add-Type -Path $managedDll

Write-Host "Mode: $Mode$(if ($Mode -in @('DirectML', 'Cuda')) { " (device index $DeviceId)" })"
Write-Host "Native runtime folder: $nativeFolder"

$options = New-Object Microsoft.ML.OnnxRuntime.SessionOptions
$epAttached = $true
if ($Mode -eq "DirectML") {
    # Same two settings VBR's own OnnxEmbedder sets -- documented DirectML EP requirement. Wrapped
    # in try/catch exactly like OnnxEmbedder.cs does, deliberately: AppendExecutionProvider_DML can
    # throw a catchable RuntimeException on its own (live-verified: "invalid adapter handle" at
    # this exact call, on this exact machine) WITHOUT that exception ever reaching the
    # InferenceSession construction below. PowerShell treats an uncaught .NET exception from a
    # method call as a non-terminating error by default -- the script would otherwise print it and
    # sail on to construct a plain CPU-only session (no DML attached at all) and misreport that as
    # "no crash, no exception for DirectML", which is what this script actually did before this
    # fix was added.
    try {
        $options.EnableMemoryPattern = $false
        $options.ExecutionMode = [Microsoft.ML.OnnxRuntime.ExecutionMode]::ORT_SEQUENTIAL
        $options.AppendExecutionProvider_DML($DeviceId)
    }
    catch {
        $epAttached = $false
        Write-Host "AppendExecutionProvider_DML FAILED (device $DeviceId) -- catchable, not a crash:"
        Write-Host $_.Exception.Message
        Write-Host "Continuing to construct the session anyway, WITHOUT DirectML attached (CPU only) -- this tells you nothing about DirectML working, only that plain CPU inference still does."
    }
}
elseif ($Mode -eq "Cuda") {
    try {
        $options.AppendExecutionProvider_CUDA($DeviceId)
    }
    catch {
        $epAttached = $false
        Write-Host "AppendExecutionProvider_CUDA FAILED (device $DeviceId) -- catchable, not a crash:"
        Write-Host $_.Exception.Message
        Write-Host "Continuing to construct the session anyway, WITHOUT CUDA attached (CPU only) -- this tells you nothing about CUDA working, only that plain CPU inference still does."
    }
}

Write-Host "Constructing InferenceSession..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $session = New-Object Microsoft.ML.OnnxRuntime.InferenceSession($modelPath, $options)
}
catch {
    $sw.Stop()
    Write-Host "FAILED after $($sw.ElapsedMilliseconds) ms -- this is a CATCHABLE failure (not a crash):"
    Write-Host $_.Exception.Message
    exit 1
}
$sw.Stop()
if ($Mode -ne "Cpu" -and -not $epAttached) {
    Write-Host "Session ready in $($sw.ElapsedMilliseconds) ms -- but running on CPU, NOT $Mode (the execution provider failed to attach above)."
}
else {
    Write-Host "Session ready in $($sw.ElapsedMilliseconds) ms -- no crash, no exception for $Mode$(if ($Mode -in @('DirectML', 'Cuda')) { " device $DeviceId" })."
}

# --- Optional: timing comparison (dummy zeroed input -- fine for raw throughput, DINOv2 has
#     no data-dependent branching, but it is NOT a correctness test) ---
try {
    $inputName = ($session.InputMetadata.Keys | Select-Object -First 1)
    $tensor = [Microsoft.ML.OnnxRuntime.Tensors.DenseTensor[float]]::new([int[]]@(1, 3, 224, 224))
    $namedInput = [Microsoft.ML.OnnxRuntime.NamedOnnxValue]::CreateFromTensor[float]($inputName, $tensor)
    $inputs = [Microsoft.ML.OnnxRuntime.NamedOnnxValue[]]@($namedInput)

    $null = $session.Run($inputs)  # warm-up, excluded from timing (pays one-time init cost)

    $sw.Restart()
    for ($i = 0; $i -lt $Iterations; $i++) {
        $results = $session.Run($inputs)
        $results.Dispose()
    }
    $sw.Stop()
    $perCall = [math]::Round($sw.ElapsedMilliseconds / $Iterations, 2)
    Write-Host "$Iterations inferences: $($sw.ElapsedMilliseconds) ms total, $perCall ms/call"
}
catch {
    Write-Host "(Timing loop failed -- session construction above is the main result; error was: $($_.Exception.Message))"
}
finally {
    $session.Dispose()
}
