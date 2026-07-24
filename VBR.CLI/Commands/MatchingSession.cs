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

using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Matching;
using VDF.Core.AI;

namespace VBR.CLI.Commands;

/// <summary>One candidate's outcome across whichever signal(s) <c>--detection-mode</c> selected.
/// <see cref="Present"/> mirrors the decision rule <c>match</c>/<c>remove</c> have always used —
/// whichever signal actually ran wins, in priority order — just extended by one more link for
/// pHash: visual (if it ran) decides; otherwise audio; otherwise pHash.</summary>
internal readonly record struct SignalResult(MatchResult? Visual, MatchResult? Audio, MatchResult? PHash) {
	internal bool Present => Visual?.Present ?? Audio?.Present ?? PHash?.Present ?? false;
}

/// <summary>
/// Owns everything <c>match</c>/<c>remove</c> share: constructing whichever matcher(s)
/// <c>--detection-mode</c> selects, sampling/preparing the reference clip once, and comparing each
/// candidate against it. Extracted so the 3-signal / mixed-density orchestration isn't duplicated
/// between <c>MatchCommand</c> and <c>RemoveCommand</c> (which otherwise reuse it verbatim, per
/// ADR 0007) — each command still owns its own row type, report writing, and (for remove) the
/// removal step itself, which genuinely differ per command.
///
/// Visual/pHash go through <see cref="MixedDensitySampler"/> — direct seek+decode from the source,
/// no <see cref="ClipExtractor"/> involved (see that class's doc comment for why: a chained
/// stream-copy extraction was a real corruption vector on real media). Audio is untouched: it
/// still needs an actual extracted reference-clip file (Chromaprint fingerprints a file, not a
/// frame stream), so <see cref="ClipExtractor.ExtractToTemp"/> still runs, but only when audio is
/// actually requested.
/// </summary>
internal sealed class MatchingSession : IDisposable {
	readonly DetectionMode mode;
	readonly ClipEdge region;
	readonly EdgeDensityProfile profile;
	readonly float presenceThreshold;
	readonly float phashPresenceThreshold;
	readonly string? dumpFramesDir;

	MixedDensitySampler? sampler;
	VisualBumperMatcher? visualForPresence; // carries presenceThreshold for MatchMixedDensity; never opens an ONNX session on its own
	AudioBumperMatcher? audio;
	ExtractedClip? audioReferenceClip;

	IReadOnlyList<TimedFrame>? clipEmbeddings;
	IReadOnlyList<TimedPHash>? clipHashes;

	bool WantsVisual => mode is DetectionMode.visual or DetectionMode.both or DetectionMode.all;
	bool WantsAudio => mode is DetectionMode.audio or DetectionMode.both or DetectionMode.all;
	bool WantsPHash => mode is DetectionMode.phash or DetectionMode.all;

	MatchingSession(DetectionMode mode, ClipEdge region, EdgeDensityProfile profile,
			float presenceThreshold, float phashPresenceThreshold, string? dumpFramesDir) {
		this.mode = mode;
		this.region = region;
		this.profile = profile;
		this.presenceThreshold = presenceThreshold;
		this.phashPresenceThreshold = phashPresenceThreshold;
		this.dumpFramesDir = dumpFramesDir;
	}

	/// <summary>
	/// Constructs whichever matcher(s) <paramref name="mode"/> needs and prepares the reference
	/// clip. Downloads the ONNX model only when visual actually runs — <c>--detection-mode phash</c>
	/// never touches it, the whole point of offering pHash as a lightweight alternate mode. Returns
	/// an error string (never throws) on any failure a caller should print and exit nonzero for —
	/// same failure messages as before this refactor (clip produced no usable frames, extraction
	/// failed, etc.).
	/// </summary>
	internal static async Task<(MatchingSession? session, string? error)> PrepareAsync(
			DetectionMode mode, FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, EdgeDensityProfile profile,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			string? dumpFramesDir, bool verbose, CancellationToken ct) {
		var session = new MatchingSession(mode, region, profile, presenceThreshold, phashPresenceThreshold, dumpFramesDir);
		try {
			if (session.WantsVisual || session.WantsPHash) {
				session.sampler = new MixedDensitySampler(verbose);
				session.visualForPresence = new VisualBumperMatcher(presenceThreshold: presenceThreshold);

				if (session.WantsVisual && !AiComponents.IsReady) {
					Console.Error.WriteLine("AI matching components not found — downloading (one-time, ~100MB)...");
					await AiComponents.DownloadAsync(progress: null, ct);
					Console.Error.WriteLine("AI components ready.");
				}

				int usableCount;
				if (session.WantsVisual) {
					MixedDensitySampler.SampleResult clipSample = session.sampler.SampleWithPHash(
						clipFrom.FullName, region, clipLength, profile, dumpFramesDir, "clip", ct);
					session.clipEmbeddings = clipSample.Embeddings;
					session.clipHashes = clipSample.PHashes;
					usableCount = clipSample.Embeddings.Count;
				}
				else {
					session.clipHashes = session.sampler.SamplePHash(clipFrom.FullName, region, clipLength, profile, dumpFramesDir, "clip", ct);
					usableCount = session.clipHashes.Count;
				}
				if (usableCount < 1) {
					session.Dispose();
					return (null, "Error: The reference clip produced no usable frames after low-information filtering — " +
						"every sampled frame is black, blank/uniform, or a duplicate. Adjust --clip-length/--region/--edge-boundary " +
						"so the clip contains distinctive content, or pick a different --clip-from.");
				}
			}

			if (session.WantsAudio) {
				session.audio = new AudioBumperMatcher(minSimilarity, verboseLogging: verbose);
				try {
					session.audioReferenceClip = ClipExtractor.ExtractToTemp(clipFrom.FullName, ClipRegion.For(region, clipLength), verbose, ct);
				}
				catch (Exception ex) when (ex is FileNotFoundException or ArgumentOutOfRangeException or InvalidOperationException) {
					session.Dispose();
					return (null, $"Error: {ex.Message}");
				}
			}

			return (session, null);
		}
		catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentOutOfRangeException) {
			session.Dispose();
			return (null, $"Error: {ex.Message}");
		}
	}

	/// <summary>Compares one candidate against the prepared reference across whichever signal(s)
	/// are active. <paramref name="searchLength"/> is the candidate-side total window (VBR's
	/// existing <c>--search-length</c>, typically larger than <c>--clip-length</c> for slack);
	/// <paramref name="dumpLabel"/> identifies this candidate's frames under <c>--dump-frames</c>
	/// (ignored when that's off).</summary>
	internal SignalResult Compare(string candidatePath, TimeSpan searchLength, string dumpLabel, CancellationToken ct) {
		MatchResult? visualResult = null, phashResult = null, audioResult = null;

		if (WantsVisual) {
			MixedDensitySampler.SampleResult candidateSample = sampler!.SampleWithPHash(
				candidatePath, region, searchLength, profile, dumpFramesDir, dumpLabel, ct);
			visualResult = visualForPresence!.MatchMixedDensity(clipEmbeddings!, candidateSample.Embeddings);
			if (WantsPHash)
				phashResult = VisualBumperMatcher.MatchMixedDensityPHash(clipHashes!, candidateSample.PHashes, phashPresenceThreshold);
		}
		else if (WantsPHash) {
			IReadOnlyList<TimedPHash> candidateHashes = sampler!.SamplePHash(candidatePath, region, searchLength, profile, dumpFramesDir, dumpLabel, ct);
			phashResult = VisualBumperMatcher.MatchMixedDensityPHash(clipHashes!, candidateHashes, phashPresenceThreshold);
		}

		if (WantsAudio)
			audioResult = audio!.Match(audioReferenceClip!.Value.Path, candidatePath, ClipRegion.For(region, searchLength), ct);

		return new SignalResult(visualResult, audioResult, phashResult);
	}

	public void Dispose() {
		sampler?.Dispose();
		visualForPresence?.Dispose();
		audioReferenceClip?.Dispose();
	}
}
