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
using VBR.Core.Fingerprinting;
using VBR.Core.Index;
using VDF.Core.Utils;
using Xunit;

namespace VBR.Tests.Index;

/// <summary>
/// Covers <see cref="LibraryScanner"/>'s change-detection and resilience logic without needing
/// real video content or the ONNX model: these scenarios either never reach
/// <see cref="WholeFileSampler"/> at all (the cache-hit paths) or deliberately exercise what
/// happens when it fails on a non-video file (the resilience paths) — real-media sampling itself
/// is covered by the env-var-gated tests alongside <c>VisualBumperMatcherMixedDensityTests</c>.
/// </summary>
public class LibraryScannerTests {
	static readonly EdgeDensityProfile Profile = new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(4));

	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_scanner_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	static string WriteFile(string dir, string name, int sizeBytes) {
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, new byte[sizeBytes]);
		return path;
	}

	static LibraryIndexEntry CacheEntryMatching(string path) {
		var info = new FileInfo(path);
		return new LibraryIndexEntry {
			Path = path,
			FileSize = info.Length,
			DateCreated = info.CreationTimeUtc,
			DateModified = info.LastWriteTimeUtc,
			OsHash = OsHashUtils.TryCompute(path),
			Duration = TimeSpan.FromMinutes(1),
			Fingerprints = new[] { new TimedFingerprint(0, new byte[] { 1 }, 1UL) },
		};
	}

	[Fact]
	public void UnchangedFile_WithMatchingCacheEntry_IsSkippedWithoutResampling() {
		string dir = CreateTempDir();
		try {
			string file = WriteFile(dir, "episode.mkv", 1024);
			var index = new LibraryIndex();
			index.Entries[LibraryIndexKey.Normalize(file)] = CacheEntryMatching(file);
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			var results = new List<LibraryScanner.FileScanResult>();
			LibraryScanner.ScanSummary summary = scanner.Scan(
				new[] { file }, index, indexPath, Profile, forceRescan: false, results.Add);

			Assert.Equal(1, summary.SkippedUnchanged);
			Assert.Equal(0, summary.Scanned);
			Assert.Equal(0, summary.Failed);
			Assert.Equal(LibraryScanner.ScanOutcome.SkippedUnchanged, Assert.Single(results).Outcome);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void ForceRescan_BypassesCache_EvenWhenUnchanged() {
		string dir = CreateTempDir();
		try {
			// Not a real video, so the forced re-sample attempt fails -- the point of this test is
			// that it *attempts* one at all (proving the cache was bypassed), not that it succeeds.
			string file = WriteFile(dir, "episode.mkv", 1024);
			var index = new LibraryIndex();
			index.Entries[LibraryIndexKey.Normalize(file)] = CacheEntryMatching(file);
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			var results = new List<LibraryScanner.FileScanResult>();
			scanner.Scan(new[] { file }, index, indexPath, Profile, forceRescan: true, results.Add);

			Assert.NotEqual(LibraryScanner.ScanOutcome.SkippedUnchanged, Assert.Single(results).Outcome);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void ChangedFileSize_TriggersAResampleAttempt_NotASkip() {
		string dir = CreateTempDir();
		try {
			string file = WriteFile(dir, "episode.mkv", 2048);
			var index = new LibraryIndex();
			LibraryIndexEntry stale = CacheEntryMatching(file);
			stale.FileSize = 1; // pretend the cached entry was for a very different-sized file
			index.Entries[LibraryIndexKey.Normalize(file)] = stale;
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			var results = new List<LibraryScanner.FileScanResult>();
			scanner.Scan(new[] { file }, index, indexPath, Profile, forceRescan: false, results.Add);

			Assert.NotEqual(LibraryScanner.ScanOutcome.SkippedUnchanged, Assert.Single(results).Outcome);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void TimestampsChanged_ButContentProvenSameViaOsHash_IsSkippedAndTimestampsRefreshed() {
		string dir = CreateTempDir();
		try {
			// OsHashUtils needs >= 64 KiB to produce a hash at all.
			string file = WriteFile(dir, "episode.mkv", 70 * 1024);
			var index = new LibraryIndex();
			LibraryIndexEntry stale = CacheEntryMatching(file);
			stale.DateModified = stale.DateModified.AddDays(-1); // same bytes, different recorded mtime
			index.Entries[LibraryIndexKey.Normalize(file)] = stale;
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			var results = new List<LibraryScanner.FileScanResult>();
			LibraryScanner.ScanSummary summary = scanner.Scan(
				new[] { file }, index, indexPath, Profile, forceRescan: false, results.Add);

			Assert.Equal(1, summary.SkippedUnchanged);
			Assert.Equal(LibraryScanner.ScanOutcome.SkippedUnchanged, Assert.Single(results).Outcome);
			// The stored entry's timestamp was refreshed even though sampling was skipped.
			LibraryIndexEntry refreshed = index.Entries[LibraryIndexKey.Normalize(file)];
			Assert.Equal(new FileInfo(file).LastWriteTimeUtc, refreshed.DateModified);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void MissingOnDiskEntry_IsDroppedFromIndex() {
		string dir = CreateTempDir();
		try {
			string ghostPath = Path.Combine(dir, "deleted-episode.mkv");
			var index = new LibraryIndex();
			index.Entries[LibraryIndexKey.Normalize(ghostPath)] = new LibraryIndexEntry { Path = ghostPath };
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			scanner.Scan(Array.Empty<string>(), index, indexPath, Profile, forceRescan: false);

			Assert.Empty(index.Entries);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void OneFileFailing_DoesNotStopTheRestOfTheScan() {
		string dir = CreateTempDir();
		try {
			// Neither is a real video, so both fail to sample -- the point is the loop survives
			// the first failure and still reports the second, rather than the whole Scan throwing.
			string fileA = WriteFile(dir, "a.mkv", 512);
			string fileB = WriteFile(dir, "b.mkv", 512);
			var index = new LibraryIndex();
			string indexPath = Path.Combine(dir, "lib.vbridx");

			using var scanner = new LibraryScanner();
			var results = new List<LibraryScanner.FileScanResult>();
			LibraryScanner.ScanSummary summary = scanner.Scan(
				new[] { fileA, fileB }, index, indexPath, Profile, forceRescan: false, results.Add);

			Assert.Equal(2, summary.Total);
			Assert.Equal(2, summary.Failed);
			Assert.Equal(2, results.Count);
			Assert.All(results, r => Assert.Equal(LibraryScanner.ScanOutcome.Failed, r.Outcome));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Checkpointing_SavesAfterEveryFile_PlusOnceMoreAtTheEnd() {
		string dir = CreateTempDir();
		try {
			string fileA = WriteFile(dir, "a.mkv", 1024);
			string fileB = WriteFile(dir, "b.mkv", 1024);
			var index = new LibraryIndex();
			index.Entries[LibraryIndexKey.Normalize(fileA)] = CacheEntryMatching(fileA);
			index.Entries[LibraryIndexKey.Normalize(fileB)] = CacheEntryMatching(fileB);
			string indexPath = Path.Combine(dir, "lib.vbridx");

			// A zero interval means "checkpoint after every file" -- both are cache-hits (fast,
			// no real sampling needed), so this stays deterministic and quick.
			using var scanner = new LibraryScanner(checkpointInterval: TimeSpan.Zero);
			int checkpointCalls = 0;
			scanner.Scan(new[] { fileA, fileB }, index, indexPath, Profile, forceRescan: false,
				onCheckpoint: _ => checkpointCalls++);

			Assert.Equal(3, checkpointCalls); // one after each of the 2 files, plus the final save
			Assert.True(File.Exists(indexPath));
		}
		finally { DeleteTempDir(dir); }
	}
}
