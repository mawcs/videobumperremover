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

using MemoryPack;

namespace VBR.Core.Configuration;

/// <summary>
/// The <see cref="FrameQualityConfig"/> values active the last time a <c>BumperCatalog</c>/
/// <c>LibraryDatabase</c> was saved — the recipe-staleness stamp docs/iterativeplan.md's "File-path
/// DB options" entry (Part 3) commits to, and the minimum bar for the standing "Database/catalog
/// fingerprint-recipe staleness has no detection" TODO in PROGRESS.md. Only <c>frameQuality</c> is
/// stamped — see <see cref="VbrConfig"/>'s own doc comment for why <c>sampling</c> deliberately
/// isn't (mismatches there cost thoroughness, never correctness, so warning on them would false-alarm
/// on the normal, correct state).
///
/// <b>Known scope limit, not hidden:</b> this is a whole-*file* stamp, re-captured at every
/// <c>Save</c>, not a per-entry one. A catalog with ten bumpers added under old settings and one
/// just added under current settings reports "current" for the whole file — the ten old entries'
/// staleness isn't caught. Per-entry tracking would close that gap but is real additional scope,
/// deliberately deferred past this minimum bar.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class FrameQualitySnapshot {
	[MemoryPackOrder(0)]
	public double MinDetail { get; set; }

	[MemoryPackOrder(1)]
	public double DarkOverrideDetail { get; set; }

	[MemoryPackOrder(2)]
	public double DarkRejectPercent { get; set; }

	/// <summary>Captures <see cref="VbrConfig.Current"/>'s <c>frameQuality</c> values right now —
	/// call immediately before a <c>Save</c> so the stamp reflects what the save actually used, not
	/// some earlier point in the run.</summary>
	public static FrameQualitySnapshot CaptureCurrent() {
		FrameQualityConfig fq = VbrConfig.Current.FrameQuality;
		return new FrameQualitySnapshot { MinDetail = fq.MinDetail, DarkOverrideDetail = fq.DarkOverrideDetail, DarkRejectPercent = fq.DarkRejectPercent };
	}

	/// <summary>Null when <paramref name="stamped"/>'s own values exactly match the currently active
	/// config (nothing to warn about), or when <paramref name="stamped"/> is itself null (a file
	/// saved before this stamp existed at all — unknown, not provably stale, so this stays silent
	/// rather than false-alarming on every pre-2026-08-12 file). Otherwise, a ready-to-print warning
	/// naming <paramref name="subjectDescription"/> (e.g. <c>"Catalog 'default'"</c>) and exactly
	/// which values drifted.</summary>
	public static string? DescribeMismatchFromCurrent(FrameQualitySnapshot? stamped, string subjectDescription) {
		if (stamped is null) return null;
		FrameQualityConfig fq = VbrConfig.Current.FrameQuality;
		if (stamped.MinDetail == fq.MinDetail && stamped.DarkOverrideDetail == fq.DarkOverrideDetail && stamped.DarkRejectPercent == fq.DarkRejectPercent)
			return null;
		return $"{subjectDescription} was built with different frame-quality settings than are active now " +
			$"(minDetail {stamped.MinDetail:0.###} -> {fq.MinDetail:0.###}, " +
			$"darkOverrideDetail {stamped.DarkOverrideDetail:0.###} -> {fq.DarkOverrideDetail:0.###}, " +
			$"darkRejectPercent {stamped.DarkRejectPercent:0.#} -> {fq.DarkRejectPercent:0.#}) -- " +
			"results may be wrong; re-scan with --rescan / re-add the bumper to refresh it.";
	}
}
