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
using MemoryPack;

namespace VBR.Core.Database;

/// <summary>
/// A named library's persisted, cached fingerprint database — docs/iterativeplan.md, "Library scan —
/// cached fingerprint index" (named "database"/"db" in CLI terminology since 2026-07-28's rename —
/// see iterativeplan.md's "CLI terminology & multi-folder libraries" entry). One physical file per
/// named library (decision 13), never VDF's own <c>FileEntry</c>/<c>ScannedFiles.db</c> (decision 2):
/// VDF's <c>grayBytes</c>/<c>PHashes</c> dictionaries are keyed to VDF's own uniform sampling
/// positions, and reusing them for this project's non-uniform edge/sparse samples wouldn't actually
/// feed VDF's own dedup scan (see docs/iterativeplan.md's 2026-07-24 entry for the full reasoning) —
/// so this is a wholly separate store instead.
///
/// <see cref="FormatVersion"/> is carried unconditionally from the first version (decision 14),
/// even though there is nothing to migrate yet — the cost of having it and never needing it is
/// nothing; the cost of not having it and needing it later is a blind compatibility guess against
/// database files already in the wild.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class LibraryDatabase {
	public const int CurrentFormatVersion = 1;

	[MemoryPackOrder(0)]
	public int FormatVersion { get; set; } = CurrentFormatVersion;

	[MemoryPackOrder(1)]
	public string LibraryName { get; set; } = string.Empty;

	/// <summary>The <c>EdgeDensityProfile</c> this database was last scanned under — recorded so a
	/// future default/parameter change can be recognized as making cached entries stale (a
	/// documented open question, not yet enforced: see docs/iterativeplan.md decision 4's note).
	/// Stored as raw seconds rather than a direct <c>EdgeDensityProfile</c> reference so this type
	/// doesn't need to depend on <c>VBR.Core.Fingerprinting</c> for anything but
	/// <see cref="Fingerprinting.TimedFingerprint"/> itself.</summary>
	[MemoryPackOrder(2)]
	public double EdgeBoundarySeconds { get; set; }

	[MemoryPackOrder(3)]
	public double DenseIntervalSeconds { get; set; }

	[MemoryPackOrder(4)]
	public double SparseIntervalSeconds { get; set; }

	/// <summary>Keyed by the entry's own <see cref="LibraryDatabaseEntry.Path"/>. Always look up via
	/// <see cref="LibraryDatabaseKey.Normalize"/> — MemoryPack round-tripping a <c>Dictionary</c> is
	/// not guaranteed to preserve a custom comparer, so case-insensitivity is enforced by
	/// normalizing the key string itself, not by the dictionary's comparer.</summary>
	[MemoryPackOrder(5)]
	public Dictionary<string, LibraryDatabaseEntry> Entries { get; set; } = new();
}

/// <summary>Normalizes a path for use as a <see cref="LibraryDatabase.Entries"/> key — Windows paths
/// are case-insensitive, and a plain <see cref="Dictionary{TKey,TValue}"/>'s comparer is not
/// guaranteed to survive a MemoryPack round-trip, so callers normalize the key itself instead of
/// relying on the dictionary's construction-time comparer.</summary>
public static class LibraryDatabaseKey {
	public static string Normalize(string path) => System.IO.Path.GetFullPath(path).ToUpperInvariant();
}
