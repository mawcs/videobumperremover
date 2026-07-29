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
using MemoryPack;
using VBR.Core.Fingerprinting;

namespace VBR.Core.Database;

/// <summary>
/// One scanned video file's cached fingerprints — the persisted counterpart to a
/// <see cref="WholeFileSampler.Result"/>, plus the change-detection fields
/// <see cref="LibraryScanner"/> needs to decide whether a rescan can reuse it (mirrors the fields
/// VDF's own <c>FileEntry</c>/<c>ScanEngine.RefreshExistingEntry</c> compare — see that method's
/// doc comment for the exact reasoning this mirrors).
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class LibraryDatabaseEntry {
	[MemoryPackOrder(0)]
	public string Path { get; set; } = string.Empty;

	[MemoryPackOrder(1)]
	public long FileSize { get; set; }

	[MemoryPackOrder(2)]
	public DateTime DateCreated { get; set; }

	[MemoryPackOrder(3)]
	public DateTime DateModified { get; set; }

	/// <summary>OpenSubtitles-style content hash (<see cref="VDF.Core.Utils.OsHashUtils"/>) — null
	/// when it couldn't be computed (missing/locked/too-small file); such entries always re-sample
	/// on the next scan rather than risk trusting stale data (same rule VDF's own rescan follows).</summary>
	[MemoryPackOrder(4)]
	public string? OsHash { get; set; }

	[MemoryPackOrder(5)]
	public TimeSpan Duration { get; set; }

	/// <summary>Chromaprint blocks for the whole file (~1s each) — self-contained, not read from or
	/// written to VDF's own <c>FileEntry.AudioFingerprint</c> (see <see cref="LibraryDatabase"/>'s doc
	/// comment for why this store never touches VDF's scan data). Null when the file has no usable
	/// audio track.</summary>
	[MemoryPackOrder(6)]
	public uint[]? AudioFingerprint { get; set; }

	/// <summary>The merged, timestamp-sorted dense-edge + sparse-whole-file fingerprint set from
	/// <see cref="WholeFileSampler"/>.</summary>
	[MemoryPackOrder(7)]
	public TimedFingerprint[] Fingerprints { get; set; } = Array.Empty<TimedFingerprint>();

	/// <summary>Non-null once a scan found this entry's file missing on disk — the UTC time that
	/// scan ran (VDF-style tombstoning, adopted 2026-07-29; see iterativeplan.md's "CLI terminology
	/// & multi-folder libraries" entry). The entry, fingerprints included, is deliberately kept
	/// rather than removed: if the same content ever reappears at a new path (e.g. a re-download),
	/// there's still something to re-link against, instead of a full re-sample. Cleared back to null
	/// automatically the moment a scan finds the file present again at *this* entry's own
	/// <see cref="Path"/> — re-linking to a *different* path once tombstoned is still deferred,
	/// unbuilt (<see cref="LibraryScanner"/> only clears/sets this; nothing reads it yet).</summary>
	[MemoryPackOrder(8)]
	public DateTime? TombstonedUtc { get; set; }
}
