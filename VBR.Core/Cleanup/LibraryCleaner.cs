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
using System.Text.Json;
using System.Threading;
using VBR.Core.Extraction;
using VBR.Core.Removal;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Cleanup;

/// <summary>How one candidate's cleanup attempt ended.</summary>
public enum CleanupOutcome {
	/// <summary>Original replaced by the cleaned output; old original and manifest both removed.</summary>
	Cleaned,
	/// <summary>Validation, marking, or promotion failed — needs the user's attention. The
	/// original is guaranteed untouched (still present under its normal name).</summary>
	Broken,
	/// <summary>The swap succeeded (the correct content is live under the original's name) but
	/// deleting the old original and/or manifest failed — self-heals via a future run's recovery
	/// sweep, not urgent.</summary>
	PendingReclamation,
	/// <summary><c>--file</c> only: the target has no <c>.vbr.</c> output, so there was nothing to do.</summary>
	Skipped,
}

/// <summary>Result of attempting to clean one original/<c>.vbr.</c> pair.</summary>
public sealed record CleanupFileResult(string OriginalPath, CleanupOutcome Outcome, string? Detail);

/// <summary>One reconciliation performed by the startup recovery sweep (ADR 0008, Decision 8) for
/// a marker left behind by a previous run that didn't complete cleanly.</summary>
public sealed record RecoveryAction(string OriginalPath, string Detail);

/// <summary>Everything that happened processing one directory (or one <c>--file</c> target).</summary>
public sealed record CleanupRunResult(IReadOnlyList<CleanupFileResult> Results, IReadOnlyList<RecoveryAction> RecoveryActions);

/// <summary>
/// Promotes verified <c>.vbr.</c> outputs (written by <see cref="ClipRemover"/>, never touched by
/// it — see ADR 0007) to replace their originals: the one operation in this project permitted to
/// delete video files. Full design and rationale: ADR 0008
/// (docs/decisions/0008-cleanup-command.md). Key properties, all decided there:
/// <list type="bullet">
/// <item>Original ↔ output pairing is derived from the filename (the inverse of
/// <see cref="ClipRemover.BuildOutputPath"/>), never from the JSON manifest — the manifest can be
/// separated from, or deleted independently of, the video it describes, so it is not a dependable
/// mechanism for deciding what to touch.</item>
/// <item>Per file: mark the original for deletion, promote the output into its name, then
/// best-effort delete the marked original and its manifest — fully resolved before moving to the
/// next file (pairwise, not phase-batched). Rollback covers only the mark/promote swap; a failed
/// final delete is never rolled back; see <see cref="CleanOnePair"/>.</item>
/// <item>A cheap, unconditional recovery sweep runs before the main pass in every directory,
/// reconciling markers left behind by a previous run that didn't finish; see
/// <see cref="RecoverOnePair"/>.</item>
/// <item>No secondary trash/soft-delete stage — <c>remove</c>'s non-destructive sibling output
/// already serves as the recycle bin during the review window before <c>cleanup</c> runs.</item>
/// </list>
/// </summary>
public static class LibraryCleaner {
	/// <summary>Appended after the full original filename (not inserted before the extension, so
	/// a marked file's last extension is no longer a video extension — this incidentally shrinks
	/// the window in which a player, indexer, or AV scanner might open/lock it while it's
	/// mid-flight). Decided, ADR 0008 Decision 7b.</summary>
	public const string DeletionMarkerSuffix = ".vbrdelete";

	/// <summary>Placeholder pending ADR 0008's open "--validate-files duration tolerance" question
	/// — looser than <see cref="ClipRemover.EndCutOvershootSafetyMarginSeconds"/> deliberately:
	/// this is a post-hoc sanity check meant to catch gross errors (wrong length used, wrong file),
	/// not a precise re-verification of exact cut placement — precision already happened in
	/// <c>remove</c>.</summary>
	public const double ValidateFilesDurationToleranceSeconds = 2.0;

	/// <summary>Inverse of <see cref="ClipRemover.BuildOutputPath"/>: given a <c>.vbr.</c> output
	/// path, returns the original path it was cut from, or <see langword="null"/> if
	/// <paramref name="vbrPath"/> doesn't look like one of our outputs at all. Anchored the same
	/// way <c>BuildOutputPath</c> builds it (the <c>.vbr</c> infix immediately before the real
	/// extension, or the whole "extension" when the original had none) — never a substring match
	/// anywhere else in the path. Requires the recovered original to end in a recognized video
	/// extension (or have none) — without this, a manifest sitting right next to its output
	/// (<c>name.vbr.json</c>) would itself parse as "a .vbr. output named name.vbr, extension
	/// .json" and get offered up as something to promote, which is exactly wrong.</summary>
	public static string? OriginalPathFor(string vbrPath) {
		string? dir = Path.GetDirectoryName(vbrPath);
		string nameWithoutExt = Path.GetFileNameWithoutExtension(vbrPath);
		string ext = Path.GetExtension(vbrPath);
		string originalName;
		if (ext.Equals(".vbr", StringComparison.OrdinalIgnoreCase)) {
			// e.g. "no-extension.vbr" -> the original had no extension at all: "no-extension".
			originalName = nameWithoutExt;
		}
		else if (nameWithoutExt.EndsWith(".vbr", StringComparison.OrdinalIgnoreCase)
				&& ClipExtractor.VideoExtensions.Contains(ext.ToLowerInvariant())) {
			// e.g. "S01E01.vbr.mkv" -> "S01E01" + ".mkv" — only when .mkv is a real video
			// extension, so "S01E01.vbr.json" (the manifest) correctly falls through to null.
			originalName = nameWithoutExt[..^4] + ext;
		}
		else {
			return null;
		}
		return dir is { Length: > 0 } ? Path.Combine(dir, originalName) : originalName;
	}

	/// <summary>
	/// <c>--library</c> path (ADR 0008, Decision 2): processes one directory, top level only —
	/// the caller walks subdirectories itself, calling this once per directory. Runs the recovery
	/// sweep to completion first, then a single pairwise pass over every <c>.vbr.</c> file found
	/// afterward (so a file the sweep just restored is picked up naturally, as an ordinary
	/// candidate, with no special-casing).
	/// </summary>
	public static CleanupRunResult CleanDirectory(string directory, bool validateFiles, bool verbose, CancellationToken ct = default) {
		var enumOptions = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
		var recoveryActions = new List<RecoveryAction>();

		foreach (string markedPath in Directory.EnumerateFiles(directory, $"*{DeletionMarkerSuffix}", enumOptions)) {
			ct.ThrowIfCancellationRequested();
			string originalPath = markedPath[..^DeletionMarkerSuffix.Length];
			string vbrPath = ClipRemover.BuildOutputPath(originalPath);
			RecoveryAction? action = RecoverOnePair(originalPath, vbrPath, markedPath, verbose);
			if (action is not null) recoveryActions.Add(action);
		}

		var vbrFiles = Directory.EnumerateFiles(directory, "*", enumOptions)
			.Where(f => OriginalPathFor(f) is not null)
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			.ToList();

		var results = new List<CleanupFileResult>(vbrFiles.Count);
		foreach (string vbrPath in vbrFiles) {
			ct.ThrowIfCancellationRequested();
			results.Add(CleanOnePair(OriginalPathFor(vbrPath)!, vbrPath, validateFiles, verbose, ct));
		}

		return new CleanupRunResult(results, recoveryActions);
	}

	/// <summary>
	/// <c>--file</c> path (ADR 0008, Decision 10 — decided 2026-07-20): touches only
	/// <paramref name="targetPath"/>'s own pairing/marker state, never anything else sharing its
	/// directory. <paramref name="targetPath"/> may name either the original or the <c>.vbr.</c>
	/// output — resolved via <see cref="OriginalPathFor"/> either way, so either name works.
	/// </summary>
	public static (CleanupFileResult Result, RecoveryAction? Recovery) CleanSingleFile(string targetPath, bool validateFiles, bool verbose, CancellationToken ct = default) {
		string? derivedOriginal = OriginalPathFor(targetPath);
		string originalPath = derivedOriginal ?? targetPath;
		string vbrPath = derivedOriginal is not null ? targetPath : ClipRemover.BuildOutputPath(targetPath);
		string markedPath = originalPath + DeletionMarkerSuffix;

		RecoveryAction? recovery = RecoverOnePair(originalPath, vbrPath, markedPath, verbose);

		if (!File.Exists(vbrPath))
			return (new CleanupFileResult(originalPath, CleanupOutcome.Skipped, "No .vbr. output exists for this file — nothing to clean."), recovery);

		return (CleanOnePair(originalPath, vbrPath, validateFiles, verbose, ct), recovery);
	}

	/// <summary>
	/// Reconciles one leftover <paramref name="markedPath"/> from a run that didn't complete
	/// cleanly (ADR 0008, Decision 8). Returns <see langword="null"/> if there was nothing to do.
	/// Two unambiguous cases, told apart purely by which of <paramref name="originalPath"/> /
	/// <paramref name="vbrPath"/> currently exist:
	/// <list type="bullet">
	/// <item>Original exists, <c>.vbr.</c> doesn't → promotion had already completed; only the
	/// final delete (step (e)) was interrupted. Retry the delete.</item>
	/// <item>Original doesn't exist, <c>.vbr.</c> does → interrupted between mark and promote.
	/// Conservatively restore the original rather than assume it's safe to finish the swap — an
	/// abnormal termination doesn't tell us enough to trust "resume forward" as the last-known-good
	/// intent, matching AGENTS.md's "verification before destruction."</item>
	/// </list>
	/// Anything else (both exist, or neither) is ambiguous and left untouched rather than guessed at.
	/// </summary>
	static RecoveryAction? RecoverOnePair(string originalPath, string vbrPath, string markedPath, bool verbose) {
		if (!File.Exists(markedPath)) return null;

		bool originalExists = File.Exists(originalPath);
		bool vbrExists = File.Exists(vbrPath);
		RecoveryAction result;

		if (originalExists && !vbrExists) {
			try {
				File.Delete(markedPath);
				result = new RecoveryAction(originalPath,
					"Found a marked-for-deletion file left by an interrupted run; the promotion had already completed, so the old file was deleted now.");
			}
			catch (Exception ex) {
				result = new RecoveryAction(originalPath,
					$"Found a marked-for-deletion file left by an interrupted run (promotion had completed) but could not delete it: {ex.Message}");
			}
		}
		else if (!originalExists && vbrExists) {
			try {
				File.Move(markedPath, originalPath);
				result = new RecoveryAction(originalPath,
					"Found a marked-for-deletion file left by a run interrupted before promotion; restored the original so it can be reprocessed.");
			}
			catch (Exception ex) {
				result = new RecoveryAction(originalPath,
					$"Found a marked-for-deletion file left by an interrupted run and could not restore it: {ex.Message} — manual attention needed at '{markedPath}'.");
			}
		}
		else {
			result = new RecoveryAction(originalPath,
				$"Found a marked-for-deletion file in an unexpected state (original exists={originalExists}, .vbr. exists={vbrExists}) — left untouched, needs manual review.");
		}

		if (verbose) Logger.Instance.Info($"[cleanup] Recovery: '{Path.GetFileName(originalPath)}': {result.Detail}");
		return result;
	}

	/// <summary>
	/// The pairwise mark → promote → delete sequence for one already-paired original/<c>.vbr.</c>
	/// file (ADR 0008, Decision 7). Rollback covers only the mark/promote swap — once both
	/// succeed, the swap is never undone, and a subsequent delete failure is reported as
	/// <see cref="CleanupOutcome.PendingReclamation"/>, not treated as broken.
	/// </summary>
	static CleanupFileResult CleanOnePair(string originalPath, string vbrPath, bool validateFiles, bool verbose, CancellationToken ct) {
		string markedPath = originalPath + DeletionMarkerSuffix;
		// Named after the original, not the .vbr. output (ClipRemover.Remove, decided 2026-07-20).
		string manifestPath = Path.ChangeExtension(originalPath, ".json");
		string fileTag = Path.GetFileName(originalPath);

		if (!File.Exists(originalPath))
			return Broken(originalPath, "No matching original file found next to this .vbr. output.");

		if (validateFiles) {
			string? validationError = ValidateVbrFile(vbrPath, manifestPath, originalPath, verbose);
			if (validationError is not null) return Broken(originalPath, validationError);
		}

		try {
			File.Move(originalPath, markedPath);
		}
		catch (Exception ex) {
			return Broken(originalPath, $"Could not mark the original for deletion: {ex.Message}");
		}

		try {
			File.Move(vbrPath, originalPath);
		}
		catch (Exception ex) {
			try {
				File.Move(markedPath, originalPath);
				return Broken(originalPath, $"Could not promote the cleaned output ({ex.Message}); rolled back, original restored.");
			}
			catch (Exception rollbackEx) {
				return Broken(originalPath,
					$"Could not promote the cleaned output ({ex.Message}); ROLLBACK ALSO FAILED ({rollbackEx.Message}) — " +
					$"manual attention needed, check for '{markedPath}'.");
			}
		}

		// Swap complete and correct — name.ext now holds the cleaned content. Never undone past
		// this point: everything below is best-effort housekeeping, not correctness.
		ct.ThrowIfCancellationRequested();
		var leftovers = new List<string>(2);
		try { File.Delete(markedPath); }
		catch (Exception ex) { leftovers.Add($"old original ({ex.Message})"); }
		try { if (File.Exists(manifestPath)) File.Delete(manifestPath); }
		catch (Exception ex) { leftovers.Add($"manifest ({ex.Message})"); }

		if (leftovers.Count > 0) {
			string detail = $"Cleaned, but could not remove: {string.Join("; ", leftovers)}.";
			if (verbose) Logger.Instance.Warn($"[cleanup] '{fileTag}': {detail}");
			return new CleanupFileResult(originalPath, CleanupOutcome.PendingReclamation, detail);
		}

		if (verbose) Logger.Instance.Info($"[cleanup] '{fileTag}': cleaned.");
		return new CleanupFileResult(originalPath, CleanupOutcome.Cleaned, null);

		CleanupFileResult Broken(string original, string detail) {
			if (verbose) Logger.Instance.Warn($"[cleanup] '{fileTag}': {detail}");
			return new CleanupFileResult(original, CleanupOutcome.Broken, detail);
		}
	}

	/// <summary>
	/// <c>--validate-files</c> (ADR 0008, Decision 4): confirms <paramref name="vbrPath"/> is a
	/// real, playable file before it's allowed anywhere near the original. Prefers a precise check
	/// against the manifest's recorded figures when the manifest is present and parses; falls back
	/// to a coarser "shorter than the original" check when it isn't — the manifest is a diagnostic
	/// convenience here (Decision 3: never a dependency), so its absence must degrade gracefully,
	/// not disable validation entirely. Returns <see langword="null"/> on success, or a
	/// human-readable reason on failure.
	/// </summary>
	static string? ValidateVbrFile(string vbrPath, string manifestPath, string originalPath, bool verbose) {
		var vbrInfo = FFProbeEngine.GetMediaInfo(vbrPath, extendedLogging: verbose);
		if (vbrInfo is null || vbrInfo.Duration <= TimeSpan.Zero)
			return "Cleaned output failed to probe or reported no duration — likely corrupt or incomplete.";

		if (File.Exists(manifestPath)) {
			try {
				var entry = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), RemovalJsonContext.Default.RemovalManifestEntry);
				if (entry is not null) {
					double expectedSeconds = string.Equals(entry.Region, nameof(ClipEdge.end), StringComparison.OrdinalIgnoreCase)
						? entry.CutPointSeconds
						: entry.SourceDurationSeconds - entry.CutPointSeconds;
					double actualSeconds = vbrInfo.Duration.TotalSeconds;
					if (Math.Abs(actualSeconds - expectedSeconds) > ValidateFilesDurationToleranceSeconds)
						return $"Cleaned output duration ({actualSeconds:0.###}s) does not match the manifest's expected duration ({expectedSeconds:0.###}s).";
					return null;
				}
			}
			catch (JsonException) {
				// Falls through to the coarse check below — an unreadable manifest degrades
				// validation, it doesn't disable it (Decision 3/4).
			}
		}

		var originalInfo = FFProbeEngine.GetMediaInfo(originalPath, extendedLogging: verbose);
		if (originalInfo is null || originalInfo.Duration <= TimeSpan.Zero)
			return "No usable manifest, and could not probe the original file's duration for comparison.";
		if (vbrInfo.Duration >= originalInfo.Duration)
			return $"No usable manifest, and the cleaned output ({vbrInfo.Duration.TotalSeconds:0.###}s) is not shorter than the original ({originalInfo.Duration.TotalSeconds:0.###}s).";
		return null;
	}
}
