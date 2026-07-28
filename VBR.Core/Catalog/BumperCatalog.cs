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

using System.Collections.Generic;
using MemoryPack;

namespace VBR.Core.Catalog;

/// <summary>
/// One named library's persisted, curated store of known bumpers (docs/iterativeplan.md, "Bumper
/// catalog"). Per-library, not global (maintainer correction during planning) — mirrors
/// <see cref="Index.LibraryIndex"/>'s shape closely (same MemoryPack `VersionTolerant` convention,
/// same magic-header-checked atomic store) but is a wholly separate file/format: a catalog is keyed
/// by bumper identity, not by file, and has no per-file change-detection concept at all.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class BumperCatalog {
	public const int CurrentFormatVersion = 1;

	[MemoryPackOrder(0)]
	public int FormatVersion { get; set; } = CurrentFormatVersion;

	[MemoryPackOrder(1)]
	public string LibraryName { get; set; } = string.Empty;

	/// <summary>Keyed by <see cref="BumperCatalogEntry.Id"/> — a GUID, so (unlike
	/// <see cref="Index.LibraryIndex.Entries"/>) no normalization step is needed for the key
	/// itself.</summary>
	[MemoryPackOrder(2)]
	public Dictionary<string, BumperCatalogEntry> Entries { get; set; } = new();
}
