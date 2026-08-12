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
using VBR.Core.Database;
using VBR.Core.Fingerprinting;
using Xunit;

namespace VBR.Tests.Database;

public class LibraryDatabaseStoreTests {
	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_database_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	static LibraryDatabase BuildSampleDatabase() {
		var database = new LibraryDatabase {
			LibraryName = "Sample Library",
			EdgeBoundarySeconds = 20,
			DenseIntervalSeconds = 0.2,
			SparseIntervalSeconds = 4,
		};
		var entry = new LibraryDatabaseEntry {
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
			TombstonedUtc = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
		};
		database.Entries[LibraryDatabaseKey.Normalize(entry.Path)] = entry;
		return database;
	}

	[Fact]
	public void SaveThenLoad_RoundTripsHeaderAndEntries() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "library.vbrdb");
			LibraryDatabase original = BuildSampleDatabase();

			LibraryDatabaseStore.Save(original, path);
			LibraryDatabase loaded = LibraryDatabaseStore.Load(path);

			Assert.Equal(LibraryDatabase.CurrentFormatVersion, loaded.FormatVersion);
			Assert.Equal(original.LibraryName, loaded.LibraryName);
			Assert.Equal(original.EdgeBoundarySeconds, loaded.EdgeBoundarySeconds);
			Assert.Equal(original.DenseIntervalSeconds, loaded.DenseIntervalSeconds);
			Assert.Equal(original.SparseIntervalSeconds, loaded.SparseIntervalSeconds);
			Assert.Single(loaded.Entries);

			LibraryDatabaseEntry originalEntry = Assert.Single(original.Entries.Values);
			LibraryDatabaseEntry loadedEntry = Assert.Single(loaded.Entries.Values);
			Assert.Equal(originalEntry.Path, loadedEntry.Path);
			Assert.Equal(originalEntry.FileSize, loadedEntry.FileSize);
			Assert.Equal(originalEntry.DateCreated, loadedEntry.DateCreated);
			Assert.Equal(originalEntry.DateModified, loadedEntry.DateModified);
			Assert.Equal(originalEntry.OsHash, loadedEntry.OsHash);
			Assert.Equal(originalEntry.Duration, loadedEntry.Duration);
			Assert.Equal(originalEntry.AudioFingerprint, loadedEntry.AudioFingerprint);
			Assert.Equal(originalEntry.TombstonedUtc, loadedEntry.TombstonedUtc);
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
			string path = Path.Combine(dir, "library.vbrdb");
			LibraryDatabaseStore.Save(BuildSampleDatabase(), path);
			Assert.True(File.Exists(path));
			Assert.False(File.Exists(path + ".tmp"));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Save_RetriesThenThrows_WhenTheDestinationIsAnExistingDirectory() {
		string dir = CreateTempDir();
		try {
			// `path` itself being an existing directory (not a file) makes the final File.Move fail
			// every attempt -- exercises Save's retry-then-give-up path deterministically, instead of
			// depending on a real transient antivirus/other-process lock. LibraryScanner is what
			// catches this in the real product (LibraryScannerTests.DatabaseSaveFailure_...); this proves
			// Save itself still surfaces a genuine, non-transient failure rather than hanging or
			// swallowing it.
			string path = Path.Combine(dir, "library.vbrdb");
			Directory.CreateDirectory(path);

			Exception ex = Assert.ThrowsAny<Exception>(() => LibraryDatabaseStore.Save(BuildSampleDatabase(), path));
			Assert.True(ex is IOException or UnauthorizedAccessException,
				$"Expected IOException or UnauthorizedAccessException, got {ex.GetType()}: {ex.Message}");
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_NonexistentFile_ReturnsFreshEmptyDatabase() {
		string dir = CreateTempDir();
		try {
			LibraryDatabase database = LibraryDatabaseStore.Load(Path.Combine(dir, "does-not-exist.vbrdb"));
			Assert.Equal(LibraryDatabase.CurrentFormatVersion, database.FormatVersion);
			Assert.Empty(database.Entries);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_WrongMagicHeader_Throws() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "not-a-database.vbrdb");
			File.WriteAllText(path, "this is not a library database file, just plain text");
			Assert.Throws<InvalidOperationException>(() => LibraryDatabaseStore.Load(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Theory]
	[InlineData(@"D:\Media\TV Shows", "TV Shows")]
	[InlineData(@"D:\Media\TV Shows\", "TV Shows")]
	[InlineData(@"D:\Media\TV Shows/", "TV Shows")]
	public void DeriveLibraryName_UsesLastPathSegment_IgnoringTrailingSeparators(string folder, string expected) {
		Assert.Equal(expected, LibraryDatabaseStore.DeriveLibraryName(folder));
	}

	[Theory]
	[InlineData(@"C:\some\custom-folder\My Library.custom", @"C:\some\custom-folder\My Library.custom")]
	[InlineData(@"C:\some\custom-folder\no-extension", @"C:\some\custom-folder\no-extension")]
	public void ResolvePath_ExplicitPath_UsedVerbatim_AnyOrNoExtension(string explicitPath, string expected) {
		Assert.Equal(expected, LibraryDatabaseStore.ResolvePath(explicitPath));
	}

	[Fact]
	public void ResolvePath_NoExplicitPath_DerivesFromFolder_UnderDedicatedFolder() {
		string resolved = LibraryDatabaseStore.ResolvePath(null, @"D:\Media\My Library");
		string folder = LibraryDatabaseStore.GetDefaultDatabaseFolder();
		Assert.Equal(Path.Combine(folder, "My Library.vbrdb"), resolved);
	}

	[Fact]
	public void ResolvePath_NoExplicitPath_SanitizesInvalidFileNameCharactersInDerivedName() {
		string resolved = LibraryDatabaseStore.ResolvePath(null, @"D:\Media\Colon: Test");
		Assert.DoesNotContain(":", Path.GetFileName(resolved));
	}

	[Fact]
	public void ResolvePath_NeitherExplicitPathNorDeriveFolder_Throws() {
		Assert.Throws<ArgumentException>(() => LibraryDatabaseStore.ResolvePath(null, null));
	}
}
