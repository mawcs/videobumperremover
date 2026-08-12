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
using System.IO;
using System.Threading;
using VBR.Core.Diagnostics;
using VBR.Core.Extraction;
using VDF.Core;
using VDF.Core.AI;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Fingerprinting;

/// <summary>
/// The library scan's whole-file sampling entry point (docs/iterativeplan.md, "Library scan —
/// cached fingerprint index"). Unlike <see cref="MixedDensitySampler"/> (one known edge, a known
/// bumper length), a scan doesn't know where a bumper is, so it samples the whole file: dense near
/// *both* true edges (<see cref="DenseFrameSampler"/>'s full-decode region overload — same as the
/// match/remove path, no <see cref="VBR.Core.Extraction.ClipExtractor"/> involved) plus a sparse,
/// keyframe-only pass across the *entire* file (<see cref="DenseFrameSampler.SampleKeyframes"/>).
/// All three passes' surviving frames merge into one combined, timestamp-sorted
/// <see cref="TimedFingerprint"/> list — deliberately not three separately-bounded regions: the
/// sparse pass always covers the whole file unconditionally, so there's no "compute the interior
/// region, clamp for files shorter than 2×edge-boundary" step to get wrong. Presence matching never
/// requires uniform density or alignment between the two sides it compares, so a merged collection
/// with dense points clustered near a few sparse ones is exactly as valid as three separate zones —
/// the only cost is a little redundant (but keyframe-cheap) sparse coverage of the edges themselves.
///
/// This class always knows the file's real probed duration (<see cref="FFProbeEngine.GetMediaInfo"/>),
/// so — unlike <see cref="MixedDensitySampler"/>, which only ever sees one caller-supplied window —
/// every <see cref="TimedFingerprint.TimestampSeconds"/> here is a true absolute position from the
/// start of the file, not relative to whichever window was requested.
/// </summary>
public sealed class WholeFileSampler : IDisposable {
	// Same per-zone safety ceiling VisualBumperMatcher/MixedDensitySampler use elsewhere; a single
	// 20s dense edge zone is well under this for any interval this project targets. Config-aware
	// since 2026-08-12 (VbrConfig.Current.Sampling.MaxFramesPerZone) -- was three independently
	// hardcoded copies of the same value before this, now one shared config key.
	static int MaxDenseFramesPerZone => Configuration.VbrConfig.Current.Sampling.MaxFramesPerZone;

	// Headroom above the exact frame count the probed duration implies, for seek/rounding slop --
	// the whole point of sizing this adaptively (rather than reusing MaxDenseFramesPerZone) is to
	// never silently truncate a long file's middle, so a little slack costs nothing. Config-aware
	// since 2026-08-12 (VbrConfig.Current.Sampling.SparseFrameCapMargin).
	static int SparseFrameCapMargin => Configuration.VbrConfig.Current.Sampling.SparseFrameCapMargin;

	readonly bool verboseLogging;
	OnnxEmbedder? embedder;

	/// <param name="verboseLogging">Logs the resolved ONNX model path on first use and, per file,
	/// per-zone sampled/usable frame counts and each inference batch call, via <see cref="Logger"/>
	/// — same convention as <see cref="MixedDensitySampler"/>/<c>VisualBumperMatcher</c>'s
	/// <c>--verbose</c> support.</param>
	public WholeFileSampler(bool verboseLogging = false) => this.verboseLogging = verboseLogging;

	public readonly record struct Result(IReadOnlyList<TimedFingerprint> Fingerprints, TimeSpan Duration);

	/// <exception cref="InvalidOperationException">ffprobe couldn't determine the file's duration,
	/// or ffmpeg failed sampling one of the three passes.</exception>
	public Result Sample(string sourcePath, EdgeDensityProfile profile, CancellationToken ct = default) {
		using var totalScope = ScanTelemetry.Time($"sample '{Path.GetFileName(sourcePath)}' (total)");
		EnsureEmbedder();

		MediaInfo? info;
		using (ScanTelemetry.Time("ffprobe (duration)"))
			info = FFProbeEngine.GetMediaInfo(sourcePath, extendedLogging: verboseLogging);
		if (info is null || info.Duration <= TimeSpan.Zero)
			throw new InvalidOperationException(
				$"Could not determine duration for '{Path.GetFileName(sourcePath)}' (ffprobe failed or reported no duration).");
		TimeSpan duration = info.Duration;

		TimeSpan edgeBoundary = profile.EdgeBoundary < TimeSpan.Zero ? TimeSpan.Zero : profile.EdgeBoundary;
		if (edgeBoundary > duration)
			edgeBoundary = duration;

		var raw = new List<(double TimestampSeconds, byte[] Rgb24)>();

		int sparseCap = (int)Math.Ceiling(duration.TotalSeconds / profile.SparseInterval.TotalSeconds) + SparseFrameCapMargin;
		byte[][] sparseFrames;
		using (ScanTelemetry.Time("sparse pass (whole-file keyframes)"))
			sparseFrames = DenseFrameSampler.SampleKeyframes(sourcePath, profile.SparseInterval.TotalSeconds, sparseCap, ct);
		AppendUsable(sparseFrames, profile.SparseInterval.TotalSeconds, zoneStartSeconds: 0, raw, "whole-file sparse", sourcePath);

		if (edgeBoundary > TimeSpan.Zero) {
			byte[][] beginFrames;
			using (ScanTelemetry.Time("begin-edge dense pass"))
				beginFrames = DenseFrameSampler.SampleFrames(
					sourcePath, ClipRegion.At(TimeSpan.Zero, edgeBoundary), profile.DenseInterval.TotalSeconds, MaxDenseFramesPerZone, ct);
			AppendUsable(beginFrames, profile.DenseInterval.TotalSeconds, zoneStartSeconds: 0, raw, "begin edge", sourcePath);

			byte[][] endFrames;
			using (ScanTelemetry.Time("end-edge dense pass"))
				endFrames = DenseFrameSampler.SampleFrames(
					sourcePath, ClipRegion.Tail(edgeBoundary), profile.DenseInterval.TotalSeconds, MaxDenseFramesPerZone, ct);
			double endZoneStart = (duration - edgeBoundary).TotalSeconds;
			AppendUsable(endFrames, profile.DenseInterval.TotalSeconds, zoneStartSeconds: endZoneStart, raw, "end edge", sourcePath);
		}

		raw.Sort((a, b) => a.TimestampSeconds.CompareTo(b.TimestampSeconds));

		using var inferenceScope = ScanTelemetry.Time("ONNX inference (all batches)");
		var result = new List<TimedFingerprint>(raw.Count);
		var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
		var batchTimestamps = new List<double>(OnnxEmbedder.MaxBatch);
		var batchHashes = new List<ulong>(OnnxEmbedder.MaxBatch);
		int batchCount = 0;
		void Flush() {
			if (batch.Count == 0) return;
			byte[][] vectors = embedder!.EmbedBatchQuantized(batch);
			if (ScanTelemetry.Enabled)
				ScanTelemetry.Note($"ONNX inference: embedded batch #{++batchCount} ({vectors.Length} frames, {vectors[0].Length}-byte quantized vectors).");
			for (int k = 0; k < vectors.Length; k++)
				result.Add(new TimedFingerprint(batchTimestamps[k], vectors[k], batchHashes[k]));
			batch.Clear();
			batchTimestamps.Clear();
			batchHashes.Clear();
		}
		foreach ((double timestamp, byte[] rgb24) in raw) {
			batchHashes.Add(FrameHashing.ComputePHash(rgb24));
			batch.Add(rgb24);
			batchTimestamps.Add(timestamp);
			if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
		}
		Flush();

		return new Result(result, duration);
	}

	void AppendUsable(byte[][] frames, double interval, double zoneStartSeconds,
			List<(double TimestampSeconds, byte[] Rgb24)> raw, string zoneName, string sourcePath) {
		bool[] usable = FrameQuality.SelectUsable(frames);
		int usableCount = 0;
		for (int i = 0; i < frames.Length; i++) {
			if (!usable[i]) continue;
			usableCount++;
			raw.Add((zoneStartSeconds + i * interval, frames[i]));
		}
		if (ScanTelemetry.DebugEnabled)
			ScanTelemetry.NoteDebug($"'{Path.GetFileName(sourcePath)}' {zoneName}: {frames.Length} frame(s) sampled @ {interval:0.###}s, " +
				$"{usableCount} usable after low-information filtering ({frames.Length - usableCount} dropped).");
	}

	void EnsureEmbedder() {
		bool preferDirectML = HardwareAcceleration.PreferDirectML;
		using (ScanTelemetry.Time("AiComponents.EnsureReady"))
			AiComponents.EnsureReady(preferDirectML);
		if (embedder is null) {
			if (verboseLogging)
				Logger.Instance.Info($"[scan] Loading ONNX model: {AiComponents.ModelPath}");
			using (ScanTelemetry.Time("OnnxEmbedder construction (model load)"))
				embedder = new OnnxEmbedder(AiComponents.ModelPath, preferDirectML, HardwareAcceleration.DirectMlDeviceId);
			if (verboseLogging)
				Logger.Instance.Info("[scan] ONNX inference session ready.");
		}
	}

	public void Dispose() => embedder?.Dispose();
}
