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
using VDF.Core;
using VDF.Core.AI;

namespace VBR.Core.Fingerprinting;

/// <summary>
/// Low-information frame filtering for the visual matcher — the real implementation of the
/// matcher-spec's "do not match on black/silence" rule (§1; see the 2026-07-18 correction in
/// docs/design/matcher-spec.md: the ported "skip empty/black frames" guard was dead code).
/// Low-information frames embed near-identically regardless of content (cosine 0.87–0.97
/// between unrelated near-black frames, and equally between blank-white ones), so a single such
/// frame on both sides fabricates a high-confidence match. Filtering applies to BOTH the
/// reference clip and each candidate's search window.
/// </summary>
public static class FrameQuality {
	/// <summary>
	/// Minimum mean absolute horizontal luma delta (0–255 scale) for a frame to count as
	/// carrying distinctive detail. Calibrated on real frames (2026-07-18, 0.2s full-decode
	/// grids of the Daredevil Netflix ident + Doctor Who/Avatar begin windows + Daredevil end
	/// credits): blank-white ident background 0.55–0.68, fade frames ≤0.95 — versus the ident's
	/// letter animation 1.33–1.97, dark-but-real scene content 1.46+, bright cards ≥3. 1.0 sits
	/// mid-gap. Smooth gradients (vignettes) score far below it; any frame with actual edges
	/// scores above.
	/// </summary>
	public const double MinDetail = 1.0;

	/// <summary>
	/// Marks which sampled frames may participate in matching. Combines VDF's own guards for
	/// its AI-partial pass (<c>ScanEngine.SelectUsableDenseFrames</c>: the ≥80%-dark-pixels
	/// rejection and the byte-identical-duplicate drop — static held cards decode bit-identical
	/// and would multiply one coincidental hit into an evidence quorum) with the
	/// <see cref="MinDetail"/> near-uniform rejection those guards lack (a blank-white frame is
	/// not dark, but carries no identity either). Excluded slots stay on the timeline so the
	/// frame-index ↔ time mapping holds.
	/// </summary>
	public static bool[] SelectUsable(byte[][] frames) {
		bool[] usable = ScanEngine.SelectUsableDenseFrames(frames);
		for (int f = 0; f < frames.Length; f++)
			if (usable[f] && MeasureDetail(frames[f]) < MinDetail)
				usable[f] = false;
		return usable;
	}

	/// <summary>Mean absolute luma difference between horizontally adjacent pixels of a
	/// 224×224 RGB24 frame — a cheap edge-energy measure. Near-uniform frames (solid color,
	/// smooth vignette) score ≈0; frames with text/logo/scene edges score well above
	/// <see cref="MinDetail"/>.</summary>
	public static double MeasureDetail(ReadOnlySpan<byte> rgb24) {
		int side = OnnxEmbedder.InputSide;
		if (rgb24.Length != side * side * 3)
			throw new ArgumentException($"Expected a {side}x{side} RGB24 frame ({side * side * 3} bytes), got {rgb24.Length}.", nameof(rgb24));
		long deltaSum = 0;
		int i = 0;
		for (int y = 0; y < side; y++) {
			int previous = 0;
			for (int x = 0; x < side; x++, i += 3) {
				int luma = (299 * rgb24[i] + 587 * rgb24[i + 1] + 114 * rgb24[i + 2]) / 1000;
				if (x > 0)
					deltaSum += Math.Abs(luma - previous);
				previous = luma;
			}
		}
		return (double)deltaSum / (side * (side - 1));
	}
}
