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
using System.Threading.Tasks;
using VBR.Core.Extraction;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Removal;

/// <summary>Which removal mechanism produced an output. See ADR 0007
/// (docs/decisions/0007-removal-command.md): <see cref="ReEncode"/> is the decided default
/// (frame-accurate; correctly rebases every stream's timestamps, subtitles included, to the new
/// timeline) but is far slower — it decodes and re-encodes the entire kept portion of the file,
/// not just the trimmed region. <see cref="StreamCopy"/> is fast (no decode/encode at all) but
/// keyframe-bound and, per the maintainer's own prior investigation, unreliable at realigning
/// subtitle cues for begin-region cuts — kept available as an explicit opt-out
/// (<c>--re-encode false</c>) for fast iteration.</summary>
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

/// <summary>One periodic progress update from an in-flight ffmpeg cut (docs/iterativeplan.md,
/// "CLI feedback during remove" entry) — <see cref="Processed"/>/<see cref="Total"/> are both
/// measured against the *kept* portion being written, not the source file's own full duration
/// (for an end-region cut that's the same thing; for a begin-region cut it's the shorter
/// post-bumper remainder). <see cref="SpeedMultiplier"/> is ffmpeg's own reported encode speed
/// relative to realtime (null if ffmpeg didn't report one yet, e.g. the very first update).</summary>
public readonly record struct RemovalProgress(TimeSpan Processed, TimeSpan Total, double? SpeedMultiplier);

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

	/// <param name="verbose">Logs duration probing, the computed cut point and its rationale,
	/// the exact ffmpeg command run, and the manifest write, via <see cref="Logger"/> — for
	/// <c>--verbose</c>.</param>
	/// <param name="onProgress">Invoked periodically (from a background read of ffmpeg's own
	/// <c>-progress</c> stream, not this thread) while the cut runs — the CLI's only way to show
	/// the user something is actually happening during a potentially multi-minute re-encode
	/// (docs/iterativeplan.md, "CLI feedback during remove" entry). Never invoked for a
	/// stream-copy cut's near-instant duration isn't a concern, but the plumbing is uniform across
	/// both modes rather than a re-encode-only special case.</param>
	/// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> does not exist.</exception>
	/// <exception cref="InvalidOperationException">Duration probing or the ffmpeg cut failed, or
	/// (stream-copy only) no safe keyframe could be found before the computed end-region cut
	/// point.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="bumperLength"/> is not
	/// positive, or is not shorter than the source file's duration.</exception>
	public static RemovalResult Remove(string sourcePath, ClipEdge region, TimeSpan bumperLength,
			RemovalMode mode, string? matchDetail = null, bool verbose = false,
			Action<RemovalProgress>? onProgress = null, CancellationToken ct = default) {
		if (bumperLength <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(bumperLength), "Bumper length must be positive.");
		if (!File.Exists(sourcePath))
			throw new FileNotFoundException("Source video not found.", sourcePath);

		(TimeSpan sourceDuration, string? sourceCodecName, string? sourcePixelFormat, string? sourceHdrFormat) = ProbeSourceInfo(sourcePath, verbose);
		if (verbose)
			Logger.Instance.Info($"[remove] '{Path.GetFileName(sourcePath)}': duration={sourceDuration.TotalSeconds:0.###}s, region={region}, bumperLength={bumperLength.TotalSeconds:0.###}s, mode={mode}, codec={sourceCodecName ?? "unknown"}.");
		if (bumperLength >= sourceDuration)
			throw new ArgumentOutOfRangeException(nameof(bumperLength),
				$"Bumper length ({bumperLength.TotalSeconds:0.###}s) must be shorter than the " +
				$"source file's duration ({sourceDuration.TotalSeconds:0.###}s).");

		string outputPath = BuildOutputPath(sourcePath);
		double cutPointSeconds;
		if (mode == RemovalMode.StreamCopy) {
			if (region == ClipEdge.begin) {
				cutPointSeconds = bumperLength.TotalSeconds;
				RunFfmpegOutputSeekCopy(sourcePath, cutPointSeconds, outputPath, TimeSpan.FromSeconds(sourceDuration.TotalSeconds - cutPointSeconds), onProgress, verbose, ct);
			}
			else {
				double naiveCutPoint = sourceDuration.TotalSeconds - bumperLength.TotalSeconds;
				cutPointSeconds = FindSafeEndCutPoint(sourcePath, naiveCutPoint, ct);
				if (verbose)
					Logger.Instance.Info($"[remove] End-cut safety margin: arithmetic point {naiveCutPoint:0.###}s -> nearest safe keyframe {cutPointSeconds:0.###}s ({naiveCutPoint - cutPointSeconds:0.###}s extra trimmed).");
				RunFfmpegDurationCopy(sourcePath, cutPointSeconds, outputPath, TimeSpan.FromSeconds(cutPointSeconds), onProgress, verbose, ct);
			}
		}
		else {
			// Only the re-encode path can lose HDR color metadata (stream-copy never touches
			// stream contents at all) -- see docs/decisions/0013-gpu-acceleration.md: bit depth is
			// matched, but full color-metadata passthrough is deferred, so warn rather than
			// silently produce a technically-10-bit-but-wrong-looking (or SDR-flattened) output.
			if (!string.IsNullOrEmpty(sourceHdrFormat))
				Console.Error.WriteLine($"Warning: '{Path.GetFileName(sourcePath)}' source is {sourceHdrFormat} -- " +
					"re-encoding preserves bit depth but not HDR color metadata yet (docs/decisions/0013-gpu-acceleration.md). " +
					"The output may not display correctly as HDR.");
			VideoEncoderChoice encoder = SelectVideoEncoder(sourceCodecName, sourcePixelFormat, verbose, ct);
			// Unconditional (not gated on --verbose), unlike decode's HardwareAcceleration.ReportDecodeRequest()
			// note -- this one IS a real, code-certain confirmation, not a hopeful request: GpuEncoderProbe
			// already probed a real synthetic encode before this choice was made (docs/iterativeplan.md,
			// 2026-08-05 entry, "Issue 1" -- the encode side is the one layer where "requested" and
			// "actually engaging" are known to be the same thing).
			Console.Error.WriteLine($"    Re-encode codec: {encoder.Encoder} ({(encoder.IsGpu ? "GPU" : "CPU")}).");
			if (verbose)
				Logger.Instance.Info($"[remove] Re-encode video codec: {encoder.Encoder}{(encoder.IsGpu ? " (GPU)" : " (CPU)")}.");

			// Re-encoding removes the keyframe constraint entirely — no safety margin needed;
			// the cut point is the exact arithmetic value.
			if (region == ClipEdge.begin) {
				cutPointSeconds = bumperLength.TotalSeconds;
				RunFfmpegOutputSeekReEncode(sourcePath, cutPointSeconds, outputPath, TimeSpan.FromSeconds(sourceDuration.TotalSeconds - cutPointSeconds), encoder, onProgress, verbose, ct);
			}
			else {
				cutPointSeconds = sourceDuration.TotalSeconds - bumperLength.TotalSeconds;
				RunFfmpegDurationReEncode(sourcePath, cutPointSeconds, outputPath, TimeSpan.FromSeconds(cutPointSeconds), encoder, onProgress, verbose, ct);
			}
		}

		// Named after the ORIGINAL, not the .vbr. output (decided, maintainer, 2026-07-20) —
		// keeps the manifest sorted next to the file it describes, and means it never has to be
		// specifically excluded from anything that pattern-matches on the .vbr. convention.
		string manifestPath = Path.ChangeExtension(sourcePath, ".json");
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
		if (verbose)
			Logger.Instance.Info($"[remove] Wrote output '{outputPath}' and manifest '{manifestPath}'.");

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

	/// <summary>Duration plus the video stream's codec/pixel-format/HDR info in one ffprobe call —
	/// all of it already parsed by <see cref="FFProbeEngine"/>/<c>MediaInfo</c>, no new ffprobe
	/// parsing needed (docs/decisions/0013-gpu-acceleration.md). <c>CodecName</c>/<c>PixelFormat</c>/
	/// <c>HdrFormat</c> are null when no video stream was found at all (never expected in
	/// practice, but not fatal — <see cref="SelectVideoEncoder"/> falls back to the universal CPU
	/// default when the codec name is unknown).</summary>
	static (TimeSpan Duration, string? CodecName, string? PixelFormat, string? HdrFormat) ProbeSourceInfo(string path, bool verbose) {
		var info = FFProbeEngine.GetMediaInfo(path, extendedLogging: verbose);
		if (info is null || info.Duration <= TimeSpan.Zero)
			throw new InvalidOperationException($"Could not determine duration for '{Path.GetFileName(path)}' (ffprobe failed or reported no duration).");
		var video = info.Streams.FirstOrDefault(s => s.CodecType == "video");
		return (info.Duration, video?.CodecName, video?.PixelFormat, video?.HdrFormat);
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
	static void RunFfmpegOutputSeekCopy(string sourcePath, double cutPointSeconds, string outputPath,
			TimeSpan totalKeptDuration, Action<RemovalProgress>? onProgress, bool verbose, CancellationToken ct) {
		var psi = StartFfmpegArgs(sourcePath);
		psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		AddFfmpegCopyTail(psi, outputPath);
		RunFfmpeg(psi, sourcePath, StreamCopyTimeout, totalKeptDuration, onProgress, verbose, ct);
	}

	// End-region: keep [0, cutPointSeconds). No seek needed — the kept segment always starts at
	// the file's own beginning.
	static void RunFfmpegDurationCopy(string sourcePath, double cutPointSeconds, string outputPath,
			TimeSpan totalKeptDuration, Action<RemovalProgress>? onProgress, bool verbose, CancellationToken ct) {
		var psi = StartFfmpegArgs(sourcePath);
		psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		AddFfmpegCopyTail(psi, outputPath);
		RunFfmpeg(psi, sourcePath, StreamCopyTimeout, totalKeptDuration, onProgress, verbose, ct);
	}

	/// <summary>One chosen video encoder for a re-encode cut, per
	/// docs/decisions/0012-removal-reencode-defaults.md (CPU codec matching) and
	/// docs/decisions/0013-gpu-acceleration.md (the GPU tier on top). <see cref="QualityArgs"/> is
	/// whatever quality-control flags that specific encoder wants (<c>-crf N -preset P</c> for a
	/// CPU x264/x265/vp9 encoder; <c>-cq N</c>/<c>-global_quality N</c>/<c>-qp_i N -qp_p N</c> for
	/// NVENC/QSV/AMF respectively) — deliberately opaque to the caller, which just appends them
	/// verbatim after <c>-c:v</c>.</summary>
	internal readonly record struct VideoEncoderChoice(string Encoder, IReadOnlyList<string> QualityArgs, bool Is10Bit, bool IsGpu);

	const string ReEncodePreset = "medium"; // CPU x264/x265 preset -- ADR 0012 leans "slow" but leaves this specific value not yet finalized
	const string ReEncodeAudioCodec = "aac";
	const string ReEncodeAudioBitrate = "192k";

	/// <summary>
	/// Picks the video encoder for a re-encode cut. GPU-first when hardware acceleration is
	/// enabled and the source is H.264/HEVC (the only two families <see cref="GpuEncoderProbe"/>
	/// covers — VP9/AV1 stay CPU-only/deferred, per docs/decisions/0013-gpu-acceleration.md);
	/// falls back to the CPU codec-matched table (docs/decisions/0012-removal-reencode-defaults.md)
	/// otherwise, or when no GPU encoder actually probes as working. An unrecognized/unknown source
	/// codec falls all the way through to the universal <c>libx264</c> fallback row, matching this
	/// project's existing behavior for anything it doesn't specifically recognize.
	/// </summary>
	internal static VideoEncoderChoice SelectVideoEncoder(string? sourceCodecName, string? sourcePixelFormat, bool verbose, CancellationToken ct) {
		bool is10Bit = sourcePixelFormat is not null &&
			(sourcePixelFormat.Contains("10le", StringComparison.OrdinalIgnoreCase) || sourcePixelFormat.Contains("10be", StringComparison.OrdinalIgnoreCase));
		string family = sourceCodecName switch {
			"h264" => "h264",
			"hevc" or "h265" => "hevc",
			_ => "other",
		};

		if (HardwareAcceleration.Enabled && family is "h264" or "hevc") {
			string? gpu = GpuEncoderProbe.TryGetEncoder(family, verbose, ct);
			if (gpu is not null) {
				// GPU quality knobs aren't CRF -- roughly mirrored at the same numeric target as
				// the adjacent CPU row below (not a verified equivalence, a reasonable starting
				// point -- see docs/decisions/0013-gpu-acceleration.md).
				string quality = family == "h264" ? "22" : "24";
				IReadOnlyList<string> qualityArgs = gpu switch {
					"h264_nvenc" or "hevc_nvenc" => new[] { "-cq", quality, "-preset", "p5" },
					"h264_qsv" or "hevc_qsv" => new[] { "-global_quality", quality, "-preset", "slow" },
					"h264_amf" or "hevc_amf" => new[] { "-qp_i", quality, "-qp_p", quality, "-quality", "quality" },
					_ => new[] { "-cq", quality },
				};
				return new VideoEncoderChoice(gpu, qualityArgs, is10Bit, IsGpu: true);
			}
		}

		return sourceCodecName switch {
			"h264" => new VideoEncoderChoice("libx264", new[] { "-crf", "22", "-preset", ReEncodePreset }, is10Bit, IsGpu: false),
			"hevc" or "h265" => new VideoEncoderChoice("libx265", new[] { "-crf", "24", "-preset", ReEncodePreset }, is10Bit, IsGpu: false),
			"vp9" => new VideoEncoderChoice("libvpx-vp9", new[] { "-crf", "31", "-b:v", "0" }, is10Bit, IsGpu: false),
			// Universal fallback -- matches today's existing default, so an old/unrecognized source doesn't break.
			_ => new VideoEncoderChoice("libx264", new[] { "-crf", "22", "-preset", ReEncodePreset }, is10Bit, IsGpu: false),
		};
	}

	// -ss placed BEFORE -i ("input seeking"): fast (seeks near the target via the container
	// index) and, combined with an actual re-encode (not stream copy), frame-accurate — ffmpeg
	// decodes forward from the nearest preceding keyframe and starts the OUTPUT exactly at the
	// requested frame, discarding what's in between. No safety margin needed, unlike Mode A.
	//
	// Audio is RE-ENCODED here, not copied — verified empirically (2026-07-19/20, synthetic
	// subtitle test) that `-ss` input-seeking combined with `-c:a copy` and no fixed upper bound
	// desyncs the muxer's end-of-stream detection: the output ran ~2s LONGER than requested
	// regardless of whether an explicit `-t` was also given, isolated by testing every
	// copy/re-encode combination of video/audio/subtitles against the same seek. Re-encoding
	// audio (this method only — end-region cuts never seek and audio-copy was verified correct
	// there) resolved it, and as a side effect confirmed subtitle cues shift correctly through
	// the transcode pipeline once the muxer stops running long: a synthetic SRT with cues at
	// 6s/15s/25s, cut with a 5s begin-region bumper length, came out at exactly 1s/10s/20s.
	static void RunFfmpegOutputSeekReEncode(string sourcePath, double cutPointSeconds, string outputPath,
			TimeSpan totalKeptDuration, VideoEncoderChoice encoder, Action<RemovalProgress>? onProgress, bool verbose, CancellationToken ct) {
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
		psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
		if (HardwareAcceleration.Enabled) {
			psi.ArgumentList.Add("-hwaccel");
			psi.ArgumentList.Add(HardwareAcceleration.Mode.ToString());
		}
		psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
		AddFfmpegReEncodeTail(psi, outputPath, copyAudio: false, encoder);
		RunFfmpeg(psi, sourcePath, ReEncodeTimeout, totalKeptDuration, onProgress, verbose, ct);
	}

	// End-region: keep [0, cutPointSeconds). No seek needed — no seek, no bug (verified: audio
	// copy alongside a re-encoded, -t-bounded video stream landed within 28ms of the exact
	// requested cut point). Re-encoding removes the ~1s+ safety margin Mode A pays for GOP
	// safety, at zero audio quality cost since copy is safe here.
	static void RunFfmpegDurationReEncode(string sourcePath, double cutPointSeconds, string outputPath,
			TimeSpan totalKeptDuration, VideoEncoderChoice encoder, Action<RemovalProgress>? onProgress, bool verbose, CancellationToken ct) {
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
		psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
		if (HardwareAcceleration.Enabled) {
			psi.ArgumentList.Add("-hwaccel");
			psi.ArgumentList.Add(HardwareAcceleration.Mode.ToString());
		}
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
		psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(cutPointSeconds.ToString(CultureInfo.InvariantCulture));
		AddFfmpegReEncodeTail(psi, outputPath, copyAudio: true, encoder);
		RunFfmpeg(psi, sourcePath, ReEncodeTimeout, totalKeptDuration, onProgress, verbose, ct);
	}

	static void AddFfmpegReEncodeTail(ProcessStartInfo psi, string outputPath, bool copyAudio, VideoEncoderChoice encoder) {
		psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
		psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoder.Encoder);
		foreach (string arg in encoder.QualityArgs)
			psi.ArgumentList.Add(arg);
		if (encoder.Is10Bit) {
			psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p10le");
		}
		if (copyAudio) {
			psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("copy");
		}
		else {
			psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add(ReEncodeAudioCodec);
			psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add(ReEncodeAudioBitrate);
		}
		// Subtitles are always stream-copied — verified unaffected by the seek+copy bug above
		// (isolated by testing video+subtitle-copy alone, with no audio stream mapped at all).
		psi.ArgumentList.Add("-c:s"); psi.ArgumentList.Add("copy");
		psi.ArgumentList.Add(outputPath);
	}

	// Common head (-i and everything before it) for the stream-copy paths only; caller adds
	// -ss/-t (which must come right after -i for output-seeking semantics), then
	// AddFfmpegCopyTail appends -map/-c copy/output. The re-encode paths build their own
	// ProcessStartInfo directly since -ss placement differs (before -i, not after).
	static ProcessStartInfo StartFfmpegArgs(string sourcePath) {
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
		psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
		if (HardwareAcceleration.Enabled) {
			// A no-op with the stream-copy tail these two callers append (-c copy never decodes),
			// but harmless to include unconditionally -- matches VDF's own FfmpegEngine convention
			// and means switching either caller to a re-encode path later needs no new wiring here.
			psi.ArgumentList.Add("-hwaccel");
			psi.ArgumentList.Add(HardwareAcceleration.Mode.ToString());
		}
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

	static readonly TimeSpan StreamCopyTimeout = TimeSpan.FromMinutes(5);
	// Re-encoding is decode+encode over the ENTIRE kept portion of the file — for an end-region
	// cut that's essentially the whole episode, not just the trimmed few seconds. Even with a GPU
	// encoder/hwaccel decode (docs/decisions/0013-gpu-acceleration.md) this stays a generous
	// ceiling against a truly wedged process, not a real expectation of how long a legitimate
	// encode should take -- CPU x264 at a "medium" preset (still the fallback whenever no GPU
	// encoder is available) is genuinely slow on a long file.
	static readonly TimeSpan ReEncodeTimeout = TimeSpan.FromHours(4);

	static void RunFfmpeg(ProcessStartInfo psi, string sourcePath, TimeSpan timeout,
			TimeSpan totalKeptDuration, Action<RemovalProgress>? onProgress, bool verbose, CancellationToken ct) {
		if (verbose)
			Logger.Instance.Info($"[remove] {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
		var stopwatch = verbose ? Stopwatch.StartNew() : null;
		using var process = new Process { StartInfo = psi };
		process.Start();

		// Killing on cancellation must not depend on this thread ever reaching a check below —
		// it's about to block in ReadToEnd()/WaitForExit(), neither of which return early on
		// their own. A CancellationTokenRegistration callback fires independently of whatever
		// this thread is doing, so Ctrl+C actually reaches ffmpeg instead of leaving it running
		// as an orphan. Observed live (2026-07-20): killing `vbr remove` mid-encode left ffmpeg
		// running in the background indefinitely, still fully using a CPU core.
		using CancellationTokenRegistration killOnCancel = ct.Register(static s => {
			var p = (Process)s!;
			try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
		}, process);

		// Both streams must be drained concurrently, not sequentially: ffmpeg's progress/log
		// output goes to stderr, which is large enough to fill the OS pipe buffer — reading one
		// stream to completion first, while nothing drains the other, risks deadlocking ffmpeg
		// against this process (same bug class hit and fixed in VBR.Tests's synthetic-clip
		// helper, 2026-07-20). stdout carries structured `-progress pipe:1` key=value lines (every
		// caller passes `-progress pipe:1` -- see StartFfmpegArgs/the two re-encode builders) —
		// read line-by-line as they arrive, not ReadToEndAsync, so onProgress fires while ffmpeg is
		// still running rather than only once the whole cut is already done (docs/iterativeplan.md,
		// "CLI feedback during remove" entry: a multi-minute re-encode with zero console output in
		// the meantime read as "hung," not "working").
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		Task stdoutTask = ReadProgressAsync(process.StandardOutput, totalKeptDuration, onProgress, ct);
		bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
		if (!exited || ct.IsCancellationRequested) {
			try { process.Kill(entireProcessTree: true); } catch { }
			if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
			throw new InvalidOperationException($"ffmpeg timed out removing the bumper from '{Path.GetFileName(sourcePath)}'.");
		}
		Task.WaitAll(stderrTask, stdoutTask);
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"ffmpeg failed (exit {process.ExitCode}) removing the bumper from '{Path.GetFileName(sourcePath)}': {stderrTask.Result.Trim()}");
		if (verbose)
			Logger.Instance.Info($"[remove] ffmpeg completed in {stopwatch!.Elapsed.TotalSeconds:0.#}s.");
	}

	/// <summary>
	/// Parses ffmpeg's <c>-progress pipe:1</c> output (a repeating block of <c>key=value</c> lines,
	/// each block terminated by <c>progress=continue</c> or <c>progress=end</c>) and invokes
	/// <paramref name="onProgress"/> once per block with the accumulated <c>out_time</c> and, when
	/// ffmpeg reported one, the current <c>speed</c> multiplier. Reads via <c>out_time=</c> (an
	/// <c>HH:MM:SS.ffffff</c> string) rather than the confusingly-named <c>out_time_ms</c> key
	/// (actually microseconds, a long-standing ffmpeg naming quirk) — parsing the human string
	/// sidesteps that unit ambiguity entirely. Silently no-ops (still drains the stream, so the
	/// process can't deadlock on a full pipe buffer) when <paramref name="onProgress"/> is null.
	/// </summary>
	static async Task ReadProgressAsync(StreamReader stdout, TimeSpan total, Action<RemovalProgress>? onProgress, CancellationToken ct) {
		TimeSpan lastOutTime = TimeSpan.Zero;
		double? lastSpeed = null;
		string? line;
		while ((line = await stdout.ReadLineAsync(ct).ConfigureAwait(false)) is not null) {
			int eq = line.IndexOf('=');
			if (eq < 0) continue;
			string key = line[..eq];
			string value = line[(eq + 1)..];
			switch (key) {
				case "out_time":
					// A brief "N/A" is possible right at startup, before the first real timestamp.
					if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan t))
						lastOutTime = t;
					break;
				case "speed":
					string trimmed = value.TrimEnd('x');
					lastSpeed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : null;
					break;
				case "progress":
					onProgress?.Invoke(new RemovalProgress(lastOutTime, total, lastSpeed));
					break;
			}
		}
	}
}
