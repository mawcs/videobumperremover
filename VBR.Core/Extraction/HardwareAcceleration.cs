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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VBR.Core.Diagnostics;
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

	/// <summary>Forwards to <see cref="FfmpegEngine.UseNativeBinding"/> — VBR's own knob for the
	/// native FFmpeg.AutoGen decode path (docs/decisions/0015-native-ffmpeg-binding.md), separate
	/// from <see cref="Mode"/>/<see cref="Enabled"/> (GPU device acceleration): this is about
	/// avoiding a spawned <c>ffmpeg.exe</c> process per sampling call, not about which device does
	/// the decoding. **Defaults to <c>true</c>** (decided 2026-08-03, an explicit reversal of that
	/// ADR's original "default off" draft) — every command sets this once at startup regardless of
	/// whether the user passed the CLI flag, same convention as <see cref="Mode"/>.</summary>
	public static bool NativeFfmpegBinding {
		get => FfmpegEngine.UseNativeBinding;
		set => FfmpegEngine.UseNativeBinding = value;
	}

	/// <summary>
	/// Prints one unconditional console line (stderr, same "Note:" convention every other
	/// unconditional CLI note in this project already uses) reporting the ffmpeg hardware
	/// acceleration this run requested for decode, and whether the native FFmpeg.AutoGen binding
	/// (<see cref="NativeFfmpegBinding"/>) is active. Called once per command (<c>scan</c>/
	/// <c>match</c>/<c>remove</c>/<c>add-bumper</c>), right after <see cref="Mode"/>/
	/// <see cref="NativeFfmpegBinding"/> are set from the parsed CLI options — docs/iterativeplan.md's
	/// 2026-08-05 entry, Issue 1, Option A: dogfooding surfaced that nothing told a user whether
	/// <c>-hwaccel</c> was actually being passed to ffmpeg at all, short of reading the source or
	/// passing <c>--verbose</c> and grepping the logged command line.
	///
	/// <b>Deliberately reports only what this process is CERTAIN of</b> — the flag it's about to
	/// pass, and whether native binding is active — not whether ffmpeg actually engaged hardware
	/// decode. <c>-hwaccel auto</c> (and named modes) can silently fall back to software decode
	/// with no distinct exit code or exception, so claiming "engaged" here would be exactly the
	/// kind of unverified success signal this project's own GPU work has already been burned by
	/// twice (<c>GpuEncoderProbe</c> replacing a static encoder-list check with a real probe encode;
	/// <c>RunDirectMlProbe</c>'s 2026-08-02 fix, after discovering "construction didn't throw" was
	/// never proof DirectML actually attached — see docs/decisions/0013-gpu-acceleration.md).
	/// Closing that same gap for decode (a real probe attempting an actual accelerated decode) is
	/// the iterative-plan entry's Option B — a separate, bigger step, not attempted here.
	///
	/// GPU re-encode is the one ffmpeg layer that IS independently confirmed, because
	/// <see cref="Removal.GpuEncoderProbe"/> already probes a real encode rather than trusting a
	/// flag — see <see cref="Removal.ClipRemover"/>'s own per-file console line, which reports the
	/// actual resolved encoder and whether it's GPU or CPU, not just what was requested.
	/// </summary>
	public static void ReportDecodeRequest() {
		string decode = Mode == FFHardwareAccelerationMode.none
			? "disabled (CPU decode only)"
			: $"\"{Mode}\" requested for decode (not independently confirmed here -- ffmpeg can " +
				"silently fall back to software; GPU re-encode, when remove re-encodes a match, IS " +
				"confirmed and reported separately per file)";
		Console.Error.WriteLine($"Note: ffmpeg hardware acceleration -- {decode}. Native ffmpeg binding: " +
			$"{(NativeFfmpegBinding ? "on" : "off")}.");
	}

	static bool directMlUnavailable;

	/// <summary>Which DXGI adapter index actually initializes DirectML successfully on this
	/// machine — set by <see cref="ProbeDirectMlInSubprocess"/> once it finds one. **Device 0 is
	/// not a safe default** (see that method's doc comment): live-verified that a remote-session
	/// display adapter can enumerate at index 0, ahead of the real GPU, while falsely advertising
	/// full D3D12 feature-level support. Real `OnnxEmbedder` construction (as opposed to the probe
	/// itself) should always pass this value, never a hardcoded 0.</summary>
	public static int DirectMlDeviceId { get; private set; }

	/// <summary>True when ONNX inference should request the DirectML execution provider
	/// (docs/decisions/0013-gpu-acceleration.md) — Windows only; CUDA/other <see cref="Mode"/>
	/// values still map to DirectML here too, since DirectML is the only ONNX GPU backend this
	/// project targets (no CUDA execution provider support, by design — see that ADR). False for
	/// the rest of this process once <see cref="MarkDirectMlUnavailable"/> has been called (e.g.
	/// the runtime failed to download, or no device index initialized successfully) — every
	/// subsequent caller falls back to CPU inference without re-attempting something that already
	/// failed once this run.</summary>
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

	// Generous relative to any real machine's adapter count -- live-verified (2026-07-30) that
	// device 0 can be a phantom remote-session display adapter, not a real GPU, so at least one
	// extra index beyond "the obvious one" always needs trying; this leaves headroom for multi-GPU
	// machines too, not just the single-real-GPU-plus-one-virtual-adapter case actually observed.
	const int MaxDirectMlDeviceIdToTry = 4;

	/// <summary>
	/// Finds a DXGI adapter index DirectML actually initializes successfully against, by running
	/// the exact same <c>OnnxEmbedder(modelPath, preferDirectML: true, deviceId)</c> construction
	/// <em>in a separate child process per candidate index</em> (0 through <see cref="MaxDirectMlDeviceIdToTry"/>),
	/// stopping at the first success. Two things live-verified (2026-07-30) that make both the
	/// process isolation and the multi-index search necessary, not defensive overkill:
	/// <list type="bullet">
	/// <item>A failed DirectML device init crashed with a native access violation (0xC0000005)
	/// that bypassed every managed <c>try</c>/<c>catch</c> up the stack, including
	/// <c>OnnxEmbedder</c>'s own — uncatchable from managed code, only isolable by process
	/// boundary, the same reason <see cref="Removal.GpuEncoderProbe"/> probes ffmpeg encoders
	/// out-of-process too.</item>
	/// <item><b>Device index 0 is not a safe default.</b> On a machine reached over a remote
	/// session, DXGI enumerated a virtual "remote display adapter" at index 0 — one that falsely
	/// advertises full D3D12 feature-level support (no real compute hardware backs the claim) —
	/// ahead of the real GPU at index 1. Trying only index 0 would have permanently marked
	/// DirectML unavailable on a machine where it actually works fine, just not at the default
	/// index.</item>
	/// </list>
	/// Each candidate is its own child process (a crash terminates that one attempt cleanly;
	/// nothing carries over to the next candidate) — invoked via <see cref="DirectMlProbeArgument"/>
	/// as a hidden first argument, handled at the very top of <c>Program.Main</c> before normal CLI
	/// parsing, so a crash never touches System.CommandLine or any real command's state.
	/// </summary>
	/// <returns><c>Success</c>/<c>DeviceId</c> describe the first working index found (sets
	/// <see cref="DirectMlDeviceId"/> as a side effect on success). <c>Detail</c> is the *last*
	/// candidate's diagnostic text when none worked — the child's own caught-exception message
	/// (<see cref="RunDirectMlProbe"/> writes it to stderr before returning nonzero) when it
	/// exited cleanly with an error, or a note that it crashed/timed out when there's no such text
	/// to read (a real native crash exits via the OS, not this process's own error-reporting path).</returns>
	public static (bool Success, int DeviceId, string? Detail) ProbeDirectMlInSubprocess(string modelPath, CancellationToken ct = default) {
		using var totalScope = ScanTelemetry.Time("DirectML adapter probe (total, all candidates)");

		IReadOnlyList<DirectMlAdapterEnumerator.AdapterInfo> realAdapters;
		using (ScanTelemetry.Time("DXGI adapter enumeration"))
			realAdapters = DirectMlAdapterEnumerator.GetRealGpuAdapters();
		List<int> candidates = new();
		if (realAdapters.Count > 0) {
			foreach (DirectMlAdapterEnumerator.AdapterInfo adapter in realAdapters)
				candidates.Add(adapter.Index);
		}
		else {
			for (int deviceId = 0; deviceId <= MaxDirectMlDeviceIdToTry; deviceId++)
				candidates.Add(deviceId);
		}
		totalScope.Detail = $"{candidates.Count} candidate(s)";

		string? lastDetail = null;
		foreach (int deviceId in candidates) {
			bool success; string? detail;
			using (var scope = ScanTelemetry.Time($"DirectML probe subprocess (device {deviceId})")) {
				(success, detail) = ProbeOneDevice(modelPath, deviceId, ct);
				scope.Detail = success ? "attached" : "failed";
			}
			if (success) {
				DirectMlDeviceId = deviceId;
				return (true, deviceId, null);
			}
			lastDetail = $"device {deviceId}: {detail}";
		}
		return (false, 0, lastDetail);
	}

	static (bool Success, string? Detail) ProbeOneDevice(string modelPath, int deviceId, CancellationToken ct) {
		try {
			string? processPath = Environment.ProcessPath;
			if (string.IsNullOrEmpty(processPath)) return (false, "could not determine this process's own executable path");

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
				if (string.IsNullOrEmpty(assemblyLocation)) return (false, "could not determine the entry assembly's location");
				psi.ArgumentList.Add(assemblyLocation);
			}
			psi.ArgumentList.Add(DirectMlProbeArgument);
			psi.ArgumentList.Add(modelPath);
			psi.ArgumentList.Add(deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
				return (false, "the probe did not exit within 30s and was killed");
			}
			Task.WaitAll(stdoutTask, stderrTask);
			if (process.ExitCode == 0) return (true, null);
			string stderr = stderrTask.Result.Trim();
			// A clean nonzero exit from RunDirectMlProbe's own catch block always has a message on
			// stderr; a true native crash (access violation, etc.) terminates via the OS instead,
			// so there is nothing here to read -- report the raw exit code so it's still visible.
			return (false, stderr.Length > 0 ? stderr : $"the probe process exited with code {process.ExitCode} and no error output (likely a native crash)");
		}
		catch (Exception ex) {
			return (false, $"could not run the probe process: {ex.Message}");
		}
	}

	/// <summary>The child-process entry point <see cref="ProbeDirectMlInSubprocess"/> invokes (once
	/// per candidate device index) — called from <c>VBR.CLI.Program</c>'s own hidden-argument
	/// check, before normal CLI parsing, so a crash here never touches System.CommandLine or any
	/// real command's state. Constructs exactly what a real run would construct against
	/// <paramref name="deviceId"/>; a clean 0 means that index is safe to trust after the parent
	/// reads this exit code. On failure, writes the caught exception's type and message to stderr
	/// before returning nonzero — the parent's <see cref="ProbeDirectMlInSubprocess"/> surfaces
	/// this text so a failure is diagnosable instead of a bare "didn't work."
	///
	/// <b>Returns nonzero (1) when DirectML silently falls back to CPU, not just when construction
	/// throws.</b> Live-verified (2026-08-02) this distinction was missing: <see cref="VDF.Core.AI.OnnxEmbedder"/>'s
	/// constructor catches a DirectML attach failure internally and falls back to a working CPU
	/// session with NO exception at all — so a probe that only checked "did construction throw"
	/// reported every non-crashing device index as "success" even when DirectML itself failed and
	/// silently fell back underneath, on every machine this was tested on, the whole time. Checking
	/// <see cref="VDF.Core.AI.OnnxEmbedder.UsedDirectML"/> is what actually distinguishes "DirectML
	/// attached" from "gracefully did not."</summary>
	public static int RunDirectMlProbe(string modelPath, int deviceId) {
		try {
			VDF.Core.AI.AiComponents.EnsureResolverInstalled(preferDirectML: true);
			using var embedder = new VDF.Core.AI.OnnxEmbedder(modelPath, preferDirectML: true, deviceId);
			if (!embedder.UsedDirectML) {
				Console.Error.WriteLine("DirectML did not attach (fell back to CPU inside OnnxEmbedder) -- see log.txt for the caught exception's own message.");
				return 1;
			}
			return 0;
		}
		catch (Exception ex) {
			Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
			return 1;
		}
	}
}
