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
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Extraction;

/// <summary>Which edge of a video a bumper lives at. Drives both which part of a source video
/// becomes the reference clip, and which part of each candidate file gets searched — a bumper
/// lives at one edge, so one choice governs both (see docs/design/matcher-spec.md). "middle" is
/// a future addition for mid-video interstitials, not used yet. Lowercase members, matching
/// this codebase's convention for CLI-facing enums (e.g. FFHardwareAccelerationMode) so the
/// option value on the command line matches the member name exactly.</summary>
public enum ClipEdge { begin, end }

/// <summary>
/// A region of a source video to extract. Per "precision is the tool's job, not the user's"
/// (docs/design/bumper-catalog.md): callers never supply a pre-cut clip file — they point at a
/// video and a rough region, and this library extracts internally. <c>Start</c> null means
/// "measured backward from end of file" (see <see cref="EndOffset"/>); non-null means "measured
/// from the start of the file."
/// </summary>
public readonly record struct ClipRegion {
	public TimeSpan? Start { get; init; }

	/// <summary>Only meaningful when <see cref="Start"/> is null: how far before EOF this region's
	/// *start* sits. Equal to <see cref="Duration"/> means the region reaches the true end (what
	/// <see cref="Tail"/> sets); larger than <see cref="Duration"/> means it ends short of the true
	/// end (see <see cref="BeforeEnd"/>).</summary>
	public TimeSpan EndOffset { get; init; }

	public TimeSpan Duration { get; init; }

	/// <summary>The first <paramref name="duration"/> of the video.</summary>
	public static ClipRegion Head(TimeSpan duration) => new() { Start = TimeSpan.Zero, Duration = duration };

	/// <summary>The last <paramref name="duration"/> of the video.</summary>
	public static ClipRegion Tail(TimeSpan duration) => new() { Start = null, EndOffset = duration, Duration = duration };

	/// <summary>An explicit <paramref name="start"/>..<paramref name="start"/>+<paramref name="duration"/> range.</summary>
	public static ClipRegion At(TimeSpan start, TimeSpan duration) => new() { Start = start, Duration = duration };

	/// <summary>A <paramref name="duration"/>-long region starting <paramref name="endOffset"/>
	/// before EOF — like <see cref="Tail"/>, but for a window that doesn't reach the true end (e.g.
	/// the sparser, farther-out zone of a mixed-density edge fingerprint, which sits before the
	/// dense zone that itself reaches EOF). Requires <paramref name="endOffset"/> &gt;=
	/// <paramref name="duration"/>.</summary>
	public static ClipRegion BeforeEnd(TimeSpan endOffset, TimeSpan duration) => new() { Start = null, EndOffset = endOffset, Duration = duration };

	/// <summary><see cref="Head"/> for <see cref="ClipEdge.begin"/>, <see cref="Tail"/> for <see cref="ClipEdge.end"/>.</summary>
	public static ClipRegion For(ClipEdge edge, TimeSpan duration) => edge == ClipEdge.begin ? Head(duration) : Tail(duration);
}

/// <summary>A temp-file clip extracted by <see cref="ClipExtractor"/>. Disposing deletes it.</summary>
public readonly struct ExtractedClip : IDisposable {
	public string Path { get; }
	internal ExtractedClip(string path) => Path = path;
	public void Dispose() {
		try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort cleanup */ }
	}
}

/// <summary>
/// The one ffmpeg clip-extraction path (folds in what used to be duplicated between
/// AudioBumperMatcher and VDF.IntegrationTests' VisualTailProbe.ExtractTail) — used both to pull
/// the reference bumper clip out of a source video, and by the visual matcher to isolate each
/// candidate's search window before the expensive decode+embed step. Stream-copy is
/// keyframe-bound, so the actual start may land slightly earlier than requested; that's fine, we
/// only ever need a generous rough region, per docs/design/bumper-catalog.md. Stream-copy that
/// lands on corrupted source timestamps is a different, non-cosmetic failure mode — see
/// <see cref="Extract"/>'s doc comment for the duration-sanity-check + re-encode fallback that
/// catches it.
/// </summary>
public static class ClipExtractor {
	/// <summary>Extensions this project treats as video files.</summary>
	public static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".webm", ".wmv" };

	/// <param name="verbose">Logs the exact ffmpeg command line and the extracted temp file's
	/// size via <see cref="Logger"/> — proof of exactly what was run, for <c>--verbose</c>.</param>
	/// <param name="ct">Killing ffmpeg on cancellation is best-effort — see the doc comment on
	/// <see cref="Extract"/> for why a plain <see cref="CancellationToken.ThrowIfCancellationRequested"/>
	/// check alone wouldn't be enough.</param>
	/// <exception cref="FileNotFoundException">The source video does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="region"/>'s duration is not positive.</exception>
	/// <exception cref="InvalidOperationException">Extraction failed (ffmpeg missing, region out of range, etc.).</exception>
	public static ExtractedClip ExtractToTemp(string sourceVideoPath, ClipRegion region, bool verbose = false, CancellationToken ct = default) {
		if (!File.Exists(sourceVideoPath))
			throw new FileNotFoundException("Source video not found.", sourceVideoPath);
		if (region.Duration <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(region), "Clip region duration must be positive.");

		string outputPath = Path.Combine(Path.GetTempPath(), $"vbr_clip_{Guid.NewGuid():N}.mkv");
		if (!Extract(sourceVideoPath, region, outputPath, verbose, ct))
			throw new InvalidOperationException(
				$"Failed to extract the requested region from '{Path.GetFileName(sourceVideoPath)}' — " +
				"check ffmpeg is on PATH and the region fits within the file's duration.");
		if (verbose)
			Logger.Instance.Info($"[extract] '{Path.GetFileName(sourceVideoPath)}' -> {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
		return new ExtractedClip(outputPath);
	}

	// A requested-vs-actual gap this far past normal keyframe-snap slack (a fraction of a second —
	// see the removal path's own ~0.2s/1s margins) means stream-copy didn't just round to a nearby
	// keyframe; it silently produced a padded/corrupted result (see Extract's doc comment).
	const double StreamCopyDurationToleranceSeconds = 2.0;

	// Registers a kill-on-cancel callback independent of whatever this thread is currently
	// blocked on (ReadToEnd/WaitForExit below don't return early on their own) — same fix, same
	// rationale, as VBR.Core.Removal.ClipRemover.RunFfmpeg (2026-07-20): a Ctrl+C during a stuck
	// or merely slow ffmpeg call must not leave it running as an orphan.
	//
	// Stream-copy (tried first, for speed) can land on a run of non-monotonic DTS in the source —
	// observed on a real DVD-rip season where ffmpeg guesses replacement timestamps and *silently
	// succeeds* (exit 0, non-empty file) while padding the output far past what was requested (a
	// real case: a 5s tail request came out 14+s, mostly duplicated frames). Nothing downstream
	// throws on that — it looks like a normal short clip until DenseFrameSampler/FrameQuality
	// reject every sampled frame as duplicate/uniform padding. A related case: stream-copying a
	// *second* time (MixedDensitySampler.AppendZone carving a sub-region out of an already-extracted
	// temp file) from a source whose first extraction needed the re-encode fallback below can itself
	// produce an outright-corrupt Matroska remux (observed: "Duplicate element" / invalid EBML —
	// ffprobe can't read a duration back at all, not just an implausible one). So after a
	// stream-copy "success", verify the output's actual duration and retry with a re-encode (which
	// fixes broken timestamps as a side effect — the same workaround the original VisualTailProbe
	// spike used by hand) whenever it's missing/non-positive (unreadable) OR implausibly long
	// (padded). Re-encoding keeps both streams (unlike the removal path, this extractor is shared
	// with AudioBumperMatcher, which needs the audio intact).
	static bool Extract(string sourceVideoPath, ClipRegion region, string outputPath, bool verbose, CancellationToken ct = default) {
		if (!RunFfmpegExtract(sourceVideoPath, region, outputPath, streamCopy: true, verbose, ct))
			return false;

		double? actualSeconds = FFProbeEngine.GetMediaInfo(outputPath, extendedLogging: verbose)?.Duration.TotalSeconds;
		bool unreadable = actualSeconds is null or <= 0;
		bool padded = actualSeconds > region.Duration.TotalSeconds + StreamCopyDurationToleranceSeconds;
		if (unreadable || padded) {
			if (verbose)
				Logger.Instance.Warn($"[extract] stream-copy of '{Path.GetFileName(sourceVideoPath)}' produced " +
					$"{(unreadable ? "an unreadable result" : $"{actualSeconds:0.00}s")} for a " +
					$"{region.Duration.TotalSeconds:0.00}s request (likely non-monotonic timestamps at/near the seek " +
					"point in the source) — retrying with a re-encode.");
			return RunFfmpegExtract(sourceVideoPath, region, outputPath, streamCopy: false, verbose, ct);
		}
		return true;
	}

	/// <summary>Appends the <c>-ss</c>/<c>-sseof</c> input-seek args for <paramref name="region"/> —
	/// shared with <see cref="Fingerprinting.DenseFrameSampler"/>'s region-aware overload so a
	/// direct source decode uses exactly the same seek semantics this extractor does.</summary>
	internal static void AppendSeekArgs(System.Collections.Generic.IList<string> argumentList, ClipRegion region) {
		if (region.Start is { } start) {
			argumentList.Add("-ss");
			argumentList.Add(start.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		}
		else {
			argumentList.Add("-sseof");
			argumentList.Add((-region.EndOffset.TotalSeconds).ToString(CultureInfo.InvariantCulture));
		}
	}

	static bool RunFfmpegExtract(string sourceVideoPath, ClipRegion region, string outputPath, bool streamCopy, bool verbose, CancellationToken ct) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-y");
		if (HardwareAcceleration.Enabled) {
			psi.ArgumentList.Add("-hwaccel");
			psi.ArgumentList.Add(HardwareAcceleration.Mode.ToString());
		}
		AppendSeekArgs(psi.ArgumentList, region);
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(sourceVideoPath);
		psi.ArgumentList.Add("-t");
		psi.ArgumentList.Add(region.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		if (streamCopy) {
			psi.ArgumentList.Add("-c");
			psi.ArgumentList.Add("copy");
		}
		else {
			psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
			psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
		}
		psi.ArgumentList.Add(outputPath);
		if (verbose)
			Logger.Instance.Info($"[extract] {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
		using var p = Process.Start(psi)!;
		using CancellationTokenRegistration killOnCancel = ct.Register(static s => {
			var proc = (Process)s!;
			try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
		}, p);
		try {
			// Concurrent, not sequential — draining stderr to completion before touching stdout
			// (or vice versa) risks deadlocking ffmpeg against this process if the undrained
			// stream's pipe buffer fills. Same fix as ClipRemover.RunFfmpeg (2026-07-20).
			Task<string> stderrTask = p.StandardError.ReadToEndAsync();
			Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
			p.WaitForExit(60_000);
			ct.ThrowIfCancellationRequested();
			Task.WaitAll(stderrTask, stdoutTask);
			bool ok = p.HasExited && p.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
			if (!ok && verbose)
				Logger.Instance.Warn($"[extract] ffmpeg exit {(p.HasExited ? p.ExitCode : "(still running)")}: {stderrTask.Result.Trim()}");
			return ok;
		}
		catch (OperationCanceledException) { throw; }
		catch { return false; }
	}
}
