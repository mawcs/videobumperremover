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

namespace VBR.Core.Configuration;

/// <summary>
/// The fingerprint-recipe config values active the last time a <c>BumperCatalog</c>/
/// <c>LibraryDatabase</c>/<c>BumperCatalogEntry</c> was saved — the recipe-staleness stamp
/// docs/iterativeplan.md's "File-path DB options" entry (Part 3) commits to, and the minimum bar for
/// the standing "Database/catalog fingerprint-recipe staleness has no detection" TODO in
/// PROGRESS.md. Covers <c>frameQuality</c> (gates which frames get embedded) and, since 2026-08-14,
/// <c>audio.bucketSeconds</c> (changes what each stored audio-fingerprint element encodes — see
/// <see cref="VbrConfig"/>'s own doc comment on why both are true recipes) — kept as one type/one
/// stamp rather than two, since both answer the same question ("was this built with settings that
/// still match now?") and every call site already threads exactly one of these through. The name
/// stayed <c>FrameQualitySnapshot</c> despite now covering audio too, to avoid a mechanical rename
/// across the ten-plus call sites already using it — see <see cref="VbrConfig"/>'s doc comment for
/// the up-to-date scope. <see cref="VbrConfig"/>'s own doc comment covers why <c>sampling</c>
/// deliberately isn't stamped (mismatches there cost thoroughness, never correctness).
///
/// <b>Known scope limit, not hidden:</b> whole-*file* stamps (<c>BumperCatalog</c>/
/// <c>LibraryDatabase</c>) are re-captured at every <c>Save</c>; <c>BumperCatalogEntry</c>'s own
/// per-entry stamp (2026-08-13) closes the gap this used to have for a catalog with some entries
/// added under old settings and some under current ones.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class FrameQualitySnapshot {
	[MemoryPackOrder(0)]
	public double MinDetail { get; set; }

	[MemoryPackOrder(1)]
	public double DarkOverrideDetail { get; set; }

	[MemoryPackOrder(2)]
	public double DarkRejectPercent { get; set; }

	/// <summary>Zero for any snapshot saved before this field existed (MemoryPack's version-tolerant
	/// default for a field the writer never set) — treated as "unknown, not provably stale" in
	/// <see cref="DescribeMismatchFromCurrent"/>, the same convention a wholly-null <paramref
	/// name="stamped"/> already gets, since <see cref="VbrConfig.AudioConfig.BucketSeconds"/> is
	/// validated <c>&gt; 0</c> and can therefore never be a legitimately-captured value.</summary>
	[MemoryPackOrder(3)]
	public double AudioBucketSeconds { get; set; }

	/// <summary>Captures <see cref="VbrConfig.Current"/>'s fingerprint-recipe values right now —
	/// call immediately before a <c>Save</c> (or an <c>add-bumper</c> entry build) so the stamp
	/// reflects what actually got used, not some earlier point in the run.</summary>
	public static FrameQualitySnapshot CaptureCurrent() {
		FrameQualityConfig fq = VbrConfig.Current.FrameQuality;
		return new FrameQualitySnapshot {
			MinDetail = fq.MinDetail, DarkOverrideDetail = fq.DarkOverrideDetail, DarkRejectPercent = fq.DarkRejectPercent,
			AudioBucketSeconds = VbrConfig.Current.Audio.BucketSeconds,
		};
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
		double currentBucketSeconds = VbrConfig.Current.Audio.BucketSeconds;
		bool audioBucketChanged = stamped.AudioBucketSeconds > 0 && stamped.AudioBucketSeconds != currentBucketSeconds;
		bool frameQualityChanged = stamped.MinDetail != fq.MinDetail || stamped.DarkOverrideDetail != fq.DarkOverrideDetail
			|| stamped.DarkRejectPercent != fq.DarkRejectPercent;
		if (!frameQualityChanged && !audioBucketChanged)
			return null;

		var parts = new List<string>(4);
		if (frameQualityChanged) {
			parts.Add($"minDetail {stamped.MinDetail:0.###} -> {fq.MinDetail:0.###}");
			parts.Add($"darkOverrideDetail {stamped.DarkOverrideDetail:0.###} -> {fq.DarkOverrideDetail:0.###}");
			parts.Add($"darkRejectPercent {stamped.DarkRejectPercent:0.#} -> {fq.DarkRejectPercent:0.#}");
		}
		if (audioBucketChanged)
			parts.Add($"audio.bucketSeconds {stamped.AudioBucketSeconds:0.###} -> {currentBucketSeconds:0.###}");
		return $"{subjectDescription} was built with different fingerprint-recipe settings than are active now " +
			$"({string.Join(", ", parts)}) -- results may be wrong; re-scan with --rescan / re-add the bumper to refresh it.";
	}
}
