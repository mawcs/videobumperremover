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

using System.Linq;
using VBR.Core.Catalog;
using VBR.Core.Database;
using VBR.Core.Diagnostics;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Matching;
using VDF.Core.AI;
using VDF.Core.Utils;

namespace VBR.CLI.Commands;

/// <summary>
/// One candidate's outcome across whichever signal(s) <c>--detection-mode</c> selected.
/// <see cref="Present"/> (decided 2026-08-13, docs/iterativeplan.md — the maintainer's own real
/// dogfooding found a bumper whose false positives survived both extreme <c>frameQuality</c> values
/// and a raised <c>presenceThreshold</c>, i.e. visual alone, tuned as hard as a single signal can
/// be, still wasn't enough): when visual ran, it must still agree — same as before — but every
/// *other* signal that also ran and is actually meaningful for this bumper now has to agree too,
/// not just corroborate optionally. "Meaningful" is the load-bearing word for audio specifically —
/// see <see cref="AudioApplicable"/>. pHash needs no such carve-out: it shares the exact same
/// upfront usable-frame gate visual does (both come from one <see cref="Fingerprinting.MixedDensitySampler.GatherFrames"/>
/// call), so if visual could run at all, pHash's clip-side data is equally real. When visual
/// *didn't* run at all (<c>--detection-mode audio|phash</c>), the old priority-fallback behavior is
/// unchanged — whichever single signal ran decides alone.
/// </summary>
/// <param name="AudioApplicable">Whether the *reference* side's own audio is real enough to mean
/// anything (mirrors <see cref="Matching.AudioBumperMatcher.MatchFingerprints"/>'s own "no usable
/// audio fingerprint on the reference clip" check — <see cref="MatchingSession.ReferenceHasUsableAudio"/>).
/// False for a silent/near-silent bumper: <see cref="Audio"/> is still computed and still shown in
/// <see cref="CombinedDetail"/> for transparency, it just never vetoes a match it structurally
/// cannot agree OR disagree with. True for a bumper with real, distinguishing audio: <see cref="Audio"/>
/// becomes as mandatory as pHash. This is what makes audio "handle silent bumpers as well as
/// bumpers with sound" (the maintainer's own framing) — not a smarter fingerprint algorithm, a
/// caller that knows when the algorithm's answer is meaningful.</param>
internal readonly record struct SignalResult(MatchResult? Visual, MatchResult? Audio, MatchResult? PHash, bool AudioApplicable) {
	internal bool Present {
		get {
			if (Visual is not { } visual)
				return Audio?.Present ?? PHash?.Present ?? false;
			bool ok = visual.Present;
			if (PHash is { } phash) ok &= phash.Present;
			if (Audio is { } audio && AudioApplicable) ok &= audio.Present;
			return ok;
		}
	}

	/// <summary>Every computed signal's own detail string, concatenated — same shape/order
	/// <c>MatchRow</c>/<c>RemoveRow.ToLine()</c> already build per-row from these same three fields,
	/// centralized here so single-line call sites (a "Match found" progress message, a removal
	/// manifest's <c>MatchDetail</c>) show every signal that decided the outcome, not just whichever
	/// happened to be first in an old priority chain.</summary>
	internal string? CombinedDetail {
		get {
			var parts = new List<string>(3);
			if (Visual is { } visual) parts.Add($"visual: {visual.Detail}");
			if (Audio is { } audio) parts.Add($"audio: {audio.Detail}");
			if (PHash is { } phash) parts.Add($"phash: {phash.Detail}");
			return parts.Count > 0 ? string.Join("  |  ", parts) : null;
		}
	}
}

/// <summary>
/// Owns everything <c>match</c>/<c>remove</c> share: constructing whichever matcher(s)
/// <c>--detection-mode</c> selects, preparing the reference clip once, and comparing each
/// candidate against it. Extracted so the 3-signal / mixed-density orchestration isn't duplicated
/// between <c>MatchCommand</c> and <c>RemoveCommand</c> (which otherwise reuse it verbatim, per
/// ADR 0007) — each command still owns its own row type, report writing, and (for remove) the
/// removal step itself, which genuinely differ per command.
///
/// Two independent axes, per docs/iterativeplan.md's "Utilizing Databases" entry — the reference
/// side and the candidate side each have two possible sources, freely mixable:
/// <list type="bullet">
/// <item>Reference: an ad hoc <c>--clip-from</c>/<c>--region</c>/<c>--clip-length</c> (sampled
/// fresh here, via <see cref="PrepareAsync"/>) or a <see cref="BumperCatalogEntry"/>'s already-
/// sampled <c>Fingerprints</c>/<c>AudioFingerprint</c> (reused as-is, via
/// <see cref="PrepareFromCatalogEntry"/> — no re-extraction).</item>
/// <item>Candidate: an ad hoc file (sampled fresh per call, via <see cref="Compare"/>) or a
/// <see cref="LibraryDatabaseEntry"/>'s already-sampled <c>Fingerprints</c>/<c>AudioFingerprint</c>
/// (reused as-is, via <see cref="CompareUsingDatabase"/> — no re-scan).</item>
/// </list>
/// Visual/pHash presence matching (<see cref="VisualBumperMatcher.MatchMixedDensity"/>/
/// <see cref="VisualBumperMatcher.MatchMixedDensityPHash"/>) never requires temporal alignment
/// between the two sides, so mixing an absolute-timestamped database entry against a window-
/// relative-timestamped catalog entry (or clip) is exactly as valid as any other pairing — see
/// <see cref="TimedFingerprint"/>'s and <see cref="WholeFileSampler"/>'s doc comments for why the
/// two persisted stores use different timestamp origins in the first place.
///
/// Audio is handled uniformly by extracting/reusing one whole-file Chromaprint fingerprint per
/// side up front (<see cref="referenceAudioFingerprint"/>), then always comparing via
/// <see cref="AudioBumperMatcher.MatchFingerprints"/> — this also fixes a latent inefficiency the
/// four-combo matrix would otherwise have made much worse: the old code re-extracted the
/// reference clip's own fingerprint on every single candidate via <c>AudioBumperMatcher.Match</c>;
/// now it's computed (or reused) exactly once per run, regardless of which combo is active.
///
/// <b>Decision rule (revised 2026-08-13, see <see cref="SignalResult"/>'s own doc comment for the
/// full reasoning):</b> every signal that both ran and is meaningful for this specific bumper must
/// agree, not just whichever ran first. <see cref="ReferenceHasUsableAudio"/> is what keeps this
/// safe for silent bumpers — audio only ever gates when the reference side actually has real audio
/// to compare, never when it's silent/near-silent, so a silent bumper's audio result stays
/// informational-only instead of becoming an automatic veto no candidate could ever pass.
/// </summary>
internal sealed class MatchingSession : IDisposable {
	readonly DetectionMode mode;
	readonly ClipEdge region;
	readonly EdgeDensityProfile profile;
	readonly float presenceThreshold;
	readonly float phashPresenceThreshold;
	readonly float minSimilarity;
	readonly string? dumpFramesDir;
	readonly bool verboseLogging;

	MixedDensitySampler? sampler; // only used for ad hoc (fresh-sampled) candidates -- never touched by CompareUsingDatabase
	VisualBumperMatcher? visualForPresence; // carries presenceThreshold for MatchMixedDensity; never opens an ONNX session on its own
	ExtractedClip? audioReferenceClip; // ad hoc reference only -- disposed with the session; null when the reference came from a catalog entry
	uint[]? referenceAudioFingerprint; // set for both reference sources -- ad hoc (extracted once here) or catalog (reused directly)

	IReadOnlyList<TimedFrame>? clipEmbeddings;
	IReadOnlyList<TimedPHash>? clipHashes;

	bool WantsVisual => mode is DetectionMode.visual or DetectionMode.both or DetectionMode.all;
	bool WantsAudio => mode is DetectionMode.audio or DetectionMode.both or DetectionMode.all;
	bool WantsPHash => mode is DetectionMode.phash or DetectionMode.all;

	/// <summary>Whether the reference clip's own audio carries enough real content for a comparison
	/// against it to mean anything — exactly the predicate <see cref="Matching.AudioBumperMatcher.MatchFingerprints"/>
	/// already uses internally to short-circuit ("no usable audio fingerprint on the reference
	/// clip"), promoted here to a caller-visible fact instead of a silently-always-false result
	/// (docs/iterativeplan.md, 2026-08-13 — see <see cref="SignalResult.AudioApplicable"/>). False
	/// for a silent/near-silent bumper — Chromaprint blocks are ~1s each, so <c>&lt; 2</c> is
	/// "essentially nothing was fingerprinted," not "fingerprinted but different."</summary>
	bool ReferenceHasUsableAudio => referenceAudioFingerprint is { Length: >= 2 };

	MatchingSession(DetectionMode mode, ClipEdge region, EdgeDensityProfile profile,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity, string? dumpFramesDir, bool verboseLogging) {
		this.mode = mode;
		this.region = region;
		this.profile = profile;
		this.presenceThreshold = presenceThreshold;
		this.phashPresenceThreshold = phashPresenceThreshold;
		this.minSimilarity = minSimilarity;
		this.dumpFramesDir = dumpFramesDir;
		this.verboseLogging = verboseLogging;
	}

	/// <summary>
	/// Constructs whichever matcher(s) <paramref name="mode"/> needs and prepares the reference
	/// clip by sampling <paramref name="clipFrom"/> fresh. Downloads the ONNX model only when
	/// visual actually runs — <c>--detection-mode phash</c> never touches it, the whole point of
	/// offering pHash as a lightweight alternate mode. Returns an error string (never throws) on
	/// any failure a caller should print and exit nonzero for — same failure messages as before
	/// this refactor (clip produced no usable frames, extraction failed, etc.).
	/// </summary>
	internal static async Task<(MatchingSession? session, string? error)> PrepareAsync(
			DetectionMode mode, FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, EdgeDensityProfile profile,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			string? dumpFramesDir, bool verbose, CancellationToken ct) {
		var session = new MatchingSession(mode, region, profile, presenceThreshold, phashPresenceThreshold, minSimilarity, dumpFramesDir, verbose);
		try {
			if (session.WantsVisual || session.WantsPHash) {
				session.sampler = new MixedDensitySampler(verbose);
				session.visualForPresence = new VisualBumperMatcher(
					presenceThreshold: presenceThreshold, rigidHitThreshold: VBR.Core.Configuration.VbrConfig.Current.Matching.RigidHitThreshold);

				if (session.WantsVisual)
					await SharedOptions.EnsureAiComponentsReadyAsync(HardwareAcceleration.PreferDirectML, ct);

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
				try {
					session.audioReferenceClip = ClipExtractor.ExtractToTemp(clipFrom.FullName, ClipRegion.For(region, clipLength), verbose, ct);
				}
				catch (Exception ex) when (ex is FileNotFoundException or ArgumentOutOfRangeException or InvalidOperationException) {
					session.Dispose();
					return (null, $"Error: {ex.Message}");
				}
				// Extracted once here rather than left to be re-extracted per candidate (the old
				// per-call AudioBumperMatcher.Match behavior) -- see the class doc comment.
				session.referenceAudioFingerprint = AudioBumperMatcher.ExtractFingerprint(session.audioReferenceClip.Value.Path, verbose, ct);
				if (verbose && session.referenceAudioFingerprint is { Length: >= 2 } fp)
					Logger.Instance.Info($"[audio] reference clip: fingerprint extracted, {fp.Length} blocks.");
			}

			return (session, null);
		}
		catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentOutOfRangeException) {
			session.Dispose();
			return (null, $"Error: {ex.Message}");
		}
	}

	/// <summary>
	/// Like <see cref="PrepareAsync"/>, but the reference side comes from an already-built
	/// <see cref="BumperCatalogEntry"/> instead of sampling <c>--clip-from</c> — no decode, no
	/// ONNX inference, no ffmpeg extraction (docs/iterativeplan.md, "Utilizing Databases" entry:
	/// "must not attempt to re-extract the bumper from the source"). <paramref name="region"/> and
	/// the reference clip length are the catalog entry's own (<c>--region</c>/<c>--clip-length</c>
	/// are invalid alongside <c>--bumper-label</c>, so there's nothing to reconcile). Still
	/// synchronous and non-throwing, matching <see cref="PrepareAsync"/>'s error-string contract.
	/// <paramref name="profile"/>/<paramref name="dumpFramesDir"/> only matter if a candidate ends
	/// up being ad hoc-sampled (<see cref="Compare"/>) later in the run -- a fully cached run
	/// (catalog reference + database candidates) never touches either.
	/// </summary>
	internal static (MatchingSession? session, string? error) PrepareFromCatalogEntry(
			DetectionMode mode, BumperCatalogEntry entry, EdgeDensityProfile profile,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			string? dumpFramesDir, bool verboseLogging) {
		var session = new MatchingSession(mode, entry.Region, profile, presenceThreshold, phashPresenceThreshold, minSimilarity, dumpFramesDir, verboseLogging);

		if (session.WantsVisual || session.WantsPHash) {
			session.sampler = new MixedDensitySampler(verboseLogging);
			session.visualForPresence = new VisualBumperMatcher(
					presenceThreshold: presenceThreshold, rigidHitThreshold: VBR.Core.Configuration.VbrConfig.Current.Matching.RigidHitThreshold);
			session.clipEmbeddings = ToTimedFrames(entry.Fingerprints);
			session.clipHashes = ToTimedPHashes(entry.Fingerprints);
			if (session.clipEmbeddings.Count < 1) {
				session.Dispose();
				return (null, $"Error: Catalog bumper '{entry.Label}' has no usable fingerprints -- " +
					"it may have been added before fingerprinting existed, or with every sampled frame " +
					"filtered out. Re-add it via 'vbr add-bumper'.");
			}
		}

		if (session.WantsAudio) {
			session.referenceAudioFingerprint = entry.AudioFingerprint;
			if (verboseLogging)
				Logger.Instance.Info($"[audio] catalog bumper '{entry.Label}': using stored fingerprint " +
					$"({entry.AudioFingerprint?.Length ?? 0} blocks) -- no re-extraction.");
		}

		return (session, null);
	}

	/// <summary>Compares one ad hoc (fresh-sampled) candidate against the prepared reference
	/// across whichever signal(s) are active. <paramref name="searchLength"/> is the candidate-side
	/// total window (VBR's existing <c>--search-length</c>, typically larger than <c>--clip-length</c>
	/// for slack); <paramref name="dumpLabel"/> identifies this candidate's frames under
	/// <c>--dump-frames</c> (ignored when that's off).</summary>
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

		if (WantsAudio) {
			uint[]? candidateFingerprint = AudioBumperMatcher.ExtractFingerprint(candidatePath, verboseLogging, ct);
			if (verboseLogging && candidateFingerprint is { Length: >= 2 } fp)
				Logger.Instance.Info($"[audio] '{Path.GetFileName(candidatePath)}': fingerprint extracted, {fp.Length} blocks.");
			audioResult = AudioBumperMatcher.MatchFingerprints(
				referenceAudioFingerprint, candidateFingerprint, ClipRegion.For(region, searchLength), minSimilarity);
		}

		return new SignalResult(visualResult, audioResult, phashResult, ReferenceHasUsableAudio);
	}

	/// <summary>
	/// Compares one candidate against the prepared reference using only its already-persisted
	/// <see cref="LibraryDatabaseEntry"/> data -- no decode, no ffmpeg, no ONNX inference
	/// (docs/iterativeplan.md, "Utilizing Databases" entry: "must leverage the sampling/fingerprint
	/// data already in the database and not re-scan the video"). Visual/pHash fingerprints are
	/// filtered to the requested <paramref name="searchLength"/> window (using
	/// <paramref name="dbEntry"/>'s own already-probed <see cref="LibraryDatabaseEntry.Duration"/> --
	/// no ffprobe call needed either) before comparing; audio reuses
	/// <see cref="LibraryDatabaseEntry.AudioFingerprint"/> directly.
	/// </summary>
	internal SignalResult CompareUsingDatabase(LibraryDatabaseEntry dbEntry, TimeSpan searchLength) {
		MatchResult? visualResult = null, phashResult = null, audioResult = null;

		if (WantsVisual || WantsPHash) {
			(double start, double end) = SearchWindowSeconds(region, searchLength, dbEntry.Duration);
			List<TimedFingerprint> windowed = dbEntry.Fingerprints
				.Where(f => f.TimestampSeconds >= start && f.TimestampSeconds <= end)
				.ToList();
			if (WantsVisual)
				visualResult = visualForPresence!.MatchMixedDensity(clipEmbeddings!, ToTimedFrames(windowed));
			if (WantsPHash)
				phashResult = VisualBumperMatcher.MatchMixedDensityPHash(clipHashes!, ToTimedPHashes(windowed), phashPresenceThreshold);
		}

		if (WantsAudio) {
			// Debug tier, not --verbose (moved 2026-08-13 -- this fires once per candidate and was
			// drowning out --verbose's actual per-run diagnostics on a real library). Currently a
			// no-op for match/remove: only ScanCommand wires ScanTelemetry.DebugEnabled/subscribes
			// to DebugNoted today, so this line goes nowhere until something opts in -- see the
			// PROGRESS.md TODO this is filed under.
			ScanTelemetry.NoteDebug($"[audio] '{Path.GetFileName(dbEntry.Path)}': using cached database fingerprint " +
				$"({dbEntry.AudioFingerprint?.Length ?? 0} blocks) -- no audio decode.");
			audioResult = AudioBumperMatcher.MatchFingerprints(
				referenceAudioFingerprint, dbEntry.AudioFingerprint, ClipRegion.For(region, searchLength), minSimilarity);
		}

		return new SignalResult(visualResult, audioResult, phashResult, ReferenceHasUsableAudio);
	}

	/// <summary>The absolute-seconds-from-BOF window a <paramref name="searchLength"/>-sized search
	/// at <paramref name="region"/> covers within a file of <paramref name="fileDuration"/> --
	/// <see cref="LibraryDatabaseEntry.Fingerprints"/> are absolute-timestamped (unlike a freshly
	/// sampled clip's window-relative ones -- see <see cref="TimedFingerprint"/>'s doc comment), so
	/// filtering by real file position is what "the requested search window" means for a database
	/// entry.</summary>
	static (double start, double end) SearchWindowSeconds(ClipEdge region, TimeSpan searchLength, TimeSpan fileDuration) {
		double duration = fileDuration.TotalSeconds;
		double length = Math.Min(searchLength.TotalSeconds, duration);
		return region == ClipEdge.begin ? (0, length) : (Math.Max(0, duration - length), duration);
	}

	static List<TimedFrame> ToTimedFrames(IEnumerable<TimedFingerprint> fingerprints) =>
		fingerprints.Select(f => new TimedFrame(f.TimestampSeconds, f.Embedding)).ToList();

	static List<TimedPHash> ToTimedPHashes(IEnumerable<TimedFingerprint> fingerprints) =>
		fingerprints.Select(f => new TimedPHash(f.TimestampSeconds, f.PHash)).ToList();

	public void Dispose() {
		sampler?.Dispose();
		visualForPresence?.Dispose();
		audioReferenceClip?.Dispose();
	}
}
