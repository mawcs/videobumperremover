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
using VBR.Core.Catalog;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Index;
using Xunit;

namespace VBR.Tests.Catalog;

public class BumperCatalogStoreTests {
	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_catalog_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	static BumperCatalog BuildSampleCatalog() {
		var catalog = new BumperCatalog { LibraryName = "Sample Library" };
		var entry = new BumperCatalogEntry {
			Id = Guid.NewGuid().ToString("N"),
			Label = "Disney FBI warning 2003",
			Description = "Standard-def DVD ident, black background.",
			Tags = new[] { "disney", "fbi-warning" },
			Region = ClipEdge.begin,
			Status = "active",
			Duration = TimeSpan.FromSeconds(5.023),
			Fingerprints = new[] {
				new TimedFingerprint(0.0, new byte[] { 1, 2, 3 }, 0xAAAAAAAAAAAAAAAA),
				new TimedFingerprint(0.2, new byte[] { 4, 5, 6 }, 0xBBBBBBBBBBBBBBBB),
			},
			AudioFingerprint = new uint[] { 1, 2, 3, 4, 5 },
			ReferenceClipPath = Path.Combine("clips", "sample.mkv"),
			Thumbnail = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 },
			SourceVideoPath = @"D:\Media\Show\S01E01.mkv",
			DateAdded = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
			OccurrenceCount = 0,
		};
		catalog.Entries[entry.Id] = entry;
		return catalog;
	}

	[Fact]
	public void SaveThenLoad_RoundTripsHeaderAndEntries() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "library.vbrcat");
			BumperCatalog original = BuildSampleCatalog();

			BumperCatalogStore.Save(original, path);
			BumperCatalog loaded = BumperCatalogStore.Load(path);

			Assert.Equal(BumperCatalog.CurrentFormatVersion, loaded.FormatVersion);
			Assert.Equal(original.LibraryName, loaded.LibraryName);
			Assert.Single(loaded.Entries);

			BumperCatalogEntry originalEntry = Assert.Single(original.Entries.Values);
			BumperCatalogEntry loadedEntry = Assert.Single(loaded.Entries.Values);
			Assert.Equal(originalEntry.Id, loadedEntry.Id);
			Assert.Equal(originalEntry.Label, loadedEntry.Label);
			Assert.Equal(originalEntry.Description, loadedEntry.Description);
			Assert.Equal(originalEntry.Tags, loadedEntry.Tags);
			Assert.Equal(originalEntry.Region, loadedEntry.Region);
			Assert.Equal(originalEntry.Status, loadedEntry.Status);
			Assert.Equal(originalEntry.Duration, loadedEntry.Duration);
			Assert.Equal(originalEntry.AudioFingerprint, loadedEntry.AudioFingerprint);
			Assert.Equal(originalEntry.ReferenceClipPath, loadedEntry.ReferenceClipPath);
			Assert.Equal(originalEntry.Thumbnail, loadedEntry.Thumbnail);
			Assert.Equal(originalEntry.SourceVideoPath, loadedEntry.SourceVideoPath);
			Assert.Equal(originalEntry.DateAdded, loadedEntry.DateAdded);
			Assert.Equal(originalEntry.OccurrenceCount, loadedEntry.OccurrenceCount);
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
			string path = Path.Combine(dir, "library.vbrcat");
			BumperCatalogStore.Save(BuildSampleCatalog(), path);
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
			// every attempt -- exercises Save's retry-then-give-up path deterministically, same
			// mechanism (and same reason to test it) as LibraryIndexStoreTests' equivalent.
			string path = Path.Combine(dir, "library.vbrcat");
			Directory.CreateDirectory(path);

			Exception ex = Assert.ThrowsAny<Exception>(() => BumperCatalogStore.Save(BuildSampleCatalog(), path));
			Assert.True(ex is IOException or UnauthorizedAccessException,
				$"Expected IOException or UnauthorizedAccessException, got {ex.GetType()}: {ex.Message}");
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_NonexistentFile_ReturnsFreshEmptyCatalog() {
		string dir = CreateTempDir();
		try {
			BumperCatalog catalog = BumperCatalogStore.Load(Path.Combine(dir, "does-not-exist.vbrcat"));
			Assert.Equal(BumperCatalog.CurrentFormatVersion, catalog.FormatVersion);
			Assert.Empty(catalog.Entries);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_WrongMagicHeader_Throws() {
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "not-a-catalog.vbrcat");
			File.WriteAllText(path, "this is not a bumper catalog file, just plain text");
			Assert.Throws<InvalidOperationException>(() => BumperCatalogStore.Load(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_IndexFileGivenAsCatalog_Throws() {
		// Cross-check that the two stores' magic headers actually differ -- an index file must never
		// silently load as a catalog (or vice versa).
		string dir = CreateTempDir();
		try {
			string path = Path.Combine(dir, "confused.vbrcat");
			File.WriteAllBytes(path, "VBRIDX01"u8.ToArray());
			Assert.Throws<InvalidOperationException>(() => BumperCatalogStore.Load(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Theory]
	[InlineData(@"C:\some\custom-folder", @"C:\some\custom-folder\My Library.vbrcat")]
	[InlineData(@"C:\some\custom-folder\", @"C:\some\custom-folder\My Library.vbrcat")]
	public void ResolveCatalogPath_ExplicitFolder_FileNameAlwaysDerivedFromLibraryName(string explicitFolder, string expected) {
		Assert.Equal(expected, BumperCatalogStore.ResolveCatalogPath(explicitFolder, "My Library"));
	}

	[Fact]
	public void ResolveCatalogPath_NoExplicitPath_DefaultsUnderDedicatedFolder_NamedAfterLibrary() {
		string resolved = BumperCatalogStore.ResolveCatalogPath(null, "My Library");
		string folder = BumperCatalogStore.GetDefaultCatalogFolder();
		Assert.Equal(Path.Combine(folder, "My Library.vbrcat"), resolved);
	}

	[Fact]
	public void ResolveCatalogPath_SanitizesInvalidFileNameCharactersInLibraryName() {
		string resolved = BumperCatalogStore.ResolveCatalogPath(null, "Colon: Test");
		Assert.DoesNotContain(":", Path.GetFileName(resolved));
	}

	[Fact]
	public void DefaultCatalogFolder_IsSiblingOfDefaultIndexFolder_NotInsideIt() {
		string catalogFolder = BumperCatalogStore.GetDefaultCatalogFolder();
		string indexFolder = LibraryIndexStore.GetDefaultIndexFolder();
		Assert.NotEqual(indexFolder, catalogFolder);
		Assert.Equal(Path.GetDirectoryName(indexFolder), Path.GetDirectoryName(catalogFolder));
	}
}
