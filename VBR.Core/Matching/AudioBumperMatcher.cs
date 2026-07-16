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
using System.Globalization;
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
/// populated only when the caller requested positional search windows and the file was long
/// enough to search them.</summary>
public readonly record struct BumperMatch(string FilePath, WindowResult? Full, WindowResult? Head, WindowResult? Tail);

/// <summary>
/// A region of a source video to use as the reference bumper clip. Per "precision is the
/// tool's job, not the user's" (docs/design/bumper-catalog.md): callers never supply a
/// pre-cut clip file — they point at a source video and a rough region, and this library
/// extracts the clip internally. <c>Start</c> null means "measured from end of file" (the
/// last <see cref="Duration"/> seconds); non-null means "measured from the start of the file."
/// </summary>
public readonly record struct ClipRegion {
	public TimeSpan? Start { get; init; }
	public TimeSpan Duration { get; init; }

	/// <summary>The first <paramref name="duration"/> of the source video.</summary>
	public static ClipRegion Head(TimeSpan duration) => new() { Start = TimeSpan.Zero, Duration = duration };

	/// <summary>The last <paramref name="duration"/> of the source video.</summary>
	public static ClipRegion Tail(TimeSpan duration) => new() { Start = null, Duration = duration };

	/// <summary>An explicit <paramref name="start"/>..<paramref name="start"/>+<paramref name="duration"/> range.</summary>
	public static ClipRegion At(TimeSpan start, TimeSpan duration) => new() { Start = start, Duration = duration };
}

/// <summary>
/// Finds a known bumper's audio fingerprint inside a folder of library videos — the
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
	/// Extracts <paramref name="region"/> from <paramref name="sourceVideoPath"/> as the
	/// reference bumper clip, fingerprints it and every video in <paramref name="libraryFolder"/>
	/// (non-recursive), then reports the best full-file alignment for each. Files with no
	/// usable audio track are skipped, not reported. <paramref name="sourceVideoPath"/> itself
	/// is excluded from the library results (it's typically one of the episodes being searched).
	///
	/// <paramref name="headSeconds"/>/<paramref name="tailSeconds"/> additionally search only
	/// the first/last N seconds of each library file when &gt; 0 — shrinking the offset space
	/// this way rescues short *audible* bumpers that the full-file search misses (see the
	/// positional-window finding in docs/research/vdf-evaluation.md). This is a different
	/// concept from <paramref name="region"/>: <paramref name="region"/> picks the reference
	/// clip out of the source video; these narrow the search inside each candidate file.
	/// </summary>
	/// <exception cref="FileNotFoundException">The source video does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/>'s duration is not positive.</exception>
	/// <exception cref="DirectoryNotFoundException">The library folder does not exist.</exception>
	/// <exception cref="InvalidOperationException">The clip could not be extracted, or has no usable audio track.</exception>
	public static IReadOnlyList<BumperMatch> FindInLibrary(
			string sourceVideoPath,
			ClipRegion region,
			string libraryFolder,
			int headSeconds = 0,
			int tailSeconds = 0,
			CancellationToken ct = default) {

		if (!File.Exists(sourceVideoPath))
			throw new FileNotFoundException("Source video not found.", sourceVideoPath);
		if (region.Duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(region), "Clip region duration must be positive.");
		if (!Directory.Exists(libraryFolder))
			throw new DirectoryNotFoundException($"Library folder not found: {libraryFolder}");

		string clipTemp = Path.Combine(Path.GetTempPath(), $"vbr_clip_{Guid.NewGuid():N}.mkv");
		try {
			if (!ExtractClip(sourceVideoPath, region, clipTemp))
				throw new InvalidOperationException(
					$"Failed to extract the requested region from '{Path.GetFileName(sourceVideoPath)}' — " +
					"check ffmpeg is on PATH and the region fits within the file's duration.");

			uint[]? clipFingerprint = ChromaprintEngine.ExtractFingerprint(clipTemp, extendedLogging: false, ct);
			if (clipFingerprint is not { Length: >= 2 })
				throw new InvalidOperationException(
					$"The extracted region of '{Path.GetFileName(sourceVideoPath)}' produced no usable audio " +
					"fingerprint — does that part of the video have an audio track?");

			var matches = new List<BumperMatch>();
			var libraryFiles = Directory.EnumerateFiles(libraryFolder)
				.Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
				.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(sourceVideoPath), StringComparison.OrdinalIgnoreCase))
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
		finally {
			try { if (File.Exists(clipTemp)) File.Delete(clipTemp); } catch { /* best effort cleanup */ }
		}
	}

	/// <summary>Stream-copy extraction of <paramref name="region"/> from
	/// <paramref name="sourceVideoPath"/> via the ffmpeg CLI — same approach as
	/// VDF.IntegrationTests' VisualTailProbe.ExtractTail. Stream-copy is keyframe-bound, so the
	/// actual start may land slightly earlier than requested; that's fine here — we only ever
	/// need a "generous rough region," per docs/design/bumper-catalog.md, not a frame-accurate
	/// cut.</summary>
	static bool ExtractClip(string sourceVideoPath, ClipRegion region, string outputPath) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-y");
		if (region.Start is { } start) {
			psi.ArgumentList.Add("-ss");
			psi.ArgumentList.Add(start.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		}
		else {
			psi.ArgumentList.Add("-sseof");
			psi.ArgumentList.Add((-region.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
		}
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(sourceVideoPath);
		psi.ArgumentList.Add("-t");
		psi.ArgumentList.Add(region.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add("copy");
		psi.ArgumentList.Add(outputPath);
		try {
			using var p = Process.Start(psi)!;
			p.StandardError.ReadToEnd();
			p.StandardOutput.ReadToEnd();
			p.WaitForExit(60_000);
			return p.HasExited && p.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
		}
		catch { return false; }
	}
}
