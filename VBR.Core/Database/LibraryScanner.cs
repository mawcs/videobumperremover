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
using VBR.Core.Fingerprinting;
using VDF.Core.FFTools;
using VDF.Core.Utils;

namespace VBR.Core.Database;

/// <summary>
/// Orchestrates one `vbr scan` run over a resolved candidate list: for each file, decide whether
/// the cached entry can be reused or the file needs (re-)sampling
/// (<see cref="WholeFileSampler"/> + a whole-file Chromaprint audio fingerprint), and periodically
/// checkpoint the database to disk so an interrupted run loses at most the work since the last save
/// (decision 7). Processes candidates one at a time (decision 8 — sequential for v1).
///
/// Change-detection mirrors the *logic* of VDF's own <c>ScanEngine.RefreshExistingEntry</c> without
/// touching VDF's code or data (decision 2): size changed → re-sample; same size, timestamps moved →
/// verify via <see cref="OsHashUtils"/> before trusting the cache; same size and timestamps → trust
/// outright. Move/rename relinking (VDF's <c>TryRelinkMovedFile</c>) is deliberately not
/// implemented here — a moved file just re-samples as if new, a conscious v1 simplification, not an
/// oversight. Entries whose file no longer exists on disk at all are dropped, not tombstoned (no
/// VDF-style <c>RememberDeletedContent</c> equivalent yet — see iterativeplan.md's "CLI terminology
/// & multi-folder libraries" entry, "the tombstone question," for the open question this leaves).
/// </summary>
public sealed class LibraryScanner : IDisposable {
	public enum ScanOutcome { Sampled, SkippedUnchanged, Failed }

	public readonly record struct FileScanResult(string Path, ScanOutcome Outcome, string? Detail, string? Error);

	/// <param name="DatabaseSaveError">Null if the database's final save succeeded. Non-null means the
	/// scan itself completed (every candidate was sampled/skipped/failed as reported above) but the
	/// *last* attempt to persist the database to <c>databasePath</c> failed — distinct from a per-file
	/// failure because it means some or all of this run's results may not actually be on disk, not
	/// just that one candidate couldn't be read.</param>
	public readonly record struct ScanSummary(int Scanned, int SkippedUnchanged, int Failed, int Total, string? DatabaseSaveError = null);

	static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromSeconds(30);

	readonly WholeFileSampler sampler;
	readonly bool verboseLogging;
	readonly TimeSpan checkpointInterval;

	/// <param name="checkpointInterval">How often the database is saved mid-scan (decision 7). Exposed
	/// (rather than a hardcoded constant) so tests can force frequent checkpoints deterministically;
	/// defaults to a sane interval for real use.</param>
	public LibraryScanner(bool verboseLogging = false, TimeSpan? checkpointInterval = null) {
		this.verboseLogging = verboseLogging;
		this.checkpointInterval = checkpointInterval ?? DefaultCheckpointInterval;
		sampler = new WholeFileSampler(verboseLogging);
	}

	/// <summary>
	/// Scans <paramref name="candidatePaths"/> into <paramref name="database"/> (mutated in place),
	/// saving to <paramref name="databasePath"/> at each checkpoint and once more at the end.
	/// <paramref name="onFileScanned"/> fires after every candidate (sampled, skipped, or failed) —
	/// the CLI uses it to drive both the default running counter and <c>--verbose</c> per-file
	/// detail from one callback. <paramref name="onCheckpoint"/> fires after every *successful* save
	/// (each mid-scan checkpoint plus the final save), reporting the entry count at that point —
	/// mainly so tests can verify checkpointing actually happens without needing a real long-running
	/// scan. Neither a per-file failure nor a save failure ever throws out of this method (besides
	/// <see cref="OperationCanceledException"/> from <paramref name="ct"/>, which is intentionally
	/// left to propagate) — both are logged and the scan continues; see
	/// <see cref="ScanSummary.DatabaseSaveError"/> for how a save failure is reported back.
	/// </summary>
	public ScanSummary Scan(
			IReadOnlyList<string> candidatePaths,
			LibraryDatabase database,
			string databasePath,
			EdgeDensityProfile profile,
			bool forceRescan,
			Action<FileScanResult>? onFileScanned = null,
			CancellationToken ct = default,
			Action<int>? onCheckpoint = null) {
		int scanned = 0, skipped = 0, failed = 0;
		DateTime lastCheckpoint = DateTime.UtcNow;

		// Drop entries for files that no longer exist at all -- not entries merely absent from
		// *this run's* filtered candidate list (e.g. a .vbr. output with --include-vbr-outputs off
		// this time but on previously), which would be a real file losing its cache for no reason.
		foreach (string staleKey in database.Entries
				.Where(kv => !File.Exists(kv.Value.Path))
				.Select(kv => kv.Key)
				.ToList())
			database.Entries.Remove(staleKey);

		foreach (string path in candidatePaths) {
			ct.ThrowIfCancellationRequested();
			string key = LibraryDatabaseKey.Normalize(path);
			try {
				var fileInfo = new FileInfo(path);
				if (!fileInfo.Exists) {
					database.Entries.Remove(key);
					failed++;
					onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.Failed, null, "file no longer exists"));
					continue;
				}

				if (!forceRescan && database.Entries.TryGetValue(key, out LibraryDatabaseEntry? existing) && TryReuse(existing, fileInfo)) {
					skipped++;
					onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.SkippedUnchanged, "cached, unchanged", null));
					continue;
				}

				LibraryDatabaseEntry entry = SampleFile(path, fileInfo, profile, ct);
				database.Entries[key] = entry;
				scanned++;
				onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.Sampled,
					$"{entry.Fingerprints.Length} fingerprint(s), duration {entry.Duration}", null));
			}
			catch (Exception ex) when (ex is not OperationCanceledException) {
				failed++;
				onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.Failed, null, ex.Message));
			}
			finally {
				// finally, not "after the try/catch" -- the try above `continue`s on both the
				// skip-unchanged and file-vanished paths, which would otherwise jump straight to
				// the next iteration and never reach a checkpoint check placed after the catch.
				// Checkpointing must run for every file regardless of outcome: skipped-unchanged is
				// the *common* case on a re-scan, so gating checkpoints on "something was actually
				// sampled" would checkpoint far less often than decision 7 intends.
				if (DateTime.UtcNow - lastCheckpoint >= checkpointInterval) {
					if (TrySave(database, databasePath) is null) {
						onCheckpoint?.Invoke(database.Entries.Count);
						if (verboseLogging)
							Logger.Instance.Info($"[scan] checkpoint saved ({database.Entries.Count} entries).");
					}
					lastCheckpoint = DateTime.UtcNow;
				}
			}
		}

		string? databaseSaveError = TrySave(database, databasePath);
		if (databaseSaveError is null)
			onCheckpoint?.Invoke(database.Entries.Count);
		return new ScanSummary(scanned, skipped, failed, candidatePaths.Count, databaseSaveError);
	}

	/// <summary>Saves the database, catching and logging (never crashing the scan on) any failure —
	/// most commonly a transient antivirus/other-process lock on the database file that
	/// <see cref="LibraryDatabaseStore.Save"/>'s own retries didn't clear, or a genuinely bad
	/// destination path. <paramref name="database"/> keeps every sampled entry in memory regardless, so
	/// a failed save here costs nothing beyond this attempt: the next checkpoint (or the final save)
	/// tries again and, once one succeeds, persists everything accumulated up to that point — the
	/// same "log it, keep going" contract as a per-file sampling failure, just for the save step
	/// instead of the read step. Returns null on success, else the exception's message (for
	/// <see cref="ScanSummary.DatabaseSaveError"/>).</summary>
	static string? TrySave(LibraryDatabase database, string databasePath) {
		try {
			LibraryDatabaseStore.Save(database, databasePath);
			return null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException) {
			Logger.Instance.Warn($"[scan] Failed to save database to '{databasePath}': {ex.Message}");
			return ex.Message;
		}
	}

	// Same shape as VDF's ScanEngine.RefreshExistingEntry: size mismatch never survives; a
	// same-size, same-timestamps entry is trusted outright; a same-size, different-timestamps entry
	// is trusted only once OsHash proves the bytes are actually unchanged.
	static bool TryReuse(LibraryDatabaseEntry existing, FileInfo fileInfo) {
		if (existing.FileSize != fileInfo.Length)
			return false;
		if (existing.DateCreated == fileInfo.CreationTimeUtc && existing.DateModified == fileInfo.LastWriteTimeUtc)
			return true;
		string? osHash = OsHashUtils.TryCompute(fileInfo.FullName);
		if (osHash != null && osHash == existing.OsHash) {
			existing.DateCreated = fileInfo.CreationTimeUtc;
			existing.DateModified = fileInfo.LastWriteTimeUtc;
			return true;
		}
		return false;
	}

	LibraryDatabaseEntry SampleFile(string path, FileInfo fileInfo, EdgeDensityProfile profile, CancellationToken ct) {
		WholeFileSampler.Result sampled = sampler.Sample(path, profile, ct);
		uint[]? audioFingerprint = ChromaprintEngine.ExtractFingerprint(path, verboseLogging, ct);
		return new LibraryDatabaseEntry {
			Path = path,
			FileSize = fileInfo.Length,
			DateCreated = fileInfo.CreationTimeUtc,
			DateModified = fileInfo.LastWriteTimeUtc,
			OsHash = OsHashUtils.TryCompute(path),
			Duration = sampled.Duration,
			AudioFingerprint = audioFingerprint,
			Fingerprints = sampled.Fingerprints.ToArray(),
		};
	}

	public void Dispose() => sampler.Dispose();
}
