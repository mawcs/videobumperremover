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
using System.IO;
using VBR.Core.Fingerprinting;
using VBR.Core.Index;
using Xunit;

namespace VBR.Tests.Index;

public class LibraryIndexStoreTests {
	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_index_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	static LibraryIndex BuildSampleIndex() {
		var index = new LibraryIndex {
			LibraryName = "Sample Library",
			EdgeBoundarySeconds = 20,
			DenseIntervalSeconds = 0.2,
			SparseIntervalSeconds = 4,
		};
		var entry = new LibraryIndexEntry {
			Path = @"D:\Media\Show\S01E01.mkv",
			FileSize = 123_456_789,
			DateCreated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
			DateModified = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
			OsHash = "deadbeefcafef00d",
			Duration = TimeSpan.FromMinutes(42),
			AudioFingerprint = new uint[] { 1, 2, 3, 4, 5 },
			Fingerprints = new[] {
				new TimedFingerprint(0.0, new byte[] { 1, 2, 3 }, 0xAAAAAAAAAAAAAAAA),
				new TimedFingerprint(0.2, new byte[] { 4, 5, 6 }, 0xBBBBBBBBBBBBBBBB),
				new TimedFingerprint(20.0, new byte[] { 7, 8, 9 }, 0xCCCCCCCCCCCCCCCC),
			},
		};
		index.Entries[LibraryIndexKey.Normalize(entry.Path)] = entry;
		return index;
	}

	[Fact]
	public void SaveThenLoad_RoundTripsHeaderAndEntries() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "library.vbridx");
			LibraryIndex original = BuildSampleIndex();

			LibraryIndexStore.Save(original, path);
			LibraryIndex loaded = LibraryIndexStore.Load(path);

			Assert.Equal(LibraryIndex.CurrentFormatVersion, loaded.FormatVersion);
			Assert.Equal(original.LibraryName, loaded.LibraryName);
			Assert.Equal(original.EdgeBoundarySeconds, loaded.EdgeBoundarySeconds);
			Assert.Equal(original.DenseIntervalSeconds, loaded.DenseIntervalSeconds);
			Assert.Equal(original.SparseIntervalSeconds, loaded.SparseIntervalSeconds);
			Assert.Single(loaded.Entries);

			LibraryIndexEntry originalEntry = Assert.Single(original.Entries.Values);
			LibraryIndexEntry loadedEntry = Assert.Single(loaded.Entries.Values);
			Assert.Equal(originalEntry.Path, loadedEntry.Path);
			Assert.Equal(originalEntry.FileSize, loadedEntry.FileSize);
			Assert.Equal(originalEntry.DateCreated, loadedEntry.DateCreated);
			Assert.Equal(originalEntry.DateModified, loadedEntry.DateModified);
			Assert.Equal(originalEntry.OsHash, loadedEntry.OsHash);
			Assert.Equal(originalEntry.Duration, loadedEntry.Duration);
			Assert.Equal(originalEntry.AudioFingerprint, loadedEntry.AudioFingerprint);
			Assert.Equal(originalEntry.Fingerprints.Length, loadedEntry.Fingerprints.Length);
			for (int i = 0; i < originalEntry.Fingerprints.Length; i++) {
				Assert.Equal(originalEntry.Fingerprints[i].TimestampSeconds, loadedEntry.Fingerprints[i].TimestampSeconds);
				Assert.Equal(originalEntry.Fingerprints[i].Embedding, loadedEntry.Fingerprints[i].Embedding);
				Assert.Equal(originalEntry.Fingerprints[i].PHash, loadedEntry.Fingerprints[i].PHash);
			}
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Save_UsesAtomicSwap_NoLeftoverTempFile() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "library.vbridx");
			LibraryIndexStore.Save(BuildSampleIndex(), path);
			Assert.True(File.Exists(path));
			Assert.False(File.Exists(path + ".tmp"));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_NonexistentFile_ReturnsFreshEmptyIndex() {
		string dir = CreateTempDir();
		try {
			LibraryIndex index = LibraryIndexStore.Load(Path.Combine(dir, "does-not-exist.vbridx"));
			Assert.Equal(LibraryIndex.CurrentFormatVersion, index.FormatVersion);
			Assert.Empty(index.Entries);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_WrongMagicHeader_Throws() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "not-an-index.vbridx");
			File.WriteAllText(path, "this is not a library index file, just plain text");
			Assert.Throws<InvalidOperationException>(() => LibraryIndexStore.Load(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Theory]
	[InlineData(@"D:\Media\TV Shows", "TV Shows")]
	[InlineData(@"D:\Media\TV Shows\", "TV Shows")]
	[InlineData(@"D:\Media\TV Shows/", "TV Shows")]
	public void DeriveLibraryName_UsesLastPathSegment_IgnoringTrailingSeparators(string folder, string expected) {
		Assert.Equal(expected, LibraryIndexStore.DeriveLibraryName(folder));
	}

	[Fact]
	public void ResolveIndexPath_ExplicitPath_UsedVerbatim() {
		string explicitPath = Path.Combine(Path.GetTempPath(), "my-custom-index.vbridx");
		Assert.Equal(explicitPath, LibraryIndexStore.ResolveIndexPath(explicitPath, "Whatever"));
	}

	[Fact]
	public void ResolveIndexPath_NoExplicitPath_DefaultsUnderDedicatedFolder_NamedAfterLibrary() {
		string resolved = LibraryIndexStore.ResolveIndexPath(null, "My Library");
		string folder = LibraryIndexStore.GetDefaultIndexFolder();
		Assert.Equal(Path.Combine(folder, "My Library.vbridx"), resolved);
	}

	[Fact]
	public void ResolveIndexPath_SanitizesInvalidFileNameCharactersInLibraryName() {
		string resolved = LibraryIndexStore.ResolveIndexPath(null, "Colon: Test");
		Assert.DoesNotContain(":", Path.GetFileName(resolved));
	}
}
