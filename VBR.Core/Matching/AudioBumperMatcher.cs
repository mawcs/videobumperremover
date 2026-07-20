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
using System.IO;
using System.Threading;
using VBR.Core.Extraction;
using VDF.Core;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Matching;

/// <summary>
/// Audio-fingerprint bumper matching — the secondary *accelerator* signal (see
/// docs/design/matcher-spec.md): works only for audible bumpers, dead for silent/varying-audio
/// ones (i.e. dead for the common case this project targets — never the only signal consulted).
/// Calls VDF.Core's audio-fingerprint primitives directly (<see cref="ChromaprintEngine"/>,
/// <see cref="ScanEngine.SlidingWindowCompare"/>), bypassing VDF's own dedup gates (min
/// clip/source duration ratio, the 95% "too similar in length" ceiling) which assume whole-file
/// comparison, not a short embedded clip.
///
/// Unlike the visual matcher, this does not physically extract each candidate's search window
/// first: audio fingerprinting a whole file is cheap (docs/research/vdf-evaluation.md — decode
/// dominates, not fingerprinting), so this fingerprints the whole candidate once and slices the
/// resulting array to the requested region.
/// </summary>
public sealed class AudioBumperMatcher : IBumperMatcher {
	/// <summary>Matches VDF's own <c>Settings.PartialClipSimilarityThreshold</c> default
	/// (VDF.Core/Settings.cs). Validated bumper probes scored 85-98% on true matches, so this
	/// leaves headroom without inviting false positives.</summary>
	public const float DefaultMinSimilarity = 0.80f;

	readonly float minSimilarity;
	readonly bool verboseLogging;

	/// <param name="verboseLogging">Logs each fingerprint extraction (block count) and the
	/// resulting comparison via <see cref="Logger"/>, and raises VDF's own ffmpeg/Chromaprint
	/// extraction logging to <c>extendedLogging</c> level — for <c>--verbose</c>.</param>
	public AudioBumperMatcher(float minSimilarity = DefaultMinSimilarity, bool verboseLogging = false) {
		this.minSimilarity = minSimilarity;
		this.verboseLogging = verboseLogging;
	}

	public string Name => "audio";

	public MatchResult Match(string referenceClipPath, string candidatePath, ClipRegion searchRegion, CancellationToken ct = default) {
		uint[]? clipFingerprint = ChromaprintEngine.ExtractFingerprint(referenceClipPath, verboseLogging, ct);
		if (clipFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio fingerprint on the reference clip");
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(referenceClipPath)}': fingerprint extracted, {clipFingerprint.Length} blocks.");

		uint[]? fileFingerprint = ChromaprintEngine.ExtractFingerprint(candidatePath, verboseLogging, ct);
		if (fileFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio track");
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(candidatePath)}': fingerprint extracted, {fileFingerprint.Length} blocks.");

		(int start, int count) = ResolveWindow(fileFingerprint.Length, searchRegion);
		if (count < clipFingerprint.Length)
			return new MatchResult(false, 0f, null, "search window too short to hold the clip");

		var (similarity, offsetBlocks) = ScanEngine.SlidingWindowCompare(
			clipFingerprint, fileFingerprint[start..(start + count)], minSim: 0f);
		int offset = start + offsetBlocks;
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(candidatePath)}': sliding-window compare over blocks [{start}, {start + count}) -> similarity={similarity:P1} @ offset {offset}s.");
		return new MatchResult(similarity >= minSimilarity, similarity, offset, $"audio={similarity:P0}@{offset}s");
	}

	// Chroma fingerprint blocks are ~1s each, so seconds ≈ block index.
	static (int start, int count) ResolveWindow(int fileLengthBlocks, ClipRegion region) {
		int durationBlocks = Math.Max(1, (int)Math.Round(region.Duration.TotalSeconds));
		if (region.Start is { } start) {
			int startBlocks = Math.Clamp((int)Math.Round(start.TotalSeconds), 0, fileLengthBlocks);
			return (startBlocks, Math.Min(durationBlocks, fileLengthBlocks - startBlocks));
		}
		int count = Math.Min(durationBlocks, fileLengthBlocks);
		return (fileLengthBlocks - count, count);
	}
}
