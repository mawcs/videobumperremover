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
using VDF.Core;
using VDF.Core.FFTools;

namespace VBR.Core.Matching;

/// <summary>A sliding-window audio-fingerprint comparison against one candidate file.
/// <c>OffsetSeconds</c> is where the clip's fingerprint best aligns within the compared
/// window (chroma fingerprint blocks are ~1s each).</summary>
public readonly record struct WindowResult(float Similarity, int OffsetSeconds);

/// <summary>One library file compared against the bumper clip. <c>Head</c>/<c>Tail</c> are
/// populated only when the caller requested positional windows and the file was long enough
/// to search them.</summary>
public readonly record struct BumperMatch(string FilePath, WindowResult? Full, WindowResult? Head, WindowResult? Tail);

/// <summary>
/// Finds a known bumper clip's audio fingerprint inside a folder of library videos — the
/// "video → catalog" matching direction (see docs/ROADMAP.md Phase 2/3): a short catalog
/// bumper searched for across full-length files, as opposed to VDF's native whole-file dedup.
///
/// Calls VDF.Core's audio-fingerprint primitives (<see cref="ChromaprintEngine"/>,
/// <see cref="ScanEngine.SlidingWindowCompare"/>) directly, bypassing VDF's own dedup gates
/// (min clip/source duration ratio, the 95% "too similar in length" ceiling) which assume
/// whole-file comparison, not a short embedded clip. Validated on real bumpers — see
/// docs/research/vdf-evaluation.md (audio matcher + positional-window findings).
/// </summary>
public static class AudioBumperMatcher {
	/// <summary>Matches VDF's own <c>Settings.PartialClipSimilarityThreshold</c> default
	/// (VDF.Core/Settings.cs). Validated bumper probes scored 85-98% on true matches, so this
	/// leaves headroom without inviting false positives. Purely a suggested default — this
	/// class returns raw scores and leaves the match/no-match call to the caller.</summary>
	public const float DefaultMinSimilarity = 0.80f;

	static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".webm", ".wmv" };

	/// <summary>
	/// Fingerprints <paramref name="bumperClipPath"/> and every video in
	/// <paramref name="libraryFolder"/> (non-recursive), then reports the best full-file
	/// alignment for each. Files with no usable audio track are skipped, not reported.
	///
	/// <paramref name="headSeconds"/>/<paramref name="tailSeconds"/> additionally search only
	/// the first/last N seconds of each file when &gt; 0 — shrinking the offset space this way
	/// rescues short *audible* bumpers that the full-file search misses (see the positional-
	/// window finding in docs/research/vdf-evaluation.md).
	/// </summary>
	/// <exception cref="FileNotFoundException">The clip does not exist.</exception>
	/// <exception cref="DirectoryNotFoundException">The library folder does not exist.</exception>
	/// <exception cref="InvalidOperationException">The clip has no usable audio track.</exception>
	public static IReadOnlyList<BumperMatch> FindInLibrary(
			string bumperClipPath,
			string libraryFolder,
			int headSeconds = 0,
			int tailSeconds = 0,
			CancellationToken ct = default) {

		if (!File.Exists(bumperClipPath))
			throw new FileNotFoundException("Bumper clip not found.", bumperClipPath);
		if (!Directory.Exists(libraryFolder))
			throw new DirectoryNotFoundException($"Library folder not found: {libraryFolder}");

		uint[]? clipFingerprint = ChromaprintEngine.ExtractFingerprint(bumperClipPath, extendedLogging: false, ct);
		if (clipFingerprint is not { Length: >= 2 })
			throw new InvalidOperationException(
				$"'{Path.GetFileName(bumperClipPath)}' produced no usable audio fingerprint — does it have an audio track?");

		var matches = new List<BumperMatch>();
		var libraryFiles = Directory.EnumerateFiles(libraryFolder)
			.Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(bumperClipPath), StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

		foreach (string file in libraryFiles) {
			ct.ThrowIfCancellationRequested();

			uint[]? fileFingerprint = ChromaprintEngine.ExtractFingerprint(file, extendedLogging: false, ct);
			if (fileFingerprint is not { Length: >= 2 })
				continue; // no usable audio track — not a candidate for audio-fingerprint matching

			WindowResult? Compare(int start, int count) {
				if (count < clipFingerprint.Length) return null; // window too short to hold the clip
				var (similarity, offset) = ScanEngine.SlidingWindowCompare(
					clipFingerprint, fileFingerprint[start..(start + count)], minSim: 0f);
				return new WindowResult(similarity, start + offset);
			}

			var full = Compare(0, fileFingerprint.Length);
			var head = headSeconds > 0 ? Compare(0, Math.Min(headSeconds, fileFingerprint.Length)) : null;
			var tail = tailSeconds > 0
				? Compare(Math.Max(0, fileFingerprint.Length - tailSeconds), Math.Min(tailSeconds, fileFingerprint.Length))
				: null;

			matches.Add(new BumperMatch(file, full, head, tail));
		}

		return matches;
	}
}
