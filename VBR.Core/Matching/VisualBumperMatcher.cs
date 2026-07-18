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
using VDF.Core;
using VDF.Core.AI;
using VDF.Core.FFTools;

namespace VBR.Core.Matching;

/// <summary>
/// Visual DINOv2 presence matching — the PRIMARY bumper-matching signal (see
/// docs/design/matcher-spec.md). It is the only path validated on the bumpers this project
/// exists to remove: short, often *silent* studio/network idents, which audio cannot touch.
/// Ported from the validated probe: VDF.IntegrationTests/Comparison/VisualTailProbe.cs.
///
/// Presence beats VDF's rigid ≥4-hit matcher for this case: short/static bumpers don't give the
/// rigid matcher four temporally-consistent frames to agree on one offset. Presence asks a
/// weaker, correct question instead — does any distinctive clip frame appear somewhere in the
/// candidate's search region at high cosine similarity? One such frame IS the detection. The
/// rigid matcher's own result is still computed and reported alongside as corroboration, not as
/// a requirement (see matcher-spec.md's anti-patterns: never require the rigid matcher to fire).
///
/// Owns an ONNX inference session for its lifetime — construct one instance and reuse it across
/// every candidate in a run; do not construct one per file.
/// </summary>
public sealed class VisualBumperMatcher : IBumperMatcher, IDisposable {
	/// <summary>Matches VisualTailProbe's coded default. Validated hard requirement (not just
	/// tuning): short clips need this much denser — down to ~0.2s (5 samples/sec) — or the
	/// presence matcher doesn't have enough frames to find the distinctive content. See
	/// docs/decisions/0006-edge-focused-fingerprinting.md and docs/research/vdf-evaluation.md.
	/// There is deliberately no floor enforced on how small this can go.</summary>
	public const double DefaultSampleIntervalSeconds = 1.0;

	/// <summary>Matches VisualTailProbe's coded default (<c>BUMPER_PRESENCE</c>).</summary>
	public const float DefaultPresenceThreshold = 0.90f;

	/// <summary>Matches VisualTailProbe's coded default (<c>BUMPER_HIT_PERCENT</c>).</summary>
	public const float DefaultRigidHitThreshold = 0.89f;

	// VisualTailProbe's own cap; generous relative to any edge window this project searches.
	const int MaxFramesPerFile = 400;

	readonly double sampleInterval;
	readonly float presenceThreshold;
	readonly float rigidHitThreshold;
	readonly string? dumpFramesDir;
	OnnxEmbedder? embedder;
	bool clipFramesDumped;
	int dumpSequence;

	/// <param name="dumpFramesDir">Diagnostic: when set, every sampled frame is written as a PNG
	/// under this folder via <see cref="FrameDump"/> — the reference clip's frames once under
	/// <c>clip/</c>, then each candidate's search-window frames under a numbered subfolder in
	/// match order. Null (the default) dumps nothing.</param>
	public VisualBumperMatcher(
			double sampleInterval = DefaultSampleIntervalSeconds,
			float presenceThreshold = DefaultPresenceThreshold,
			float rigidHitThreshold = DefaultRigidHitThreshold,
			string? dumpFramesDir = null) {
		if (sampleInterval <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleInterval), "Sample interval must be positive.");
		this.sampleInterval = sampleInterval;
		this.presenceThreshold = presenceThreshold;
		this.rigidHitThreshold = rigidHitThreshold;
		this.dumpFramesDir = dumpFramesDir;
	}

	public string Name => "visual";

	public MatchResult Match(string referenceClipPath, string candidatePath, ClipRegion searchRegion, CancellationToken ct = default) {
		AiComponents.EnsureReady();
		embedder ??= new OnnxEmbedder(AiComponents.ModelPath);

		// The clip is dumped once per run, candidates every time (numbered in match order so the
		// dump folder reads in the same order as the printed report).
		string? clipDumpLabel = dumpFramesDir is null || clipFramesDumped ? null : "clip";
		var clipRec = Embed(referenceClipPath, clipDumpLabel, ct);
		if (clipDumpLabel is not null) clipFramesDumped = true;
		int clipUsable = clipRec.Frames.Count(f => f.Length > 0);
		if (clipUsable < 1)
			return new MatchResult(false, 0f, null, "reference clip produced no usable frames");

		using var window = ClipExtractor.ExtractToTemp(candidatePath, searchRegion);
		string? candidateDumpLabel = dumpFramesDir is null ? null
			: $"{++dumpSequence:000}-{Path.GetFileNameWithoutExtension(candidatePath)}";
		var candidateRec = Embed(window.Path, candidateDumpLabel, ct);
		int candidateUsable = candidateRec.Frames.Count(f => f.Length > 0);
		if (candidateUsable < 1)
			return new MatchResult(false, 0f, null, "no usable frames in the search window");

		// Presence matcher (ours): best per-frame cosine + how many clip frames appear anywhere
		// in the candidate's search window at >= presenceThreshold.
		float best = 0f;
		int bestFrame = -1;
		int hits = 0;
		for (int c = 0; c < clipRec.Frames.Length; c++) {
			if (clipRec.Frames[c].Length == 0) continue;
			float clipBest = 0f;
			for (int s = 0; s < candidateRec.Frames.Length; s++) {
				if (candidateRec.Frames[s].Length == 0) continue;
				float cos = EmbeddingMath.CosineSimilarity(clipRec.Frames[c], candidateRec.Frames[s]);
				if (cos > clipBest) clipBest = cos;
				if (cos > best) { best = cos; bestFrame = s; }
			}
			if (clipBest >= presenceThreshold) hits++;
		}
		double? bestTime = bestFrame < 0 ? null : bestFrame * sampleInterval;

		// Rigid matcher (VDF) for comparison/corroboration only — never gates the decision.
		bool rigidOk = ScanEngine.TryMatchDenseFrames(candidateRec, clipRec, rigidHitThreshold, out float rigidSim, out int rigidOffset);
		string rigid = rigidOk ? $"rigid={rigidSim:P0}@{rigidOffset}s" : "rigid:no";
		string detail = $"present={hits}/{clipUsable}  bestCos={best:P0}  {rigid}";

		return new MatchResult(hits >= 1, best, bestTime, detail);
	}

	DenseEmbeddingStore.DenseRecord Embed(string path, string? dumpLabel, CancellationToken ct) {
		byte[][]? frames = FfmpegEngine.GetDenseAiFrames(path, sampleInterval, MaxFramesPerFile, false, ct);
		if (frames is null || frames.Length == 0)
			return new DenseEmbeddingStore.DenseRecord(0, 0, (float)sampleInterval, Array.Empty<byte[]>());
		if (dumpFramesDir is not null && dumpLabel is not null)
			FrameDump.WritePngs(frames, Path.Combine(dumpFramesDir, dumpLabel));

		var emb = new byte[frames.Length][];
		var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
		var slots = new List<int>(OnnxEmbedder.MaxBatch);
		void Flush() {
			if (batch.Count == 0) return;
			byte[][] vectors = embedder!.EmbedBatchQuantized(batch);
			for (int k = 0; k < vectors.Length; k++) emb[slots[k]] = vectors[k];
			batch.Clear();
			slots.Clear();
		}
		for (int f = 0; f < frames.Length; f++) {
			emb[f] = Array.Empty<byte>();
			if (frames[f] is null || frames[f].Length == 0) continue;
			batch.Add(frames[f]);
			slots.Add(f);
			if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
		}
		Flush();
		return new DenseEmbeddingStore.DenseRecord(0, 0, (float)sampleInterval, emb);
	}

	public void Dispose() => embedder?.Dispose();
}
