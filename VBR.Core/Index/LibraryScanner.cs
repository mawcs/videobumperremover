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

namespace VBR.Core.Index;

/// <summary>
/// Orchestrates one `vbr scan` run over a resolved candidate list: for each file, decide whether
/// the cached entry can be reused or the file needs (re-)sampling
/// (<see cref="WholeFileSampler"/> + a whole-file Chromaprint audio fingerprint), and periodically
/// checkpoint the index to disk so an interrupted run loses at most the work since the last save
/// (decision 7). Processes candidates one at a time (decision 8 — sequential for v1).
///
/// Change-detection mirrors the *logic* of VDF's own <c>ScanEngine.RefreshExistingEntry</c> without
/// touching VDF's code or data (decision 2): size changed → re-sample; same size, timestamps moved →
/// verify via <see cref="OsHashUtils"/> before trusting the cache; same size and timestamps → trust
/// outright. Move/rename relinking (VDF's <c>TryRelinkMovedFile</c>) is deliberately not
/// implemented here — a moved file just re-samples as if new, a conscious v1 simplification, not an
/// oversight. Entries whose file no longer exists on disk at all are dropped, not tombstoned (no
/// VDF-style <c>RememberDeletedContent</c> equivalent yet).
/// </summary>
public sealed class LibraryScanner : IDisposable {
	public enum ScanOutcome { Sampled, SkippedUnchanged, Failed }

	public readonly record struct FileScanResult(string Path, ScanOutcome Outcome, string? Detail, string? Error);

	public readonly record struct ScanSummary(int Scanned, int SkippedUnchanged, int Failed, int Total);

	static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromSeconds(30);

	readonly WholeFileSampler sampler;
	readonly bool verboseLogging;
	readonly TimeSpan checkpointInterval;

	/// <param name="checkpointInterval">How often the index is saved mid-scan (decision 7). Exposed
	/// (rather than a hardcoded constant) so tests can force frequent checkpoints deterministically;
	/// defaults to a sane interval for real use.</param>
	public LibraryScanner(bool verboseLogging = false, TimeSpan? checkpointInterval = null) {
		this.verboseLogging = verboseLogging;
		this.checkpointInterval = checkpointInterval ?? DefaultCheckpointInterval;
		sampler = new WholeFileSampler(verboseLogging);
	}

	/// <summary>
	/// Scans <paramref name="candidatePaths"/> into <paramref name="index"/> (mutated in place),
	/// saving to <paramref name="indexPath"/> at each checkpoint and once more at the end.
	/// <paramref name="onFileScanned"/> fires after every candidate (sampled, skipped, or failed) —
	/// the CLI uses it to drive both the default running counter and <c>--verbose</c> per-file
	/// detail from one callback. <paramref name="onCheckpoint"/> fires after every save (each
	/// mid-scan checkpoint plus the final save), reporting the entry count at that point — mainly
	/// so tests can verify checkpointing actually happens without needing a real long-running scan.
	/// </summary>
	public ScanSummary Scan(
			IReadOnlyList<string> candidatePaths,
			LibraryIndex index,
			string indexPath,
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
		foreach (string staleKey in index.Entries
				.Where(kv => !File.Exists(kv.Value.Path))
				.Select(kv => kv.Key)
				.ToList())
			index.Entries.Remove(staleKey);

		foreach (string path in candidatePaths) {
			ct.ThrowIfCancellationRequested();
			string key = LibraryIndexKey.Normalize(path);
			try {
				var fileInfo = new FileInfo(path);
				if (!fileInfo.Exists) {
					index.Entries.Remove(key);
					failed++;
					onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.Failed, null, "file no longer exists"));
					continue;
				}

				if (!forceRescan && index.Entries.TryGetValue(key, out LibraryIndexEntry? existing) && TryReuse(existing, fileInfo)) {
					skipped++;
					onFileScanned?.Invoke(new FileScanResult(path, ScanOutcome.SkippedUnchanged, "cached, unchanged", null));
					continue;
				}

				LibraryIndexEntry entry = SampleFile(path, fileInfo, profile, ct);
				index.Entries[key] = entry;
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
					LibraryIndexStore.Save(index, indexPath);
					lastCheckpoint = DateTime.UtcNow;
					onCheckpoint?.Invoke(index.Entries.Count);
					if (verboseLogging)
						Logger.Instance.Info($"[scan] checkpoint saved ({index.Entries.Count} entries).");
				}
			}
		}

		LibraryIndexStore.Save(index, indexPath);
		onCheckpoint?.Invoke(index.Entries.Count);
		return new ScanSummary(scanned, skipped, failed, candidatePaths.Count);
	}

	// Same shape as VDF's ScanEngine.RefreshExistingEntry: size mismatch never survives; a
	// same-size, same-timestamps entry is trusted outright; a same-size, different-timestamps entry
	// is trusted only once OsHash proves the bytes are actually unchanged.
	static bool TryReuse(LibraryIndexEntry existing, FileInfo fileInfo) {
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

	LibraryIndexEntry SampleFile(string path, FileInfo fileInfo, EdgeDensityProfile profile, CancellationToken ct) {
		WholeFileSampler.Result sampled = sampler.Sample(path, profile, ct);
		uint[]? audioFingerprint = ChromaprintEngine.ExtractFingerprint(path, verboseLogging, ct);
		return new LibraryIndexEntry {
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
