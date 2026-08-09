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
	/// How much detail a majority-dark frame (<see cref="MeasureDarkPercent"/> ≥
	/// <see cref="DarkRejectPercent"/>) needs before the dark-pixel veto is waived — deliberately a
	/// higher bar than <see cref="MinDetail"/>, not the same one (docs/iterativeplan.md, 2026-08-07,
	/// "dark-pixel veto deferring to detail"). <b>A starting, conservative choice, not yet
	/// empirically validated against a real false-positive matrix</b> — see
	/// <c>FrameQualityFalsePositiveTests</c> for what has been checked so far. Grounded in real
	/// dogfooding data (2026-08-07): genuine bright text/logo on a dark field scored 4.2–4.6 across
	/// multiple real static and motion bumpers that were being wrongly rejected; <see cref="MinDetail"/>'s
	/// own original 2026-07-18 calibration separately recorded "dark-but-real scene content" at
	/// 1.46+. 2.0 sits with real margin below the observed legitimate values and above that older
	/// reference point — deliberately not reusing <see cref="MinDetail"/>'s exact 1.0 line, since a
	/// majority-dark frame has less benefit of the doubt than a normally-lit one.
	/// </summary>
	public const double DarkOverrideDetail = 2.0;

	/// <summary>Dark-pixel-percentage rejection line — mirrors VDF's own
	/// <c>GrayBytesUtils.VerifyRgbFrameValues</c> convention exactly (a frame is majority-dark at or
	/// above this).</summary>
	public const double DarkRejectPercent = 80.0;

	/// <summary>
	/// Marks which sampled frames may participate in matching. Three checks, none of them deferring
	/// to VDF's own <c>ScanEngine.SelectUsableDenseFrames</c> (docs/iterativeplan.md, 2026-08-07 —
	/// that method is shared with VDF's own unrelated whole-file dedup scan; VBR's bumper-detection
	/// filtering needs its own rules without risking changing that other feature's behavior):
	/// <list type="bullet">
	/// <item>Byte-identical to the immediately preceding frame → always rejected, regardless of its
	/// own detail score. A real duplicate adds no new evidence beyond the first occurrence — this
	/// protects the 2026-07-18 fix (duplicated frames must never multiply one coincidental hit into
	/// a false evidence quorum), independent of the darkness/detail question below.</item>
	/// <item>Majority-dark (<see cref="MeasureDarkPercent"/> ≥ <see cref="DarkRejectPercent"/>) →
	/// rejected *unless* <see cref="MeasureDetail"/> clears the higher <see cref="DarkOverrideDetail"/>
	/// bar. Live dogfooding (2026-08-07) found real static AND motion bumpers — bright text/logo on
	/// a dark field — being wrongly rejected here despite detail scores 4x <see cref="MinDetail"/>:
	/// the original assumption that "majority-dark implies low detail" doesn't hold for that content
	/// shape, which is common and legitimate, not degenerate.</item>
	/// <item>Otherwise (not majority-dark) → rejected below <see cref="MinDetail"/>, unchanged from
	/// before (a blank-white ident background is bright, not dark, but still carries no identity).</item>
	/// </list>
	/// Excluded slots stay on the timeline so the frame-index ↔ time mapping holds.
	/// </summary>
	public static bool[] SelectUsable(byte[][] frames) {
		int expectedLength = OnnxEmbedder.InputSide * OnnxEmbedder.InputSide * 3;
		var usable = new bool[frames.Length];
		for (int f = 0; f < frames.Length; f++) {
			if (frames[f].Length != expectedLength)
				continue; // malformed -- never expected from DenseFrameSampler, but never trust blindly
			if (f > 0 && frames[f].AsSpan().SequenceEqual(frames[f - 1]))
				continue;
			double dark = MeasureDarkPercent(frames[f]);
			double detail = MeasureDetail(frames[f]);
			double requiredDetail = dark >= DarkRejectPercent ? DarkOverrideDetail : MinDetail;
			usable[f] = detail >= requiredDetail;
		}
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

	// Mirrors VDF.Core.Utils.GrayBytesUtils.VerifyRgbFrameValues's exact dark-pixel definition
	// (a pixel counts as dark only when R, G, AND B are all <= this limit) -- duplicated rather
	// than shared because that constant is private inside an internal VDF.Core class, not worth
	// exposing just for this diagnostic. Keep in sync if VDF's own value ever changes.
	const byte BlackPixelLimit = 0x20;

	/// <summary>Percentage of pixels in a 224×224 RGB24 frame that are "dark" by
	/// <see cref="VDF.Core.Utils.GrayBytesUtils.VerifyRgbFrameValues"/>'s own definition (every
	/// channel <c>&lt;= 0x20</c>) — the same check that rejects a frame at ≥80%. Exists purely to
	/// surface the actual number for diagnostics (docs/iterativeplan.md, 2026-08-07): that method
	/// itself only returns a pass/fail bool, not how close a frame was to the line.</summary>
	public static double MeasureDarkPercent(ReadOnlySpan<byte> rgb24) {
		int pixels = rgb24.Length / 3;
		if (pixels == 0) return 0;
		int dark = 0;
		for (int i = 0; i + 2 < rgb24.Length; i += 3)
			if (rgb24[i] <= BlackPixelLimit && rgb24[i + 1] <= BlackPixelLimit && rgb24[i + 2] <= BlackPixelLimit)
				dark++;
		return 100.0 * dark / pixels;
	}

	/// <summary>One frame's full breakdown against every check <see cref="SelectUsable"/> applies
	/// — which one(s) actually caused <see cref="Usable"/> to be false, not just that it was.
	/// Purely informational: nothing reads this to make a decision (docs/iterativeplan.md,
	/// 2026-08-07 — the direct successor to the "--static suggestion" heuristic that got removed
	/// for guessing wrong twice; this reports facts instead of trying to infer a verdict from
	/// them).</summary>
	public readonly record struct FrameDiagnostic(int Index, bool Usable, double DarkPercent, bool IsDuplicateOfPrevious, double Detail);

	/// <summary>Runs <see cref="SelectUsable"/> and reports, per frame, exactly why each one did or
	/// didn't survive: dark-pixel percentage (rejected at ≥80%), whether it's byte-identical to its
	/// immediate predecessor, and its <see cref="MeasureDetail"/> score (rejected below
	/// <see cref="MinDetail"/>). Same frame-index ↔ time mapping as <see cref="SelectUsable"/> —
	/// nothing is skipped or reordered.</summary>
	public static FrameDiagnostic[] DiagnoseFrames(byte[][] frames) {
		bool[] usable = SelectUsable(frames);
		var diagnostics = new FrameDiagnostic[frames.Length];
		for (int f = 0; f < frames.Length; f++) {
			bool isDuplicate = f > 0 && frames[f].AsSpan().SequenceEqual(frames[f - 1]);
			diagnostics[f] = new FrameDiagnostic(f, usable[f], MeasureDarkPercent(frames[f]), isDuplicate, MeasureDetail(frames[f]));
		}
		return diagnostics;
	}
}
