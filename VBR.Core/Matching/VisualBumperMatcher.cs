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
using VBR.Core.Fingerprinting;
using VDF.Core;
using VDF.Core.AI;
using VDF.Core.Utils;

namespace VBR.Core.Matching;

/// <summary>
/// Visual DINOv2 presence matching — the PRIMARY bumper-matching signal (see
/// docs/design/matcher-spec.md). It is the only path validated on the bumpers this project
/// exists to remove: short, often *silent* studio/network idents, which audio cannot touch.
/// Ported from the validated probe (VDF.IntegrationTests/Comparison/VisualTailProbe.cs) with
/// two deliberate corrections over it, per the spec's 2026-07-18 correction (the probe shared
/// them as latent defects):
/// (1) frames come from <see cref="DenseFrameSampler"/> — a FULL decode of the short extract —
///     not the keyframe-only <c>GetDenseAiFrames</c> path, so <c>sampleInterval</c> yields
///     genuinely distinct frames instead of fps-filter duplicates of sparse keyframes; and
/// (2) low-information frames (near-black, blank/near-uniform, byte-duplicates) are excluded on
///     BOTH the clip and candidate sides via <see cref="FrameQuality"/> — such frames embed
///     near-identically regardless of content and fabricate false positives ("do not match on
///     black", spec §1).
///
/// Presence beats VDF's rigid ≥4-hit matcher for this case: short/static bumpers don't give the
/// rigid matcher four temporally-consistent frames to agree on one offset. Presence asks a
/// weaker, correct question instead — does any distinctive clip frame appear somewhere in the
/// candidate's search region at high cosine similarity? One such frame IS the detection. The
/// rigid matcher's own result is still computed and reported alongside as corroboration, not as
/// a requirement (see matcher-spec.md's anti-patterns: never require the rigid matcher to fire).
///
/// Owns an ONNX inference session for its lifetime — construct one instance and reuse it across
/// every candidate in a run; do not construct one per file. The reference clip's embeddings are
/// cached per instance (keyed by path), so a library run embeds the clip once, not per
/// candidate; call <see cref="PrepareClip"/> up front to surface an unusable clip before the
/// per-file loop starts.
/// </summary>
public sealed class VisualBumperMatcher : IBumperMatcher, IDisposable {
	/// <summary>Matches VisualTailProbe's coded default. Short clips need denser — down to
	/// ~0.2s (5 samples/sec); there is deliberately no floor enforced. With the full-decode
	/// sampler each step genuinely adds distinct frames (formerly, density past the keyframe
	/// cadence was an illusion — see docs/design/matcher-spec.md, 2026-07-18 correction).</summary>
	public const double DefaultSampleIntervalSeconds = 1.0;

	/// <summary>Matches VisualTailProbe's coded default (<c>BUMPER_PRESENCE</c>).</summary>
	public const float DefaultPresenceThreshold = 0.90f;

	/// <summary>Matches VisualTailProbe's coded default (<c>BUMPER_HIT_PERCENT</c>).</summary>
	public const float DefaultRigidHitThreshold = 0.89f;

	// VisualTailProbe's own cap; generous relative to any edge window this project searches
	// (a 30s window at the densest validated interval, 0.2s, is 150 frames).
	const int MaxFramesPerFile = 400;

	readonly double sampleInterval;
	readonly float presenceThreshold;
	readonly float rigidHitThreshold;
	readonly string? dumpFramesDir;
	readonly bool verboseLogging;
	OnnxEmbedder? embedder;
	bool clipFramesDumped;
	int dumpSequence;
	string? cachedClipPath;
	DenseEmbeddingStore.DenseRecord? cachedClipRecord;
	int cachedClipUsable;

	/// <param name="dumpFramesDir">Diagnostic: when set, every sampled frame is written as a PNG
	/// under this folder via <see cref="FrameDump"/> — the reference clip's frames once under
	/// <c>clip/</c>, then each candidate's search-window frames under a numbered subfolder in
	/// match order. Frames are dumped pre-filtering, so the dump shows the unfiltered truth.
	/// Null (the default) dumps nothing.</param>
	/// <param name="verboseLogging">Logs the resolved ONNX model path on first use and, per
	/// file, sampled/usable/filtered frame counts and each inference batch call, via
	/// <see cref="Logger"/> — concrete, per-run proof that the model is actually being loaded
	/// and invoked, not just trusted by reading the code.</param>
	public VisualBumperMatcher(
			double sampleInterval = DefaultSampleIntervalSeconds,
			float presenceThreshold = DefaultPresenceThreshold,
			float rigidHitThreshold = DefaultRigidHitThreshold,
			string? dumpFramesDir = null,
			bool verboseLogging = false) {
		if (sampleInterval <= 0)
			throw new ArgumentOutOfRangeException(nameof(sampleInterval), "Sample interval must be positive.");
		this.sampleInterval = sampleInterval;
		this.presenceThreshold = presenceThreshold;
		this.rigidHitThreshold = rigidHitThreshold;
		this.dumpFramesDir = dumpFramesDir;
		this.verboseLogging = verboseLogging;
	}

	public string Name => "visual";

	/// <summary>
	/// Embeds and caches the reference clip, failing loudly when it contains nothing to match
	/// on. Callers looping over a library should call this once before the loop; <see cref="Match"/>
	/// calls it lazily for one-off use.
	/// </summary>
	/// <exception cref="InvalidOperationException">The clip produced no usable frames after
	/// low-information filtering — matching on it would only ever match black/blank padding
	/// (the false-positive mode the filter exists to prevent), so this is an input error, not a
	/// "no match" result.</exception>
	public void PrepareClip(string referenceClipPath, CancellationToken ct = default) {
		AiComponents.EnsureReady();
		if (embedder is null) {
			if (verboseLogging)
				Logger.Instance.Info($"[visual] Loading ONNX model: {AiComponents.ModelPath}");
			embedder = new OnnxEmbedder(AiComponents.ModelPath);
			if (verboseLogging)
				Logger.Instance.Info("[visual] ONNX inference session ready.");
		}
		string? dumpLabel = dumpFramesDir is null || clipFramesDumped ? null : "clip";
		var record = Embed(referenceClipPath, dumpLabel, ct);
		if (dumpLabel is not null) clipFramesDumped = true;
		int usable = record.Frames.Count(f => f.Length > 0);
		if (usable < 1)
			throw new InvalidOperationException(
				"The reference clip produced no usable frames after low-information filtering — " +
				"every sampled frame is black, blank/uniform, or a duplicate, and matching on such " +
				"frames only ever produces false positives. Adjust --clip-length (or --region) so " +
				"the clip contains distinctive content, or lower --sample-interval for very short clips.");
		cachedClipPath = referenceClipPath;
		cachedClipRecord = record;
		cachedClipUsable = usable;
	}

	public MatchResult Match(string referenceClipPath, string candidatePath, ClipRegion searchRegion, CancellationToken ct = default) {
		if (cachedClipRecord is null || !string.Equals(cachedClipPath, referenceClipPath, StringComparison.OrdinalIgnoreCase))
			PrepareClip(referenceClipPath, ct);
		var clipRec = cachedClipRecord!;
		int clipUsable = cachedClipUsable;

		using var window = ClipExtractor.ExtractToTemp(candidatePath, searchRegion, verboseLogging);
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
		string detail = $"present={hits}/{clipUsable}  bestCos={best:P0}  {rigid}  win={candidateUsable}/{candidateRec.Frames.Length}";

		return new MatchResult(hits >= 1, best, bestTime, detail);
	}

	DenseEmbeddingStore.DenseRecord Embed(string path, string? dumpLabel, CancellationToken ct) {
		byte[][] frames = DenseFrameSampler.SampleFrames(path, sampleInterval, MaxFramesPerFile, ct);
		if (frames.Length == 0) {
			if (verboseLogging)
				Logger.Instance.Info($"[visual] '{Path.GetFileName(path)}': 0 frames sampled.");
			return new DenseEmbeddingStore.DenseRecord(0, 0, (float)sampleInterval, Array.Empty<byte[]>());
		}
		if (dumpFramesDir is not null && dumpLabel is not null)
			FrameDump.WritePngs(frames, Path.Combine(dumpFramesDir, dumpLabel));
		bool[] usable = FrameQuality.SelectUsable(frames);
		if (verboseLogging) {
			int usableCount = usable.Count(u => u);
			Logger.Instance.Info($"[visual] '{Path.GetFileName(path)}': {frames.Length} frames sampled, " +
				$"{usableCount} usable after low-information filtering ({frames.Length - usableCount} dropped).");
		}

		var emb = new byte[frames.Length][];
		var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
		var slots = new List<int>(OnnxEmbedder.MaxBatch);
		int batchCount = 0;
		void Flush() {
			if (batch.Count == 0) return;
			byte[][] vectors = embedder!.EmbedBatchQuantized(batch);
			if (verboseLogging)
				Logger.Instance.Info($"[visual] ONNX inference: embedded batch #{++batchCount} ({vectors.Length} frames, {vectors[0].Length}-byte quantized vectors).");
			for (int k = 0; k < vectors.Length; k++) emb[slots[k]] = vectors[k];
			batch.Clear();
			slots.Clear();
		}
		for (int f = 0; f < frames.Length; f++) {
			emb[f] = Array.Empty<byte>();
			if (!usable[f]) continue;
			batch.Add(frames[f]);
			slots.Add(f);
			if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
		}
		Flush();
		return new DenseEmbeddingStore.DenseRecord(0, 0, (float)sampleInterval, emb);
	}

	public void Dispose() => embedder?.Dispose();
}
