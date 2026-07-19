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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using VBR.Core.Extraction;
using VDF.Core.FFTools;

namespace VBR.Core.Removal;

/// <summary>Which removal mechanism produced an output. See ADR 0007
/// (docs/decisions/0007-removal-command.md): re-encode is the eventual default (frame-accurate,
/// correct subtitle realignment); stream-copy is being built first per the maintainer's build
/// order (fast to iterate on for testing) and is an explicit, documented v1 exception.</summary>
public enum RemovalMode { StreamCopy, ReEncode }

/// <summary>Everything needed to audit or undo one cut — written as a JSON sidecar next to the
/// output file (per AGENTS.md's standing "always keep a manifest" rule; ADR 0007 left the exact
/// schema open, this is the first concrete shape). Undo is simply deleting <see cref="Output"/>;
/// <see cref="Source"/> is never modified.</summary>
public sealed record RemovalManifestEntry(
	string Tool,
	string TimestampUtc,
	string Source,
	string Output,
	string Region,
	double BumperLengthSeconds,
	double SourceDurationSeconds,
	double CutPointSeconds,
	string Mode,
	string? MatchDetail);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RemovalManifestEntry))]
internal partial class RemovalJsonContext : JsonSerializerContext { }

/// <summary>Result of one successful removal, for CLI reporting.</summary>
public sealed record RemovalResult(
	string OutputPath,
	string ManifestPath,
	TimeSpan SourceDuration,
	TimeSpan CutPoint,
	RemovalMode Mode);

/// <summary>
/// Cuts a bumper from one edge of a video file, non-destructively (ADR 0007:
/// docs/decisions/0007-removal-command.md). The cut point is <b>arithmetic, not detected per
/// file</b> — <c>fileDuration − bumperLength</c> (end) or <c>bumperLength</c> (begin) — trusting
/// that <paramref name="bumperLength"/> was measured precisely once (at clip selection /
/// enrollment), per the empirical finding that bumper duration is consistent to ~0.02s across
/// studios and lengths (docs/research/vdf-evaluation.md, 2026-07-19).
/// </summary>
public static class ClipRemover {
	/// <summary>
	/// Stream-copy (<c>-c copy</c>) cannot cut at an arbitrary timestamp — it can only start or
	/// stop cleanly at a keyframe. Two behaviors were verified empirically against real media
	/// (2026-07-19, Daredevil S01E01's Netflix ident) before writing this:
	/// <list type="bullet">
	/// <item><b>Begin-region (seeking forward into the file):</b> placing <c>-ss</c> AFTER
	/// <c>-i</c> ("output seeking") snaps FORWARD to the next keyframe at or after the requested
	/// time — verified: seeking to 5s (inside the Netflix card, which starts at 2.607s) landed
	/// on the NEXT ident (6.027s), never backward into the card. This is the safe direction: the
	/// kept segment can only start at or after the true boundary, never before it.</item>
	/// <item><b>End-region (stopping mid-file):</b> <c>-t</c>/<c>-to</c> does NOT stop where
	/// requested — it overshoots by a small, roughly constant amount (~0.2s observed,
	/// independent of the target) to complete the current frame-reorder buffer. Targeting a
	/// value that is itself an exact keyframe timestamp still overshot into that keyframe's
	/// content. So an end cut must target a keyframe comfortably BEFORE the true cut point — see
	/// <see cref="EndCutOvershootSafetyMarginSeconds"/> — never the nearest one.</item>
	/// </list>
	/// </summary>
	public const double EndCutOvershootSafetyMarginSeconds = 1.0;

	// How far back from the computed cut point to search for a safe keyframe. Generous relative
	// to typical GOP lengths (~1-10s); if no keyframe is found in this window the file's
	// keyframe spacing is unusually sparse and we fail loudly rather than guess.
	const double KeyframeSearchWindowSeconds = 30.0;

	/// <exception cref="NotSupportedException"><paramref name="mode"/> is
	/// <see cref="RemovalMode.ReEncode"/> — not yet implemented (ADR 0007: stream-copy is being
	/// built first for fast iteration; re-encode is next).</exception>
	/// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> does not exist.</exception>
	/// <exception cref="InvalidOperationException">Duration probing or the ffmpeg cut failed, or
	/// no safe keyframe could be found before the computed end-region cut point.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="bumperLength"/> is not
	/// positive, or is not shorter than the source file's duration.</exception>
	public static RemovalResult Remove(string sourcePath, ClipEdge region, TimeSpan bumperLength,
			RemovalMode mode, string? matchDetail = null, CancellationToken ct = default) {
		if (mode == RemovalMode.ReEncode)
			throw new NotSupportedException(
				"Re-encode removal (Mode B) is not implemented yet — pass --re-encode false to " +
				"remove via stream-copy for now. See docs/decisions/0007-removal-command.md.");
		if (bumperLength <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(bumperLength), "Bumper length must be positive.");
		if (!File.Exists(sourcePath))
			throw new FileNotFoundException("Source video not found.", sourcePath);

		TimeSpan sourceDuration = ProbeDuration(sourcePath);
		if (bumperLength >= sourceDuration)
			throw new ArgumentOutOfRangeException(nameof(bumperLength),
				$"Bumper length ({bumperLength.TotalSeconds:0.###}s) must be shorter than the " +
				$"source file's duration ({sourceDuration.TotalSeconds:0.###}s).");

		string outputPath = BuildOutputPath(sourcePath);
		double cutPointSeconds;
		if (region == ClipEdge.begin) {
			cutPointSeconds = bumperLength.TotalSeconds;
			RunFfmpegOutputSeekCopy(sourcePath, cutPointSeconds, outputPath, ct);
		}
		else {
			double naiveCutPoint = sourceDuration.TotalSeconds - bumperLength.TotalSeconds;
			cutPointSeconds = FindSafeEndCutPoint(sourcePath, naiveCutPoint, ct);
			RunFfmpegDurationCopy(sourcePath, cutPointSeconds, outputPath, ct);
		}

		string manifestPath = Path.ChangeExtension(outputPath, ".json");
		var entry = new RemovalManifestEntry(
			Tool: "vbr remove",
			TimestampUtc: DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
			Source: Path.GetFullPath(sourcePath),
			Output: Path.GetFullPath(outputPath),
			Region: region.ToString(),
			BumperLengthSeconds: bumperLength.TotalSeconds,
			SourceDurationSeconds: sourceDuration.TotalSeconds,
			CutPointSeconds: cutPointSeconds,
			Mode: mode.ToString(),
			MatchDetail: matchDetail);
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(entry, RemovalJsonContext.Default.RemovalManifestEntry));

		return new RemovalResult(outputPath, manifestPath, sourceDuration, TimeSpan.FromSeconds(cutPointSeconds), mode);
	}

	/// <summary>Inserts <c>.vbr</c> before the extension, beside the source (ADR 0007's
	/// non-destructive default — e.g. <c>S01E01.mkv</c> → <c>S01E01.vbr.mkv</c>). Same directory
	/// as the source, per the maintainer's stated reasoning: co-located files are far easier to
	/// select together and drop into a player (MPC/VLC) for quick side-by-side verification than
	/// hunting across a separate staging folder.</summary>
	public static string BuildOutputPath(string sourcePath) {
		string? dir = Path.GetDirectoryName(sourcePath);
		string name = Path.GetFileNameWithoutExtension(sourcePath);
		string ext = Path.GetExtension(sourcePath);
		string fileName = $"{name}.vbr{ext}";
		return dir is { Length: > 0 } ? Path.Combine(dir, fileName) : fileName;
	}

	static TimeSpan ProbeDuration(string path) {
		var info = FFProbeEngine.GetMediaInfo(path, extendedLogging: false);
		if (info is null || info.Duration <= TimeSpan.Zero)
			throw new InvalidOperationException($"Could not determine duration for '{Path.GetFileName(path)}' (ffprobe failed or reported no duration).");
		return info.Duration;
	}

	// Finds the largest keyframe timestamp k such that k + EndCutOvershootSafetyMarginSeconds <=
	// naiveCutPoint, searching backward from naiveCutPoint within KeyframeSearchWindowSeconds.
	static double FindSafeEndCutPoint(string path, double naiveCutPoint, CancellationToken ct) {
		if (naiveCutPoint <= 0)
			throw new InvalidOperationException("Computed cut point is at or before the start of the file — bumper length may be wrong for this file.");
		double windowStart = Math.Max(0, naiveCutPoint - KeyframeSearchWindowSeconds);
		double[] keyframes = ProbeKeyframeTimestamps(path, windowStart, naiveCutPoint, ct);
		double target = naiveCutPoint - EndCutOvershootSafetyMarginSeconds;
		double best = keyframes.Where(k => k <= target).DefaultIfEmpty(-1).Max();
		if (best < 0) {
			// windowStart itself is always a valid (if distant) fallback: ffmpeg starts decoding
			// from file position 0 regardless, so "-t windowStart" is always achievable — but
			// only accept it if it still respects the safety margin; otherwise the file's
			// keyframes are too sparse for this margin/window and we fail loudly rather than risk
			// leaking bumper content.
			if (windowStart <= target && windowStart > 0) return windowStart;
			throw new InvalidOperationException(
				$"No keyframe found more than {EndCutOvershootSafetyMarginSeconds:0.#}s before the " +
				$"computed cut point ({naiveCutPoint:0.###}s) within the last {KeyframeSearchWindowSeconds:0.#}s " +
				"searched — this file's keyframe spacing is unusually sparse for stream-copy removal.");
		}
		return best;
	}

	static double[] ProbeKeyframeTimestamps(string path, double startSeconds, double endSeconds, CancellationToken ct) {
		var psi = new ProcessStartInfo {
			FileName = FFProbeEngine.FFprobePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
		psi.ArgumentList.Add("-skip_frame"); psi.ArgumentList.Add("nokey");
		psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("frame=pts_time");
		psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("csv=p=0");
		psi.ArgumentList.Add("-read_intervals");
		psi.ArgumentList.Add(FormattableString.Invariant($"{startSeconds:0.###}%{endSeconds:0.###}"));
		psi.ArgumentList.Add(path);

		using var process = new Process { StartInfo = psi };
		process.Start();
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(30_000)) {
			try { process.Kill(); } catch { }
			throw new InvalidOperationException($"ffprobe timed out probing keyframes in '{Path.GetFileName(path)}'.");
		}
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"ffprobe failed probing keyframes (exit {process.ExitCode}): {stderr.Trim()}");

		var timestamps = new List<double>();
		foreach (string line in stdout.Split('\n')) {
			string trimmed = line.Trim().TrimEnd(',');
			if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
				timestamps.Add(t);
		}
		return timestamps.ToArray();
	}

	// Begin-region: keep [cutPointSeconds, EOF). -ss placed AFTER -i (output seeking) — verified
	// empirically to snap FORWARD to the next keyframe, never backward into the bumper.
	static void RunFfmpegOutputSeekCopy(string sourcePath, double cutPointSeconds, string outputPath, CancellationToken ct) {
		var psi = StartFfmpegCopyArgs(sourcePath);
		psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		AddFfmpegCopyTail(psi, outputPath);
		RunFfmpeg(psi, sourcePath, ct);
	}

	// End-region: keep [0, cutPointSeconds). No seek needed — the kept segment always starts at
	// the file's own beginning.
	static void RunFfmpegDurationCopy(string sourcePath, double cutPointSeconds, string outputPath, CancellationToken ct) {
		var psi = StartFfmpegCopyArgs(sourcePath);
		psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		AddFfmpegCopyTail(psi, outputPath);
		RunFfmpeg(psi, sourcePath, ct);
	}

	// Common head (-i and everything before it); caller adds -ss/-t (which must come right after
	// -i for output-seeking semantics), then AddFfmpegCopyTail appends -map/-c copy/output.
	static ProcessStartInfo StartFfmpegCopyArgs(string sourcePath) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
		return psi;
	}

	static void AddFfmpegCopyTail(ProcessStartInfo psi, string outputPath) {
		// -map 0 keeps every stream (video, all audio tracks, all subtitle tracks) — subtitles
		// are first-class per AGENTS.md, and ffmpeg's default stream selection can silently drop
		// secondary tracks.
		psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
		psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
		psi.ArgumentList.Add(outputPath);
	}

	static void RunFfmpeg(ProcessStartInfo psi, string sourcePath, CancellationToken ct) {
		using var process = new Process { StartInfo = psi };
		process.Start();
		string stderr = process.StandardError.ReadToEnd();
		process.StandardOutput.ReadToEnd();
		bool exited = process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
		if (!exited || ct.IsCancellationRequested) {
			try { process.Kill(); } catch { }
			if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
			throw new InvalidOperationException($"ffmpeg timed out removing the bumper from '{Path.GetFileName(sourcePath)}'.");
		}
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"ffmpeg failed (exit {process.ExitCode}) removing the bumper from '{Path.GetFileName(sourcePath)}': {stderr.Trim()}");
	}
}
