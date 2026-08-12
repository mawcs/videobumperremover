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
using System.Threading;
using MemoryPack;

namespace VBR.Core.Database;

/// <summary>
/// Resolves where a named library's database file lives (decision 13 — one physical file per named
/// library, in a dedicated VBR folder by default, both overridable) and loads/saves it. Deliberately
/// simple compared to VDF's own <c>DatabaseUtils</c> (no streaming writer, no legacy-format
/// migration): this project has no installed base of database files yet to be gentle with, and a
/// scanned library's fingerprint set is nowhere near VDF's multi-million-entry whole-drive scale.
/// A leading magic header plus <see cref="LibraryDatabase.FormatVersion"/> inside the payload itself
/// give a real future migration path if one is ever needed, without paying streaming-writer
/// complexity now. Named "database"/"db" rather than "index" since 2026-07-28 — a UX call, not a
/// technical one (see iterativeplan.md's "CLI terminology & multi-folder libraries" entry): "index"
/// is the technically precise term, but tested as confusing to users relative to "database."
/// </summary>
public static class LibraryDatabaseStore {
	static LibraryDatabaseStore() => MemoryPackRegistration.Register();

	const string DatabaseFileExtension = ".vbrdb";
	static ReadOnlySpan<byte> FormatMagic => "VBRDB001"u8;

	/// <summary>Derives a default library name from the folder being scanned — the last path
	/// segment, trailing separators ignored so <c>D:\Media\Show\</c> and <c>D:\Media\Show</c>
	/// agree.</summary>
	public static string DeriveLibraryName(string libraryFolder) {
		string trimmed = libraryFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string name = Path.GetFileName(trimmed);
		return string.IsNullOrEmpty(name) ? trimmed : name;
	}

	/// <summary>Resolves the database file's full path: <paramref name="explicitPath"/> verbatim when
	/// given (callers pass a <see cref="FileInfo"/>'s own <c>.FullName</c>, which already resolves a
	/// relative <c>--library-db</c> against the current directory at construction time — see
	/// docs/iterativeplan.md, "File-path DB options" entry), else derived from
	/// <paramref name="deriveNameFromFolder"/> (typically the first <c>--library</c> folder) via
	/// <see cref="DeriveLibraryName"/>, sanitized, under <see cref="GetDefaultDatabaseFolder"/> — the
	/// no-flag default <c>vbr scan</c> alone still supports (remove/commit's scanned-library mode has
	/// no folder to derive from, so they always pass an explicit path once that mode is selected at
	/// all).</summary>
	/// <exception cref="ArgumentException">Both <paramref name="explicitPath"/> and
	/// <paramref name="deriveNameFromFolder"/> are null/empty — a caller error, not a user-facing
	/// case (every command either has an explicit path in hand or a folder to derive one from).</exception>
	public static string ResolvePath(string? explicitPath, string? deriveNameFromFolder = null) {
		if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
		if (string.IsNullOrWhiteSpace(deriveNameFromFolder))
			throw new ArgumentException("Either an explicit path or a folder to derive a name from is required.", nameof(explicitPath));
		return Path.Combine(GetDefaultDatabaseFolder(), SanitizeFileName(DeriveLibraryName(deriveNameFromFolder)) + DatabaseFileExtension);
	}

	/// <summary>The dedicated VBR-specific folder decision 6 calls for — mirrors
	/// <c>VDF.Core.Utils.CoreUtils.GetDefaultStateFolder</c>'s per-OS resolution algorithm via
	/// <see cref="Configuration.VbrPaths.GetStateRootFolder"/>, rooted at a VBR-specific base instead
	/// of VDF's, so the two projects' persisted state never mixes (decision 2/6). Created if missing.</summary>
	public static string GetDefaultDatabaseFolder() {
		string folder = Path.Combine(Configuration.VbrPaths.GetStateRootFolder(), "database");
		Directory.CreateDirectory(folder);
		return folder;
	}

	static string SanitizeFileName(string name) {
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');
		return name.Length == 0 ? "library" : name;
	}

	/// <summary>Loads the database at <paramref name="path"/>, or a fresh empty one if it doesn't
	/// exist yet — a first scan of a new library is not an error.</summary>
	/// <exception cref="InvalidOperationException">The file exists but isn't a recognized database
	/// (wrong magic header) or deserialized to null (corrupt).</exception>
	public static LibraryDatabase Load(string path) {
		if (!File.Exists(path))
			return new LibraryDatabase();
		byte[] raw = File.ReadAllBytes(path);
		if (raw.Length < FormatMagic.Length || !raw.AsSpan(0, FormatMagic.Length).SequenceEqual(FormatMagic))
			throw new InvalidOperationException($"'{path}' is not a recognized library database file.");
		LibraryDatabase? database = MemoryPackSerializer.Deserialize<LibraryDatabase>(raw.AsSpan(FormatMagic.Length));
		return database ?? throw new InvalidOperationException($"Library database at '{path}' deserialized to null (corrupt file).");
	}

	/// <summary>Writes <paramref name="database"/> to <paramref name="path"/> via a temp-file-then-move
	/// swap, so a crash mid-save leaves either the old file or the new one intact, never a
	/// half-written one — same rationale as VDF's own <c>ScannedFiles_new.db</c> pattern.</summary>
	/// <exception cref="IOException">The rename still fails after retrying (see
	/// <see cref="MoveIntoPlace"/>) — a genuine, non-transient problem with <paramref name="path"/>'s
	/// destination. Callers that scan many files (<see cref="LibraryScanner"/>) must treat this as
	/// recoverable: log it and keep going rather than letting it crash the whole run.</exception>
	public static void Save(LibraryDatabase database, string path) {
		string? dir = Path.GetDirectoryName(path);
		if (dir is { Length: > 0 })
			Directory.CreateDirectory(dir);
		byte[] payload = MemoryPackSerializer.Serialize(database);
		string tempPath = path + ".tmp";
		using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
			file.Write(FormatMagic);
			file.Write(payload);
		}
		MoveIntoPlace(tempPath, path);
	}

	// Config-aware since 2026-08-12 (VbrConfig.Current.Storage) -- shared with
	// BumperCatalogStore's identical retry mechanics, one config key each, not two copies.
	static int MoveRetryAttempts => Configuration.VbrConfig.Current.Storage.SaveRetryAttempts;
	static TimeSpan MoveRetryDelay => TimeSpan.FromMilliseconds(Configuration.VbrConfig.Current.Storage.SaveRetryDelayMilliseconds);

	/// <summary>A file that was just closed is occasionally still briefly held by something else on
	/// Windows — real-time antivirus scanning a freshly-written file is the common real-world case —
	/// so the rename can fail with <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>
	/// even though nothing is actually wrong. A handful of short retries rides out that window; if
	/// the problem isn't transient, the last attempt's exception propagates so the caller can decide
	/// what a genuine, persistent save failure means for the run.</summary>
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
