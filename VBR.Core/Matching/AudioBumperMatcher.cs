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
		double bucketSeconds = Configuration.VbrConfig.Current.Audio.BucketSeconds;
		uint[]? clipFingerprint = ChromaprintEngine.ExtractFingerprint(referenceClipPath, verboseLogging, ct, bucketSeconds: bucketSeconds);
		if (clipFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio fingerprint on the reference clip");
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(referenceClipPath)}': fingerprint extracted, {clipFingerprint.Length} blocks.");

		uint[]? fileFingerprint = ChromaprintEngine.ExtractFingerprint(candidatePath, verboseLogging, ct, bucketSeconds: bucketSeconds);
		if (fileFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio track");
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(candidatePath)}': fingerprint extracted, {fileFingerprint.Length} blocks.");

		(int start, int count) = ResolveWindow(fileFingerprint.Length, searchRegion, bucketSeconds);
		if (count < clipFingerprint.Length)
			return new MatchResult(false, 0f, null, "search window too short to hold the clip");

		var (similarity, offsetBlocks) = ScanEngine.SlidingWindowCompare(
			clipFingerprint, fileFingerprint[start..(start + count)], minSim: 0f);
		double offsetSeconds = (start + offsetBlocks) * bucketSeconds;
		if (verboseLogging)
			Logger.Instance.Info($"[audio] '{Path.GetFileName(candidatePath)}': sliding-window compare over blocks [{start}, {start + count}) -> similarity={similarity:P1} @ offset {offsetSeconds:0.###}s.");
		return new MatchResult(similarity >= minSimilarity, similarity, offsetSeconds, $"audio={similarity:P0}@{offsetSeconds:0.#}s");
	}

	/// <summary>
	/// Thin public wrapper around VDF.Core's <c>internal</c> <see cref="ChromaprintEngine.ExtractFingerprint"/>
	/// — <c>VBR.CLI</c> has no <c>InternalsVisibleTo</c> grant from VDF.Core (only <c>VBR.Core</c>
	/// does, see ADR 0005), so a caller in <c>VBR.CLI</c> (<c>MatchingSession</c>, computing a
	/// reference/candidate fingerprint once up front — see <see cref="MatchFingerprints"/>'s doc
	/// comment) needs a public entry point into the exact same extraction <see cref="Match"/> uses
	/// internally, rather than a second, divergent implementation.
	/// </summary>
	/// <param name="bucketSeconds">Null (the default) reads <c>VbrConfig.Current.Audio.BucketSeconds</c>
	/// at call time — every real caller omits this and gets whatever's currently configured; an
	/// explicit value exists only for tests that want a specific bucket size independent of
	/// <c>VbrConfig.Current</c>.</param>
	public static uint[]? ExtractFingerprint(string path, bool verboseLogging = false, CancellationToken ct = default, double? bucketSeconds = null) =>
		ChromaprintEngine.ExtractFingerprint(path, verboseLogging, ct, bucketSeconds: bucketSeconds ?? Configuration.VbrConfig.Current.Audio.BucketSeconds);

	/// <summary>
	/// Same comparison as <see cref="Match"/>, but takes two already-computed whole-file
	/// Chromaprint fingerprints instead of extracting them from files itself — the entry point for
	/// a scanned library database's cached <c>LibraryDatabaseEntry.AudioFingerprint</c> and/or a
	/// bumper catalog entry's own <c>BumperCatalogEntry.AudioFingerprint</c>
	/// (docs/iterativeplan.md, "Utilizing Databases" entry): both are computed by the exact same
	/// <see cref="ChromaprintEngine.ExtractFingerprint"/> call <see cref="Match"/> makes, so reusing
	/// them here is equivalent to (but far cheaper than) re-extracting. Static and state-free, same
	/// reasoning as <see cref="VisualBumperMatcher.MatchMixedDensity"/>.
	///
	/// <paramref name="bucketSeconds"/> is used only to interpret block indices into seconds and
	/// resolve the search window (<see cref="ResolveWindow"/>) — it does <b>not</b> verify the two
	/// fingerprints were actually built at this bucket size. A stored fingerprint built under a
	/// different <c>audio.bucketSeconds</c> than what's currently configured is not meaningfully
	/// comparable at all (see <c>VbrConfig.AudioConfig</c>'s own doc comment); catching that is
	/// <c>FrameQualitySnapshot.DescribeMismatchFromCurrent</c>'s job at the catalog/database level,
	/// not this method's.
	/// </summary>
	/// <param name="bucketSeconds">Null (the default) reads <c>VbrConfig.Current.Audio.BucketSeconds</c>
	/// at call time, same convention as <see cref="ExtractFingerprint"/>.</param>
	public static MatchResult MatchFingerprints(uint[]? clipFingerprint, uint[]? fileFingerprint, ClipRegion searchRegion, float minSimilarity, double? bucketSeconds = null) {
		if (clipFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio fingerprint on the reference clip");
		if (fileFingerprint is not { Length: >= 2 })
			return new MatchResult(false, 0f, null, "no usable audio track");

		double effectiveBucketSeconds = bucketSeconds ?? Configuration.VbrConfig.Current.Audio.BucketSeconds;
		(int start, int count) = ResolveWindow(fileFingerprint.Length, searchRegion, effectiveBucketSeconds);
		if (count < clipFingerprint.Length)
			return new MatchResult(false, 0f, null, "search window too short to hold the clip");

		var (similarity, offsetBlocks) = ScanEngine.SlidingWindowCompare(
			clipFingerprint, fileFingerprint[start..(start + count)], minSim: 0f);
		double offsetSeconds = (start + offsetBlocks) * effectiveBucketSeconds;
		return new MatchResult(similarity >= minSimilarity, similarity, offsetSeconds, $"audio={similarity:P0}@{offsetSeconds:0.#}s");
	}

	// Chroma fingerprint blocks are bucketSeconds each (docs/iterativeplan.md, "Audio bucket
	// phase-alignment" entry, 2026-08-14 -- was a hardcoded "~1s each, so seconds ≈ block index"
	// before that entry's fix).
	static (int start, int count) ResolveWindow(int fileLengthBlocks, ClipRegion region, double bucketSeconds) {
		int durationBlocks = Math.Max(1, (int)Math.Round(region.Duration.TotalSeconds / bucketSeconds));
		if (region.Start is { } start) {
			int startBlocks = Math.Clamp((int)Math.Round(start.TotalSeconds / bucketSeconds), 0, fileLengthBlocks);
			return (startBlocks, Math.Min(durationBlocks, fileLengthBlocks - startBlocks));
		}
		int count = Math.Min(durationBlocks, fileLengthBlocks);
		return (fileLengthBlocks - count, count);
	}
}
