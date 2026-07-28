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
using System.Runtime.InteropServices;
using System.Threading;
using MemoryPack;

namespace VBR.Core.Catalog;

/// <summary>
/// Resolves where a named bumper catalog lives and loads/saves it. Deliberately mirrors
/// <c>VBR.Core.Index.LibraryIndexStore</c>'s exact mechanics (same MemoryPack `VersionTolerant`
/// convention, same magic-header-checked load, same atomic temp-file-then-move save with retry) —
/// but is its own dedicated store at its own dedicated default folder, not shared with the index.
/// A catalog is named independently of any media folder (post-ship simplification, 2026-07-28 —
/// see <see cref="BumperCatalog"/>'s doc comment), so unlike the index there is no folder argument
/// anywhere in this type at all, only a name.
/// </summary>
public static class BumperCatalogStore {
	static BumperCatalogStore() => CatalogMemoryPackRegistration.Register();

	const string CatalogFileExtension = ".vbrcat";
	static ReadOnlySpan<byte> FormatMagic => "VBRCAT01"u8;

	/// <summary>Resolves the catalog file's full path: always <c>{folder}/{sanitized catalog
	/// name}.vbrcat</c>. <paramref name="explicitFolder"/> is the containing folder when given
	/// (<c>--catalog-db-folder</c>, itself not required to exist yet — created on first save), else
	/// <see cref="GetDefaultCatalogFolder"/>.</summary>
	public static string ResolveCatalogPath(string? explicitFolder, string catalogName) {
		string folder = string.IsNullOrWhiteSpace(explicitFolder) ? GetDefaultCatalogFolder() : explicitFolder;
		return Path.Combine(folder, SanitizeFileName(catalogName) + CatalogFileExtension);
	}

	/// <summary>A dedicated VBR-specific folder, sibling to (not inside) the library index's own —
	/// mirrors <c>LibraryIndexStore.GetDefaultIndexFolder</c>'s per-OS resolution algorithm rooted
	/// at a "catalog" leaf instead of "index" so the two stores never collide. Created if missing.</summary>
	public static string GetDefaultCatalogFolder() {
		string baseFolder;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
			baseFolder = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
		}
		else {
			baseFolder = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
		}
		string folder = Path.Combine(baseFolder, "VideoBumperRemover", "catalog");
		Directory.CreateDirectory(folder);
		return folder;
	}

	static string SanitizeFileName(string name) {
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');
		return name.Length == 0 ? "library" : name;
	}

	/// <summary>Loads the catalog at <paramref name="path"/>, or a fresh empty one if it doesn't
	/// exist yet — adding the first bumper to a new library's catalog is not an error.</summary>
	/// <exception cref="InvalidOperationException">The file exists but isn't a recognized catalog
	/// (wrong magic header) or deserialized to null (corrupt).</exception>
	public static BumperCatalog Load(string path) {
		if (!File.Exists(path))
			return new BumperCatalog();
		byte[] raw = File.ReadAllBytes(path);
		if (raw.Length < FormatMagic.Length || !raw.AsSpan(0, FormatMagic.Length).SequenceEqual(FormatMagic))
			throw new InvalidOperationException($"'{path}' is not a recognized bumper catalog file.");
		BumperCatalog? catalog = MemoryPackSerializer.Deserialize<BumperCatalog>(raw.AsSpan(FormatMagic.Length));
		return catalog ?? throw new InvalidOperationException($"Bumper catalog at '{path}' deserialized to null (corrupt file).");
	}

	/// <summary>Writes <paramref name="catalog"/> to <paramref name="path"/> via a temp-file-then-
	/// move swap plus retry (identical mechanics to <c>LibraryIndexStore.Save</c>, including the
	/// retry-on-transient-lock rationale — see that method's doc comment).</summary>
	/// <exception cref="IOException">The rename still fails after retrying — a genuine,
	/// non-transient problem with <paramref name="path"/>'s destination.</exception>
	public static void Save(BumperCatalog catalog, string path) {
		string? dir = Path.GetDirectoryName(path);
		if (dir is { Length: > 0 })
			Directory.CreateDirectory(dir);
		byte[] payload = MemoryPackSerializer.Serialize(catalog);
		string tempPath = path + ".tmp";
		using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
			file.Write(FormatMagic);
			file.Write(payload);
		}
		MoveIntoPlace(tempPath, path);
	}

	const int MoveRetryAttempts = 4;
	static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(150);

	/// <summary>Same transient-lock retry as <c>LibraryIndexStore</c>'s own <c>MoveIntoPlace</c> —
	/// see that method's doc comment for the full rationale (real-time antivirus on a freshly
	/// written file being the common real-world trigger on Windows).</summary>
	static void MoveIntoPlace(string tempPath, string path) {
		for (int attempt = 1; ; attempt++) {
			try {
				File.Move(tempPath, path, overwrite: true);
				return;
			}
			catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < MoveRetryAttempts) {
				Thread.Sleep(MoveRetryDelay);
			}
		}
	}
}
