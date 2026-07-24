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
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VBR.Core.Extraction;
using VDF.Core.AI;
using VDF.Core.FFTools;

namespace VBR.Core.Fingerprinting;

/// <summary>
/// Dense frame sampling for the visual matcher: decodes **all** frames of a short window and
/// emits one 224×224 RGB24 frame per <c>intervalSeconds</c>. This deliberately differs from
/// VDF's <c>FfmpegEngine.GetDenseAiFrames</c>, which decodes keyframes only
/// (<c>-skip_frame nokey</c>) — a sane optimization for whole-file dedup scans that is wrong for
/// this matcher: it caps distinct content at the keyframe cadence (a 5s Netflix ident collapsed
/// to 3 distinct images, losing the letter animation entirely) and lets the fps filter
/// manufacture duplicate "evidence" (see docs/iterativeplan.md §A2 and the 2026-07-18 correction
/// in docs/design/matcher-spec.md). Full decode is affordable precisely because callers only
/// ever hand this a short window — never a full-length episode (matcher-spec: sampling is
/// edge-focused, structurally).
///
/// Two entry points: <see cref="SampleFrames(string, double, int, CancellationToken)"/> decodes a
/// file whole (for a caller that already extracted a small clip, e.g. <see cref="VBR.Core.Matching.VisualBumperMatcher"/>);
/// <see cref="SampleFrames(string, ClipRegion, double, int, CancellationToken)"/> seeks directly
/// into a full-length source and decodes just the requested region, in one ffmpeg process — no
/// prior <see cref="VBR.Core.Extraction.ClipExtractor"/> call. <see cref="MixedDensitySampler"/>
/// uses the latter: chaining two separate stream-copy extractions (a whole edge region, then a
/// sub-zone carved out of *that* temp file) turned out to be a real corruption vector on real
/// media (2026-07-23 finding — a second stream-copy hop produced an outright-corrupt Matroska
/// remux on one real file), not just avoidable overhead. One direct seek+decode, matching what a
/// plain <c>ffmpeg -sseof -N -i source -vf fps=...</c> command does by hand, has one fewer hop to
/// go wrong.
/// </summary>
public static class DenseFrameSampler {
	/// <summary>
	/// Samples <paramref name="path"/> at one frame per <paramref name="intervalSeconds"/>
	/// (full decode; frame k represents ≈ k·interval seconds). Returns every sampled frame —
	/// low-information filtering is <see cref="FrameQuality"/>'s job, so diagnostics (frame
	/// dumps) can see the unfiltered truth.
	/// </summary>
	/// <exception cref="InvalidOperationException">ffmpeg failed or timed out.</exception>
	public static byte[][] SampleFrames(string path, double intervalSeconds, int maxFrames, CancellationToken ct = default) =>
		SampleFrames(path, region: null, intervalSeconds, maxFrames, ct);

	/// <summary>
	/// Like the three-arg overload, but seeks directly into <paramref name="sourcePath"/> using
	/// <paramref name="region"/> — the same <c>-ss</c>/<c>-sseof</c> + <c>-t</c> args
	/// <see cref="VBR.Core.Extraction.ClipExtractor"/> uses (via <see cref="ClipExtractor.AppendSeekArgs"/>)
	/// — instead of requiring a pre-extracted file. One ffmpeg process does seek+decode+sample.
	/// </summary>
	/// <exception cref="InvalidOperationException">ffmpeg failed or timed out.</exception>
	public static byte[][] SampleFrames(string sourcePath, ClipRegion region, double intervalSeconds, int maxFrames, CancellationToken ct = default) =>
		SampleFrames(sourcePath, (ClipRegion?)region, intervalSeconds, maxFrames, ct);

	static byte[][] SampleFrames(string path, ClipRegion? region, double intervalSeconds, int maxFrames, CancellationToken ct) {
		if (intervalSeconds <= 0)
			throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
		int frameBytes = OnnxEmbedder.InputSide * OnnxEmbedder.InputSide * 3;
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-nostdin");
		psi.ArgumentList.Add("-an"); psi.ArgumentList.Add("-sn"); psi.ArgumentList.Add("-dn");
		if (region is { } r) ClipExtractor.AppendSeekArgs(psi.ArgumentList, r);
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
		if (region is { } r2) {
			psi.ArgumentList.Add("-t");
			psi.ArgumentList.Add(r2.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		}
		psi.ArgumentList.Add("-vf");
		// Same filter chain the keyframe path used (verified frame-for-frame against a DaVinci
		// per-frame reference export on 2026-07-18) — minus the -skip_frame nokey input option.
		psi.ArgumentList.Add(FormattableString.Invariant(
			$"fps=1/{intervalSeconds:0.###}:round=up,scale={OnnxEmbedder.InputSide}:{OnnxEmbedder.InputSide}:flags=bicubic,format=rgb24"));
		psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add(maxFrames.ToString(CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
		psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgb24");
		psi.ArgumentList.Add("pipe:1");

		using var process = new Process { StartInfo = psi };
		using var ms = new MemoryStream();
		Task? readTask = null;
		try {
			process.Start();
			var stderr = process.StandardError.ReadToEndAsync();
			readTask = process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
			// Full decode of a ≤~40s extract takes seconds; five minutes flags a wedged decode
			// without stalling a library run forever.
			if (!readTask.Wait((int)TimeSpan.FromMinutes(5).TotalMilliseconds, ct))
				throw new TimeoutException($"ffmpeg timed out sampling frames from: {path}");
			if (!process.WaitForExit(30_000))
				throw new TimeoutException($"ffmpeg did not exit after closing its output: {path}");
			process.WaitForExit();
			if (process.ExitCode != 0)
				throw new InvalidOperationException(
					$"ffmpeg failed sampling frames (exit {process.ExitCode}): {Tail(stderr.Result)}");

			int frameCount = (int)(ms.Length / frameBytes);
			var frames = new byte[frameCount][];
			byte[] blob = ms.GetBuffer();
			for (int i = 0; i < frameCount; i++)
				frames[i] = blob.AsSpan(i * frameBytes, frameBytes).ToArray();
			return frames;
		}
		catch (Exception e) when (e is not InvalidOperationException and not OperationCanceledException) {
			KillQuietly(process, readTask);
			if (e is OperationCanceledException) throw;
			throw new InvalidOperationException($"Frame sampling failed for '{path}': {e.Message}", e);
		}
		catch (OperationCanceledException) {
			KillQuietly(process, readTask);
			throw;
		}

		static string Tail(string stderr) {
			stderr = stderr.Trim();
			return stderr.Length <= 400 ? stderr : "…" + stderr[^400..];
		}

		static void KillQuietly(Process process, Task? readTask) {
			try {
				if (!process.HasExited)
					process.Kill();
			} catch { }
			try { readTask?.Wait(2000); } catch { }
		}
	}
}
