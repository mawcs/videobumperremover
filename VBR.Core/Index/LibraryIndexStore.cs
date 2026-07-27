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

namespace VBR.Core.Index;

/// <summary>
/// Resolves where a named library's index file lives (decision 13 — one physical file per named
/// library, in a dedicated VBR folder by default, both overridable) and loads/saves it. Deliberately
/// simple compared to VDF's own <c>DatabaseUtils</c> (no streaming writer, no legacy-format
/// migration): this project has no installed base of index files yet to be gentle with, and a
/// scanned library's fingerprint set is nowhere near VDF's multi-million-entry whole-drive scale.
/// A leading magic header plus <see cref="LibraryIndex.FormatVersion"/> inside the payload itself
/// give a real future migration path if one is ever needed, without paying streaming-writer
/// complexity now.
/// </summary>
public static class LibraryIndexStore {
	static LibraryIndexStore() => MemoryPackRegistration.Register();

	const string IndexFileExtension = ".vbridx";
	static ReadOnlySpan<byte> FormatMagic => "VBRIDX01"u8;

	/// <summary>Derives a default library name from the folder being scanned — the last path
	/// segment, trailing separators ignored so <c>D:\Media\Show\</c> and <c>D:\Media\Show</c>
	/// agree.</summary>
	public static string DeriveLibraryName(string libraryFolder) {
		string trimmed = libraryFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string name = Path.GetFileName(trimmed);
		return string.IsNullOrEmpty(name) ? trimmed : name;
	}

	/// <summary>Resolves the index file's full path: always <c>{folder}/{sanitized library
	/// name}.vbridx</c> — the file's name is derived from <paramref name="libraryName"/> alone and
	/// is never independently specified, so there is exactly one thing to keep in sync between a
	/// library and its index. <paramref name="explicitFolder"/> is the containing folder when given
	/// (decision 13's <c>--index-folder</c> override, itself not required to exist yet — same as any
	/// other output folder, it's created on first save), else <see cref="GetDefaultIndexFolder"/>.
	/// <c>Path.Combine</c> handles a trailing separator on <paramref name="explicitFolder"/> either
	/// way, so callers don't need to normalize it first.</summary>
	public static string ResolveIndexPath(string? explicitFolder, string libraryName) {
		string folder = string.IsNullOrWhiteSpace(explicitFolder) ? GetDefaultIndexFolder() : explicitFolder;
		return Path.Combine(folder, SanitizeFileName(libraryName) + IndexFileExtension);
	}

	/// <summary>The dedicated VBR-specific folder decision 6 calls for — mirrors
	/// <c>VDF.Core.Utils.CoreUtils.GetDefaultStateFolder</c>'s per-OS resolution algorithm, rooted
	/// at a VBR-specific base instead of VDF's, so the two projects' persisted state never mixes
	/// (decision 2/6). Created if missing.</summary>
	public static string GetDefaultIndexFolder() {
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
		string folder = Path.Combine(baseFolder, "VideoBumperRemover", "index");
		Directory.CreateDirectory(folder);
		return folder;
	}

	static string SanitizeFileName(string name) {
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');
		return name.Length == 0 ? "library" : name;
	}

	/// <summary>Loads the index at <paramref name="path"/>, or a fresh empty one if it doesn't
	/// exist yet — a first scan of a new library is not an error.</summary>
	/// <exception cref="InvalidOperationException">The file exists but isn't a recognized index
	/// (wrong magic header) or deserialized to null (corrupt).</exception>
	public static LibraryIndex Load(string path) {
		if (!File.Exists(path))
			return new LibraryIndex();
		byte[] raw = File.ReadAllBytes(path);
		if (raw.Length < FormatMagic.Length || !raw.AsSpan(0, FormatMagic.Length).SequenceEqual(FormatMagic))
			throw new InvalidOperationException($"'{path}' is not a recognized library index file.");
		LibraryIndex? index = MemoryPackSerializer.Deserialize<LibraryIndex>(raw.AsSpan(FormatMagic.Length));
		return index ?? throw new InvalidOperationException($"Library index at '{path}' deserialized to null (corrupt file).");
	}

	/// <summary>Writes <paramref name="index"/> to <paramref name="path"/> via a temp-file-then-move
	/// swap, so a crash mid-save leaves either the old file or the new one intact, never a
	/// half-written one — same rationale as VDF's own <c>ScannedFiles_new.db</c> pattern.</summary>
	/// <exception cref="IOException">The rename still fails after retrying (see
	/// <see cref="MoveIntoPlace"/>) — a genuine, non-transient problem with <paramref name="path"/>'s
	/// destination. Callers that scan many files (<see cref="LibraryScanner"/>) must treat this as
	/// recoverable: log it and keep going rather than letting it crash the whole run.</exception>
	public static void Save(LibraryIndex index, string path) {
		string? dir = Path.GetDirectoryName(path);
		if (dir is { Length: > 0 })
			Directory.CreateDirectory(dir);
		byte[] payload = MemoryPackSerializer.Serialize(index);
		string tempPath = path + ".tmp";
		using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
			file.Write(FormatMagic);
			file.Write(payload);
		}
		MoveIntoPlace(tempPath, path);
	}

	const int MoveRetryAttempts = 4;
	static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(150);

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
