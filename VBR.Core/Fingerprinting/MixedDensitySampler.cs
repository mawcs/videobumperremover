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
using System.Linq;
using System.Threading;
using VBR.Core.Diagnostics;
using VBR.Core.Extraction;
using VDF.Core.AI;
using VDF.Core.Utils;

namespace VBR.Core.Fingerprinting;

/// <summary>
/// Samples a region that may be longer than the ultra-dense edge zone at two densities in one
/// pass: dense from the true edge out to <see cref="EdgeDensityProfile.EdgeBoundary"/>, sparse for
/// the rest — producing one <see cref="TimedFrame"/> timeline with real per-frame timestamps
/// instead of the single-interval assumption <c>VisualBumperMatcher.Embed</c> makes today. See
/// docs/iterativeplan.md, "Mixed-density edge/middle fingerprinting," for why this exists
/// separately rather than extending the single-interval path in place.
///
/// Frame gathering (seek+decode → low-information filtering → timestamp assignment) is factored
/// into <see cref="GatherFrames"/>, deliberately separate from embedding: it produces plain
/// timestamped RGB24 frames, signal-agnostic. <see cref="Sample"/> turns those into DINOv2
/// embeddings only; <see cref="SampleWithPHash"/> turns the same gathered frames into both DINOv2
/// embeddings *and* <see cref="TimedPHash"/> pHashes (via <see cref="FrameHashing"/>) in one pass,
/// so adding the second signal costs no extra decode.
///
/// Each dense/sparse zone is decoded directly from the source in one ffmpeg process via
/// <see cref="DenseFrameSampler"/>'s region-aware overload — **not** via
/// <see cref="VBR.Core.Extraction.ClipExtractor.ExtractToTemp"/>. An earlier version extracted the
/// whole edge region to a temp file first, then extracted each zone as a *second* stream-copy hop
/// out of that temp file — two chained ffmpeg processes per zone, each capable of independently
/// mis-seeking. On real media (2026-07-23) that chain produced two distinct failure modes: (1) a
/// `-sseof` seek landing on a run of non-monotonic DTS in the source silently produced a
/// duration-inflated, duplicate-padded first-stage extract; (2) stream-copying *again* out of a
/// file whose first extraction needed a re-encode fallback could itself produce an outright-corrupt
/// Matroska remux (ffprobe couldn't even read a duration back). One direct seek+decode per zone —
/// the same shape as a plain <c>ffmpeg -sseof -N -i source -vf fps=...</c> command run by hand —
/// removes the extra hop these both depended on.
/// </summary>
public sealed class MixedDensitySampler : IDisposable {
	// Same cap VisualBumperMatcher applies per extracted region; a single dense or sparse zone is
	// well under this for any bumper length this project targets.
	const int MaxFramesPerZone = 400;

	readonly bool verboseLogging;
	OnnxEmbedder? embedder;

	/// <param name="verboseLogging">Logs the resolved ONNX model path on first use (only if a
	/// DINOv2-embedding method is actually called — <see cref="SamplePHash"/> never triggers it)
	/// and, per file, sampled/usable frame counts and each inference batch call, via
	/// <see cref="Logger"/> — same convention as <c>VisualBumperMatcher</c>'s <c>--verbose</c>
	/// support.</param>
	public MixedDensitySampler(bool verboseLogging = false) => this.verboseLogging = verboseLogging;

	/// <summary>One quality-filtered sampled frame, tagged with its real position (seconds from
	/// the start of the requested region) and not yet turned into any per-signal value.</summary>
	internal readonly record struct SampledFrame(double TimestampSeconds, byte[] Rgb24);

	/// <summary>
	/// Gathers timestamped, low-information-filtered RGB24 frames across <paramref name="totalLength"/>
	/// of <paramref name="sourcePath"/>'s <paramref name="region"/> edge: densely sampled for
	/// <see cref="EdgeDensityProfile.EdgeBoundary"/> nearest the true edge, sparsely sampled the
	/// rest of the way. Signal-agnostic — no embedding happens here (see the class doc comment).
	/// </summary>
	/// <param name="dumpFramesDir">Diagnostic: when set, every sampled frame is written as a PNG
	/// under <c>{dumpFramesDir}/{dumpLabel}-dense</c> / <c>-sparse</c> via <see cref="FrameDump"/>,
	/// same convention as <c>VisualBumperMatcher</c>'s dump — written pre-filter (before
	/// <see cref="FrameQuality.SelectUsable"/> runs), so the dump shows the unfiltered truth, not
	/// just what survived. Null (the default) dumps nothing.</param>
	internal static List<SampledFrame> GatherFrames(
			string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
			string? dumpFramesDir = null, string? dumpLabel = null, bool verboseLogging = false, CancellationToken ct = default) {
		if (totalLength <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(totalLength), "Total length must be positive.");
		if (profile.DenseInterval <= TimeSpan.Zero || profile.SparseInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(profile), "Dense and sparse intervals must be positive.");
		TimeSpan edgeBoundary = profile.EdgeBoundary < TimeSpan.Zero ? TimeSpan.Zero : profile.EdgeBoundary;
		if (edgeBoundary > totalLength)
			edgeBoundary = totalLength;
		TimeSpan sparseLength = totalLength - edgeBoundary;

		var frames = new List<SampledFrame>();
		// Zone start labels are relative to the requested window (0 = the near end of totalLength
		// from the `region` edge), not absolute file time -- purely for consistent, readable
		// timestamps/diagnostics. Nothing downstream needs true-file-absolute times: presence
		// matching (ComparePresence) never requires temporal alignment between clip and candidate.
		double denseZoneStart = region == ClipEdge.begin ? 0 : sparseLength.TotalSeconds;
		double sparseZoneStart = region == ClipEdge.begin ? edgeBoundary.TotalSeconds : 0;

		// Each zone is a region directly against the true edge of `sourcePath` -- computed without
		// ever materializing a "whole edge region" intermediate file (see the class doc comment).
		if (edgeBoundary > TimeSpan.Zero) {
			ClipRegion denseRegion = region == ClipEdge.begin
				? ClipRegion.At(TimeSpan.Zero, edgeBoundary)      // first edgeBoundary of the file
				: ClipRegion.Tail(edgeBoundary);                  // last edgeBoundary of the file
			AppendZone(sourcePath, denseRegion, profile.DenseInterval, denseZoneStart, frames,
				DumpDir(dumpFramesDir, dumpLabel, "dense"), "dense", verboseLogging, ct);
		}
		if (sparseLength > TimeSpan.Zero) {
			ClipRegion sparseRegion = region == ClipEdge.begin
				? ClipRegion.At(edgeBoundary, sparseLength)       // the sparseLength right after the dense zone
				: ClipRegion.BeforeEnd(totalLength, sparseLength); // the sparseLength right before the dense zone
			AppendZone(sourcePath, sparseRegion, profile.SparseInterval, sparseZoneStart, frames,
				DumpDir(dumpFramesDir, dumpLabel, "sparse"), "sparse", verboseLogging, ct);
		}

		frames.Sort((a, b) => a.TimestampSeconds.CompareTo(b.TimestampSeconds));
		if (verboseLogging)
			Logger.Instance.Info($"[mixed-density] '{Path.GetFileName(sourcePath)}': {frames.Count} usable frame(s) total across both zones.");
		return frames;
	}

	static string? DumpDir(string? dumpFramesDir, string? dumpLabel, string zone) =>
		dumpFramesDir is null ? null : Path.Combine(dumpFramesDir, $"{dumpLabel}-{zone}");

	// Only called by Sample/SampleWithPHash -- SamplePHash never touches AiComponents/ONNX at all,
	// the whole point of offering pHash as a lightweight alternate mode.
	void EnsureEmbedder() {
		bool preferDirectML = HardwareAcceleration.PreferDirectML;
		AiComponents.EnsureReady(preferDirectML);
		if (embedder is null) {
			if (verboseLogging)
				Logger.Instance.Info($"[mixed-density] Loading ONNX model: {AiComponents.ModelPath}");
			embedder = new OnnxEmbedder(AiComponents.ModelPath, preferDirectML);
			if (verboseLogging)
				Logger.Instance.Info("[mixed-density] ONNX inference session ready.");
		}
	}

	static void AppendZone(string sourcePath, ClipRegion zone, TimeSpan interval, double zoneStartSeconds,
			List<SampledFrame> frames, string? dumpDir, string zoneName, bool verboseLogging, CancellationToken ct) {
		byte[][] rgbFrames = DenseFrameSampler.SampleFrames(sourcePath, zone, interval.TotalSeconds, MaxFramesPerZone, ct);
		if (dumpDir is not null) FrameDump.WritePngs(rgbFrames, dumpDir);
		bool[] usable = FrameQuality.SelectUsable(rgbFrames);
		int usableCount = 0;
		for (int i = 0; i < rgbFrames.Length; i++) {
			if (!usable[i]) continue;
			usableCount++;
			frames.Add(new SampledFrame(zoneStartSeconds + i * interval.TotalSeconds, rgbFrames[i]));
		}
		if (verboseLogging)
			Logger.Instance.Info($"[mixed-density] '{Path.GetFileName(sourcePath)}' {zoneName} zone: {rgbFrames.Length} frame(s) " +
				$"sampled @ {interval.TotalSeconds:0.###}s, {usableCount} usable after low-information filtering ({rgbFrames.Length - usableCount} dropped).");
	}

	/// <summary>
	/// Gathers frames (<see cref="GatherFrames"/>) and embeds every surviving one via DINOv2,
	/// batched the same way <c>VisualBumperMatcher.Embed</c> batches today. Construct one instance
	/// and reuse it across a run — the ONNX session is owned for this instance's lifetime.
	/// </summary>
	/// <param name="dumpFramesDir">See <see cref="GatherFrames"/>'s doc comment.</param>
	/// <param name="dumpLabel">Identifies this call's frames within <paramref name="dumpFramesDir"/>
	/// (e.g. <c>"clip"</c> or <c>"003-SomeEpisode"</c>); required when <paramref name="dumpFramesDir"/>
	/// is set.</param>
	public IReadOnlyList<TimedFrame> Sample(
			string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
			string? dumpFramesDir = null, string? dumpLabel = null, CancellationToken ct = default) {
		EnsureEmbedder();

		List<SampledFrame> sampled = GatherFrames(sourcePath, region, totalLength, profile, dumpFramesDir, dumpLabel, verboseLogging, ct);
		var result = new List<TimedFrame>(sampled.Count);
		var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
		var batchTimestamps = new List<double>(OnnxEmbedder.MaxBatch);
		int batchCount = 0;
		void Flush() {
			if (batch.Count == 0) return;
			byte[][] vectors = embedder!.EmbedBatchQuantized(batch);
			if (verboseLogging)
				Logger.Instance.Info($"[mixed-density] ONNX inference: embedded batch #{++batchCount} ({vectors.Length} frames, {vectors[0].Length}-byte quantized vectors).");
			for (int k = 0; k < vectors.Length; k++)
				result.Add(new TimedFrame(batchTimestamps[k], vectors[k]));
			batch.Clear();
			batchTimestamps.Clear();
		}
		foreach (SampledFrame frame in sampled) {
			batch.Add(frame.Rgb24);
			batchTimestamps.Add(frame.TimestampSeconds);
			if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
		}
		Flush();
		return result;
	}

	/// <summary>Both per-position signals from one <see cref="Sample"/>-style call: DINOv2
	/// embeddings and pHashes, gathered from a single <see cref="GatherFrames"/> decode pass so
	/// comparing the two signals never costs a second extract/decode of the source video.</summary>
	public readonly record struct SampleResult(IReadOnlyList<TimedFrame> Embeddings, IReadOnlyList<TimedPHash> PHashes);

	/// <summary>
	/// Like <see cref="Sample"/>, but also computes each surviving frame's pHash (<see cref="FrameHashing.ComputePHash"/>)
	/// from the very same decoded RGB24 bytes handed to the embedder — exploratory: lets callers
	/// compare how pHash performs against DINOv2 on identical positions/candidates (see
	/// docs/decisions/0006-edge-focused-fingerprinting.md's 2026-07-21 amendment). Does not change
	/// or replace <see cref="Sample"/>.
	/// </summary>
	/// <param name="dumpFramesDir">See <see cref="GatherFrames"/>'s doc comment.</param>
	/// <param name="dumpLabel">See <see cref="Sample"/>'s doc comment.</param>
	public SampleResult SampleWithPHash(
			string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
			string? dumpFramesDir = null, string? dumpLabel = null, CancellationToken ct = default) {
		EnsureEmbedder();

		List<SampledFrame> sampled = GatherFrames(sourcePath, region, totalLength, profile, dumpFramesDir, dumpLabel, verboseLogging, ct);
		var embeddings = new List<TimedFrame>(sampled.Count);
		var hashes = new List<TimedPHash>(sampled.Count);
		var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
		var batchTimestamps = new List<double>(OnnxEmbedder.MaxBatch);
		int batchCount = 0;
		void Flush() {
			if (batch.Count == 0) return;
			byte[][] vectors = embedder!.EmbedBatchQuantized(batch);
			if (verboseLogging)
				Logger.Instance.Info($"[mixed-density] ONNX inference: embedded batch #{++batchCount} ({vectors.Length} frames, {vectors[0].Length}-byte quantized vectors).");
			for (int k = 0; k < vectors.Length; k++)
				embeddings.Add(new TimedFrame(batchTimestamps[k], vectors[k]));
			batch.Clear();
			batchTimestamps.Clear();
		}
		foreach (SampledFrame frame in sampled) {
			hashes.Add(new TimedPHash(frame.TimestampSeconds, FrameHashing.ComputePHash(frame.Rgb24)));
			batch.Add(frame.Rgb24);
			batchTimestamps.Add(frame.TimestampSeconds);
			if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
		}
		Flush();
		return new SampleResult(embeddings, hashes);
	}

	/// <summary>
	/// pHash only — never touches <see cref="AiComponents"/>/ONNX at all (no model download, no
	/// inference session). This is what makes pHash a genuinely lightweight alternate mode rather
	/// than "DINOv2 plus a bit more": a caller that only wants pHash pays no ONNX cost whatsoever.
	/// </summary>
	/// <param name="dumpFramesDir">See <see cref="GatherFrames"/>'s doc comment.</param>
	/// <param name="dumpLabel">See <see cref="Sample"/>'s doc comment.</param>
	public IReadOnlyList<TimedPHash> SamplePHash(
			string sourcePath, ClipEdge region, TimeSpan totalLength, EdgeDensityProfile profile,
			string? dumpFramesDir = null, string? dumpLabel = null, CancellationToken ct = default) {
		List<SampledFrame> sampled = GatherFrames(sourcePath, region, totalLength, profile, dumpFramesDir, dumpLabel, verboseLogging, ct);
		return sampled.Select(f => new TimedPHash(f.TimestampSeconds, FrameHashing.ComputePHash(f.Rgb24))).ToList();
	}

	public void Dispose() => embedder?.Dispose();
}
