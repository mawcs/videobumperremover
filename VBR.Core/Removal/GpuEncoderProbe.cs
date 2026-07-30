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
using System.Threading;
using System.Threading.Tasks;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Removal;

/// <summary>
/// Detects whether a GPU video encoder actually works on this machine, for
/// <see cref="ClipRemover"/>'s re-encode path (docs/decisions/0013-gpu-acceleration.md).
/// H.264/HEVC only, per that decision — VP9/AV1 GPU encode stay out of scope (VP9 hardware encode
/// support is rare/recent; AV1 stays deferred entirely, matching ADR 0012).
///
/// <b>Detection is by attempting a real encode, not a static <c>ffmpeg -encoders</c> list
/// check.</b> A build can list e.g. <c>h264_nvenc</c> as compiled-in with no NVIDIA driver
/// present at all — the encoder would only fail at actual invocation time, which is exactly the
/// failure mode this class exists to catch before trusting a real removal to it. The probe itself
/// is cheap: a single 256×256, 1-frame synthetic source through <c>-f lavfi</c>, discarded to
/// <c>-f null</c> — no real file ever touches the candidate encoder during detection. 256×256 (not
/// a smaller size) is deliberate: live-verified on real NVENC hardware that a 64×64 probe frame
/// fails encoder init with "Frame Dimension less than the minimum supported value" even though the
/// same encoder works fine on real (much larger) sources — a probe frame must clear every
/// candidate encoder's minimum-dimension floor, not just be small/cheap.
/// </summary>
public static class GpuEncoderProbe {
	// Priority order per family: NVENC first (the most common discrete-GPU case and the
	// maintainer's own hardware), then QSV (common on Intel-CPU laptops/desktops with integrated
	// graphics), then AMF (AMD). Only one vendor's encoders will actually work on any given
	// machine — trying in this order just means NVIDIA hardware doesn't waste time probing QSV/AMF
	// first.
	static readonly string[] H264Candidates = { "h264_nvenc", "h264_qsv", "h264_amf" };
	static readonly string[] HevcCandidates = { "hevc_nvenc", "hevc_qsv", "hevc_amf" };

	// Cached once per process run, not persisted across invocations -- hardware/driver state could
	// change between separate `vbr` runs (a driver update, a different machine), and re-probing is
	// cheap enough (a handful of sub-second synthetic 1-frame encodes, worst case) that trusting a
	// stale on-disk cache isn't worth the risk of a wrong answer.
	static readonly Dictionary<string, string?> cache = new();
	static readonly object cacheLock = new();

	/// <param name="codecFamily">"h264" or "hevc" — anything else returns null immediately (no
	/// candidates exist for it, per this class' documented H.264/HEVC-only scope).</param>
	/// <returns>The first working GPU encoder name (e.g. "h264_nvenc"), or null if none of this
	/// family's candidates produced a successful probe encode.</returns>
	public static string? TryGetEncoder(string codecFamily, bool verbose = false, CancellationToken ct = default) {
		string[] candidates = codecFamily switch {
			"h264" => H264Candidates,
			"hevc" => HevcCandidates,
			_ => System.Array.Empty<string>(),
		};
		if (candidates.Length == 0) return null;

		lock (cacheLock) {
			if (cache.TryGetValue(codecFamily, out string? cached))
				return cached;

			foreach (string candidate in candidates) {
				(bool success, string? detail) = ProbeEncoder(candidate, ct);
				if (success) {
					if (verbose) Logger.Instance.Info($"[gpu-encode] '{candidate}' probed successfully -- using it for {codecFamily}.");
					cache[codecFamily] = candidate;
					return candidate;
				}
				if (verbose) Logger.Instance.Info($"[gpu-encode] '{candidate}' probe failed -- trying the next candidate, if any. ({detail})");
			}
			cache[codecFamily] = null;
			return null;
		}
	}

	static (bool Success, string? Detail) ProbeEncoder(string encoderName, CancellationToken ct) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
		// 256x256, not a smaller/cheaper size -- see the class doc comment: NVENC rejects a 64x64
		// probe frame with "Frame Dimension less than the minimum supported value" even when the
		// encoder itself works fine, which was live-verified to make this probe report false
		// negatives for a genuinely working GPU encoder.
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("color=c=black:s=256x256:r=1");
		psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
		psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoderName);
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
		psi.ArgumentList.Add("-");

		using var process = new Process { StartInfo = psi };
		try {
			process.Start();
			// Drained concurrently, not sequentially, same rationale as ClipRemover.RunFfmpeg --
			// a 1-frame probe's own output is tiny, but there's no reason to reintroduce the
			// deadlock class this codebase has already hit and fixed twice elsewhere.
			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
			Task<string> stderrTask = process.StandardError.ReadToEndAsync();
			// A synthetic 1-frame probe either succeeds in well under a second or the encoder is
			// broken/absent -- 15s is generous slack for a cold driver/context init, not an
			// expectation of how long this normally takes.
			if (!process.WaitForExit(15_000)) {
				try { process.Kill(entireProcessTree: true); } catch { }
				return (false, "timed out");
			}
			Task.WaitAll(stdoutTask, stderrTask);
			if (process.ExitCode == 0) return (true, null);
			string stderrText = stderrTask.Result.Trim();
			return (false, stderrText.Length > 0 ? stderrText : $"exit code {process.ExitCode}");
		}
		catch (Exception ex) {
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
			return (false, ex.Message);
		}
	}
}
