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
using System.Threading;
using System.Threading.Tasks;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VDF.Core;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Catalog;

/// <summary>
/// Builds one <see cref="BumperCatalogEntry"/> from a source video + region + length — the
/// <c>vbr add-bumper</c> workflow (docs/iterativeplan.md, "Bumper catalog"). Does not touch the
/// catalog file itself (load/insert/save is the caller's job, same division as
/// <c>VBR.Core.Removal.ClipRemover</c> not knowing about any index/catalog) — this class only knows
/// how to turn one clip request into one fully-populated entry.
/// </summary>
public static class BumperCatalogBuilder {
	// A bumper clip is always short (seconds to under a minute) -- the same dense interval
	// match/remove default to for short clips. Used whenever AddBumper's own sampleInterval
	// parameter is left at its TimeSpan.Zero "unset" sentinel (docs/iterativeplan.md, 2026-08-09 --
	// add-bumper now has its own --sample-interval, matching scan/match/remove; previously this was
	// the only value it could ever use at all).
	static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(0.2);

	/// <param name="clipsFolder">Where the extracted reference clip is written
	/// (<c>{clipsFolder}/{id}.mkv</c>) — the catalog's own <c>clips/</c> subfolder, created if
	/// missing.</param>
	/// <param name="sampleInterval">Seconds between sampled frames (the same dense interval
	/// <c>scan</c>/<c>match</c>/<c>remove</c> expose as <c>--sample-interval</c>). <c>TimeSpan.Zero</c>
	/// (the default) means "unset" — falls back to <see cref="DefaultSampleInterval"/> (0.2s), same
	/// as this method's own behavior before it had a tunable interval at all.</param>
	/// <param name="dumpFramesDir">Diagnostic: when set, every frame sampled from the reference
	/// region is written as a PNG under <c>{dumpFramesDir}/clip/</c> via <see cref="FrameDump"/> —
	/// same convention and dump label ("clip") <c>match</c>/<c>remove</c> already use for their own
	/// reference-clip sampling. Written pre-filter, so the dump shows the unfiltered truth, not
	/// just what survived low-information filtering. Null (the default) dumps nothing.</param>
	/// <param name="verboseLogging">Logs duration probing, sampled/usable frame counts, and the
	/// exact ffmpeg commands run, via <see cref="Logger"/> — same convention as every other
	/// sampling/extraction path in this project.</param>
	/// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> does not exist.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="clipLength"/> is not positive,
	/// or is not shorter than the source file's duration.</exception>
	/// <exception cref="InvalidOperationException">Duration probing failed, or every sampled frame
	/// in the requested region was filtered out as low-information (black/blank/duplicate) — the
	/// requested region doesn't contain real bumper content to fingerprint.</exception>
	public static BumperCatalogEntry AddBumper(
			string sourcePath, ClipEdge region, TimeSpan clipLength, string label, string? description,
			string[] tags, string clipsFolder, TimeSpan sampleInterval = default, string? dumpFramesDir = null,
			bool verboseLogging = false, CancellationToken ct = default) {
		if (!File.Exists(sourcePath))
			throw new FileNotFoundException("Source video not found.", sourcePath);
		if (clipLength <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(clipLength), "Clip length must be positive.");

		// TimeSpan.MaxValue as the edge boundary is the same "always dense, no sparse zone" idiom
		// match/remove use when --edge-boundary is left unset -- MixedDensitySampler.GatherFrames's
		// own clamp (edgeBoundary > totalLength => totalLength) turns it into "the whole clip is
		// dense" for whatever totalLength (here, always clipLength) a given call uses.
		TimeSpan effectiveInterval = sampleInterval > TimeSpan.Zero ? sampleInterval : DefaultSampleInterval;
		var allDenseProfile = new EdgeDensityProfile(TimeSpan.MaxValue, effectiveInterval, effectiveInterval);

		MediaInfo? info = FFProbeEngine.GetMediaInfo(sourcePath, extendedLogging: verboseLogging);
		if (info is null || info.Duration <= TimeSpan.Zero)
			throw new InvalidOperationException(
				$"Could not determine duration for '{Path.GetFileName(sourcePath)}' (ffprobe failed or reported no duration).");
		TimeSpan sourceDuration = info.Duration;
		if (clipLength >= sourceDuration)
			throw new ArgumentOutOfRangeException(nameof(clipLength),
				$"Clip length ({clipLength.TotalSeconds:0.###}s) must be shorter than the source file's " +
				$"duration ({sourceDuration.TotalSeconds:0.###}s).");

		if (verboseLogging)
			Logger.Instance.Info($"[add-bumper] '{Path.GetFileName(sourcePath)}': region={region}, " +
				$"clipLength={clipLength.TotalSeconds:0.###}s, sourceDuration={sourceDuration.TotalSeconds:0.###}s, " +
				$"sampleInterval={effectiveInterval.TotalSeconds:0.###}s.");

		string id = Guid.NewGuid().ToString("N");

		// Fingerprints are sampled directly from the source -- never from the extracted reference
		// clip below. This is the same lesson MixedDensitySampler's own class doc comment records:
		// chaining a second decode off an already-extracted clip was a real corruption/quality risk
		// on real media, fixed by always seeking straight into the original source.
		TimedFingerprint[] fingerprints;
		using (var sampler = new MixedDensitySampler(verboseLogging)) {
			MixedDensitySampler.SampleResult sampled = sampler.SampleWithPHash(sourcePath, region, clipLength, allDenseProfile, dumpFramesDir, "clip", ct);
			fingerprints = new TimedFingerprint[sampled.Embeddings.Count];
			for (int i = 0; i < fingerprints.Length; i++)
				fingerprints[i] = new TimedFingerprint(
					sampled.Embeddings[i].TimestampSeconds, sampled.Embeddings[i].Embedding, sampled.PHashes[i].PHash);
		}
		if (fingerprints.Length == 0)
			throw new InvalidOperationException(
				$"No usable frames found in '{Path.GetFileName(sourcePath)}''s {region} region " +
				$"({clipLength.TotalSeconds:0.###}s) -- every sampled frame was filtered out as low-information " +
				"(black/blank/duplicate). Check that --region/--clip-length actually target the bumper.");

		// The reference clip is a separate, independent extraction purely for human preview/curation
		// -- it never feeds fingerprinting. Extracted to a temp file by the shared extractor, then
		// moved into the catalog's own clips/ folder.
		Directory.CreateDirectory(clipsFolder);
		string clipDestination = Path.Combine(clipsFolder, id + ".mkv");
		using (ExtractedClip extracted = ClipExtractor.ExtractToTemp(sourcePath, ClipRegion.For(region, clipLength), verboseLogging, ct))
			File.Move(extracted.Path, clipDestination, overwrite: true);

		// The clip's own probed duration is trusted over the requested clipLength verbatim --
		// stream-copy extraction is keyframe-bound, so the actual result can differ slightly, and
		// this is the value future removal arithmetic (ADR 0007) would rely on.
		MediaInfo? clipInfo = FFProbeEngine.GetMediaInfo(clipDestination, extendedLogging: verboseLogging);
		TimeSpan measuredDuration = clipInfo is not null && clipInfo.Duration > TimeSpan.Zero ? clipInfo.Duration : clipLength;

		uint[]? audioFingerprint = ChromaprintEngine.ExtractFingerprint(clipDestination, verboseLogging, ct);

		byte[] thumbnail = Array.Empty<byte>();
		try {
			thumbnail = CaptureThumbnail(sourcePath, region, clipLength, sourceDuration, allDenseProfile, verboseLogging, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException) {
			// Best-effort, never blocks adding the bumper -- see BumperCatalogEntry.Thumbnail's doc
			// comment. A capture failure is still worth recording in log.txt.
			Logger.Instance.Warn($"[add-bumper] Thumbnail capture failed for '{Path.GetFileName(sourcePath)}': {ex.Message} -- continuing without one.");
		}

		return new BumperCatalogEntry {
			Id = id,
			Label = label,
			Description = description,
			Tags = tags,
			Region = region,
			Status = "active",
			Duration = measuredDuration,
			Fingerprints = fingerprints,
			AudioFingerprint = audioFingerprint,
			ReferenceClipPath = Path.Combine("clips", id + ".mkv"),
			Thumbnail = thumbnail,
			SourceVideoPath = Path.GetFullPath(sourcePath),
			DateAdded = DateTime.UtcNow,
			OccurrenceCount = 0,
		};
	}

	/// <summary>Picks the most detailed (least likely black/blank) sampled frame's position via
	/// <see cref="FrameQuality.MeasureDetail"/>, then re-extracts *that* frame from the original
	/// source at its native decoded resolution (the AI-pipeline frames used for scoring are
	/// downscaled to the ONNX model's fixed input size, wrong for a preview thumbnail) — one extra,
	/// cheap single-frame decode, deliberately not reused pixel data.</summary>
	static byte[] CaptureThumbnail(string sourcePath, ClipEdge region, TimeSpan clipLength, TimeSpan sourceDuration,
			EdgeDensityProfile allDenseProfile, bool verboseLogging, CancellationToken ct) {
		List<MixedDensitySampler.SampledFrame> frames =
			MixedDensitySampler.GatherFrames(sourcePath, region, clipLength, allDenseProfile, verboseLogging: verboseLogging, ct: ct);
		if (frames.Count == 0)
			return Array.Empty<byte>();

		MixedDensitySampler.SampledFrame best = frames[0];
		double bestDetail = FrameQuality.MeasureDetail(best.Rgb24);
		for (int i = 1; i < frames.Count; i++) {
			double detail = FrameQuality.MeasureDetail(frames[i].Rgb24);
			if (detail > bestDetail) {
				bestDetail = detail;
				best = frames[i];
			}
		}

		// GatherFrames' timestamps are relative to the requested window, not the source file (see
		// its own doc comment) -- for the all-dense profile used here, that window is [0, clipLength)
		// from BOF (region == begin) or the last clipLength before EOF (region == end).
		double absoluteTimestamp = region == ClipEdge.begin
			? best.TimestampSeconds
			: Math.Max(0, (sourceDuration - clipLength).TotalSeconds + best.TimestampSeconds);

		return ExtractSingleFrameJpeg(sourcePath, absoluteTimestamp, ct);
	}

	static byte[] ExtractSingleFrameJpeg(string sourcePath, double timestampSeconds, CancellationToken ct) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-nostdin");
		psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(timestampSeconds.ToString(CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
		psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
		psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("2");
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
		psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("mjpeg");
		psi.ArgumentList.Add("pipe:1");

		using var process = new Process { StartInfo = psi };
		using var ms = new MemoryStream();
		Task? readTask = null;
		try {
			process.Start();
			var stderr = process.StandardError.ReadToEndAsync();
			readTask = process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
			// A single-frame grab is fast; a minute is generous headroom against a wedged decode.
			if (!readTask.Wait((int)TimeSpan.FromMinutes(1).TotalMilliseconds, ct))
				throw new TimeoutException($"ffmpeg timed out extracting a thumbnail frame from: {sourcePath}");
			if (!process.WaitForExit(30_000))
				throw new TimeoutException($"ffmpeg did not exit after closing its output: {sourcePath}");
			process.WaitForExit();
			if (process.ExitCode != 0 || ms.Length == 0)
				throw new InvalidOperationException(
					$"ffmpeg failed extracting a thumbnail frame (exit {process.ExitCode}): {Tail(stderr.Result)}");
			return ms.ToArray();
		}
		catch (Exception e) when (e is not InvalidOperationException and not OperationCanceledException) {
			KillQuietly(process, readTask);
			throw new InvalidOperationException($"Thumbnail extraction failed for '{sourcePath}': {e.Message}", e);
		}
		catch (OperationCanceledException) {
			KillQuietly(process, readTask);
			throw;
		}

		static string Tail(string stderr) {
			stderr = stderr.Trim();
			return stderr.Length <= 400 ? stderr : "…" + stderr[^400..];
		}
		static void KillQuietly(Process process, Task? readTask) {
			try { if (!process.HasExited) process.Kill(); } catch { }
			try { readTask?.Wait(2000); } catch { }
		}
	}
}
