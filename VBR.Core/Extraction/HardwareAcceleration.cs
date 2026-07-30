// /*
//     Copyright (C) 2026 mawcs
//     This file is part of VideoBumperRemover
//     VideoBumperRemover is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoBumperRemover is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoBumperRemover.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.FFTools;

namespace VBR.Core.Extraction;

/// <summary>
/// The single knob for every GPU code path in VBR (docs/decisions/0013-gpu-acceleration.md):
/// ffmpeg decode <c>-hwaccel</c> (this class' own <see cref="Mode"/>, forwarded to VDF's existing
/// <see cref="FfmpegEngine.HardwareAccelerationMode"/>), GPU re-encode (<see cref="Removal.GpuEncoderProbe"/>,
/// only attempted when <see cref="Mode"/> isn't <see cref="FFHardwareAccelerationMode.none"/>), and
/// ONNX DirectML inference (<c>VDF.Core.AI.OnnxEmbedder</c>, same gating). One value means "how much
/// GPU should we try to use," not three independent flags — every layer falls back to today's exact
/// CPU behavior silently on any failure, so setting this to anything other than <c>none</c> is
/// always safe to try.
///
/// <see cref="FfmpegEngine"/> is an <c>internal</c> VDF.Core type — visible to <c>VBR.Core</c> via
/// the existing <c>InternalsVisibleTo</c> grant (ADR 0005), but not to <c>VBR.CLI</c>, which is why
/// this thin public bridge exists (same shape as <c>AudioBumperMatcher.ExtractFingerprint</c>'s
/// bridge to VDF.Core's <c>internal</c> <c>ChromaprintEngine</c>).
/// </summary>
public static class HardwareAcceleration {
	/// <summary>Global for this process, same convention as <see cref="FfmpegEngine.HardwareAccelerationMode"/>
	/// itself — set once at CLI startup, read from every ffmpeg-invoking call site thereafter. Not
	/// meant to change mid-run (one `vbr` invocation does one thing).</summary>
	public static FFHardwareAccelerationMode Mode {
		get => FfmpegEngine.HardwareAccelerationMode;
		set => FfmpegEngine.HardwareAccelerationMode = value;
	}

	/// <summary>True whenever any GPU path should be attempted — every call site guards on this
	/// rather than comparing against <see cref="FFHardwareAccelerationMode.none"/> directly, so the
	/// "what counts as GPU-enabled" rule lives in exactly one place.</summary>
	public static bool Enabled => Mode != FFHardwareAccelerationMode.none;

	static bool directMlUnavailable;

	/// <summary>True when ONNX inference should request the DirectML execution provider
	/// (docs/decisions/0013-gpu-acceleration.md) — Windows only; CUDA/other <see cref="Mode"/>
	/// values still map to DirectML here too, since DirectML is the only ONNX GPU backend this
	/// project targets (no CUDA execution provider support, by design — see that ADR). False for
	/// the rest of this process once <see cref="MarkDirectMlUnavailable"/> has been called (e.g.
	/// the runtime failed to download) — every subsequent caller falls back to CPU inference
	/// without re-attempting a download that already failed once this run.</summary>
	public static bool PreferDirectML => Enabled && OperatingSystem.IsWindows() && !directMlUnavailable;

	/// <summary>Called after a DirectML acquisition/initialization failure so the rest of this
	/// process's callers stop asking for it (docs/iterativeplan.md-style "fall back once, stay
	/// fallen back" — matches every other GPU layer's silent-fallback contract, just remembered at
	/// process scope instead of re-discovered per call).</summary>
	public static void MarkDirectMlUnavailable() => directMlUnavailable = true;

	/// <summary>The hidden first argument that routes a self-invocation into <see cref="RunDirectMlProbe"/>
	/// instead of the normal CLI parse — see <see cref="ProbeDirectMlInSubprocess"/>'s doc comment
	/// for why this exists at all.</summary>
	public const string DirectMlProbeArgument = "--internal-probe-directml";

	/// <summary>
	/// Verifies DirectML actually initializes successfully by running the exact same
	/// <c>OnnxEmbedder(modelPath, preferDirectML: true)</c> construction <em>in a separate child
	/// process</em>, not this one — live-verified (2026-07-30) to be necessary, not defensive
	/// overkill: on a machine with no real GPU/display driver, <c>AppendExecutionProvider_DML</c>
	/// followed by <c>InferenceSession</c> construction crashed with a native access violation
	/// (0xC0000005) that bypassed every managed <c>try</c>/<c>catch</c> up the stack, including
	/// <c>OnnxEmbedder</c>'s own — a native crash cannot be caught from managed code at all, only
	/// isolated by process boundary, the same reason <see cref="Removal.GpuEncoderProbe"/> probes
	/// ffmpeg encoders out-of-process too. The child process is this same executable, re-invoked
	/// with <see cref="DirectMlProbeArgument"/> as a hidden first argument (handled at the very top
	/// of <c>Program.Main</c>, before normal CLI parsing) — its own crash or nonzero exit only ever
	/// affects the probe, never the real run.
	/// </summary>
	public static bool ProbeDirectMlInSubprocess(string modelPath, CancellationToken ct = default) {
		try {
			string? processPath = Environment.ProcessPath;
			if (string.IsNullOrEmpty(processPath)) return false;

			var psi = new ProcessStartInfo {
				FileName = processPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			// `dotnet run`/`dotnet exec` launches via the shared "dotnet" host -- re-invoking just
			// the host path would run the SDK CLI, not this app, so the entry assembly's own DLL
			// has to be passed as dotnet's first argument in that case. A published self-contained
			// exe has no such indirection: processPath already IS the app.
			if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
				string? assemblyLocation = Assembly.GetEntryAssembly()?.Location;
				if (string.IsNullOrEmpty(assemblyLocation)) return false;
				psi.ArgumentList.Add(assemblyLocation);
			}
			psi.ArgumentList.Add(DirectMlProbeArgument);
			psi.ArgumentList.Add(modelPath);

			using var process = new Process { StartInfo = psi };
			process.Start();
			// Drained concurrently, same deadlock-avoidance rationale as every other ffmpeg/child
			// process call in this codebase.
			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
			Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
			// DirectML/D3D12 device init is typically sub-second; 30s is generous slack for a cold
			// driver, not an expectation of how long this normally takes.
			if (!process.WaitForExit(30_000)) {
				try { process.Kill(entireProcessTree: true); } catch { }
				return false;
			}
			Task.WaitAll(stdoutTask, stderrTask);
			return process.ExitCode == 0;
		}
		catch {
			return false;
		}
	}

	/// <summary>The child-process entry point <see cref="ProbeDirectMlInSubprocess"/> invokes —
	/// called from <c>VBR.CLI.Program</c>'s own hidden-argument check, before normal CLI parsing,
	/// so a crash here never touches System.CommandLine or any real command's state. Constructs
	/// exactly what a real run would construct; a clean 0 means DirectML is safe to trust this
	/// process's HardwareAcceleration state after the parent reads this exit code.</summary>
	public static int RunDirectMlProbe(string modelPath) {
		try {
			VDF.Core.AI.AiComponents.EnsureResolverInstalled(preferDirectML: true);
			using var embedder = new VDF.Core.AI.OnnxEmbedder(modelPath, preferDirectML: true);
			return 0;
		}
		catch {
			return 1;
		}
	}
}
