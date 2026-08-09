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
using VBR.Core.Fingerprinting;
using Xunit;

namespace VBR.Tests.Fingerprinting;

public class FrameQualityTests {
	const int Side = 224;
	const int FrameBytes = Side * Side * 3;

	static byte[] Solid(byte r, byte g, byte b) {
		var frame = new byte[FrameBytes];
		for (int i = 0; i < FrameBytes; i += 3) {
			frame[i] = r;
			frame[i + 1] = g;
			frame[i + 2] = b;
		}
		return frame;
	}

	/// <summary>Alternating black/white column stripes — maximal horizontal edge energy,
	/// 50% dark pixels (below the 80% dark-rejection threshold).</summary>
	static byte[] Stripes() {
		var frame = new byte[FrameBytes];
		for (int y = 0; y < Side; y++)
			for (int x = 0; x < Side; x++) {
				byte v = (byte)(x % 2 == 0 ? 0 : 255);
				int i = (y * Side + x) * 3;
				frame[i] = frame[i + 1] = frame[i + 2] = v;
			}
		return frame;
	}

	[Fact]
	public void Black_frame_is_unusable() {
		bool[] usable = FrameQuality.SelectUsable(new[] { Solid(0, 0, 0), Stripes() });
		Assert.False(usable[0]);
		Assert.True(usable[1]);
	}

	[Fact]
	public void Blank_white_frame_is_unusable_despite_not_being_dark() {
		// The blank-white ident background: bright, so the dark guard passes it — only the
		// detail guard catches it (measured 0.55–0.68 on the real thing, threshold 1.0).
		bool[] usable = FrameQuality.SelectUsable(new[] { Solid(230, 230, 230) });
		Assert.False(usable[0]);
	}

	[Fact]
	public void Byte_identical_duplicate_is_unusable() {
		byte[] a = Stripes();
		byte[] b = (byte[])a.Clone();
		bool[] usable = FrameQuality.SelectUsable(new[] { a, b });
		Assert.True(usable[0]);
		Assert.False(usable[1]);
	}

	[Fact]
	public void Detail_separates_uniform_from_edged_content() {
		Assert.True(FrameQuality.MeasureDetail(Solid(230, 230, 230)) < FrameQuality.MinDetail);
		Assert.True(FrameQuality.MeasureDetail(Solid(0, 0, 0)) < FrameQuality.MinDetail);
		Assert.True(FrameQuality.MeasureDetail(Stripes()) > FrameQuality.MinDetail);
	}

	[Fact]
	public void MeasureDetail_rejects_wrong_size() {
		Assert.Throws<ArgumentException>(() => FrameQuality.MeasureDetail(new byte[10]));
	}

	// A near-black background (well under BlackPixelLimit on every channel) with a small tight-
	// striped patch at (x,y,w,h) -- concentrated real local contrast, the same shape real bright
	// text/logo-on-black content has, without needing to reproduce actual glyphs. A solid box's
	// two edge-crossings per row don't get anywhere near the ~4x-MinDetail scores dogfooding
	// measured on real idents; tight alternation does, in a much smaller area, which is exactly
	// why real text/logos (many edges) score high while a plain shape wouldn't.
	static byte[] DarkFrameWithDetailedPatch(int x, int y, int w, int h) {
		byte[] frame = Solid(4, 4, 4);
		for (int row = y; row < y + h && row < Side; row++) {
			for (int col = x; col < x + w && col < Side; col++) {
				byte v = (byte)((col - x) % 2 == 0 ? 0 : 255);
				int i = (row * Side + col) * 3;
				frame[i] = frame[i + 1] = frame[i + 2] = v;
			}
		}
		return frame;
	}

	// ---- Dark-pixel veto deferring to detail (docs/iterativeplan.md, 2026-08-07) ----
	// Live dogfooding (2026-08-07) found real static AND motion bumpers -- bright text/logo on a
	// majority-dark field -- wrongly rejected: dark%=93.9-100, yet detail=4.2-4.6 (four times
	// MinDetail). SelectUsable now only rejects a majority-dark frame when its OWN detail also
	// stays below the higher DarkOverrideDetail bar, instead of vetoing on darkness alone.

	[Fact]
	public void DarkFrameWithRealDetail_IsUsable_TheFixThisEntryValidates() {
		byte[] frame = DarkFrameWithDetailedPatch(x: 40, y: 90, w: 40, h: 40);
		double dark = FrameQuality.MeasureDarkPercent(frame);
		double detail = FrameQuality.MeasureDetail(frame);
		Assert.True(dark >= FrameQuality.DarkRejectPercent, $"Test setup: expected a majority-dark frame, got {dark:0.#}%.");
		Assert.True(detail >= FrameQuality.DarkOverrideDetail, $"Test setup: expected detail to clear the override bar, got {detail:0.###}.");
		Assert.True(FrameQuality.SelectUsable(new[] { frame })[0]);
	}

	[Fact]
	public void DarkFrameWithOnlyBorderlineDetail_StillRejected_HigherBarThanMinDetailIsReal() {
		// A smaller patch than the case above -- enough detail to clear MinDetail (1.0) but not
		// DarkOverrideDetail (2.0). Proves the dark-override bar is a genuinely higher line, not
		// an accidental alias of MinDetail.
		byte[] frame = DarkFrameWithDetailedPatch(x: 100, y: 100, w: 17, h: 18);
		double dark = FrameQuality.MeasureDarkPercent(frame);
		double detail = FrameQuality.MeasureDetail(frame);
		Assert.True(dark >= FrameQuality.DarkRejectPercent, $"Test setup: expected a majority-dark frame, got {dark:0.#}%.");
		Assert.True(detail >= FrameQuality.MinDetail && detail < FrameQuality.DarkOverrideDetail,
			$"Test setup: expected detail between MinDetail and DarkOverrideDetail, got {detail:0.###}.");
		Assert.False(FrameQuality.SelectUsable(new[] { frame })[0]);
	}

	[Fact]
	public void DuplicateOfDarkDetailedFrame_StillRejected_DarkOverrideDoesNotBypassDuplicateCheck() {
		byte[] a = DarkFrameWithDetailedPatch(x: 40, y: 90, w: 40, h: 40);
		byte[] b = (byte[])a.Clone();
		bool[] usable = FrameQuality.SelectUsable(new[] { a, b });
		Assert.True(usable[0]);
		Assert.False(usable[1]);
	}

	// ---- False-positive risk: does admitting dark-but-detailed frames reopen the 2026-07-18 bug? ----
	// That fix exists because low-information frames embed/hash near-identically regardless of
	// actual content -- unrelated near-black frames scored DINOv2 cosine 0.87-0.97 purely from
	// having nothing to discriminate on. pHash (no ONNX/network dependency, unlike DINOv2 --
	// suitable for a fast unit test) is used here as a real, independently-computed perceptual
	// signal to check the same question for this change, not just assert it's fine.

	[Fact]
	public void TwoUnrelatedDegenerateNearBlackFrames_AliasEachOther_TheOriginalBugPattern() {
		// Both stay rejected under the new logic too (near-zero detail, no override applies) --
		// confirms the fix doesn't touch this class of frame at all, only the dark-AND-detailed one.
		byte[] blackA = Solid(4, 3, 5);
		byte[] blackB = Solid(2, 6, 3);
		Assert.False(FrameQuality.SelectUsable(new[] { blackA })[0]);
		Assert.False(FrameQuality.SelectUsable(new[] { blackB })[0]);
		float similarity = FrameHashing.Similarity(FrameHashing.ComputePHash(blackA), FrameHashing.ComputePHash(blackB));
		// 0.85, not the DINOv2 finding's own 0.87-0.97 -- pHash is a different metric than DINOv2
		// cosine similarity, so its exact aliasing number differs; measured 87.5% here, comfortably
		// within "these alias each other," which is the actual point being demonstrated.
		Assert.True(similarity >= 0.85f,
			$"Expected two unrelated degenerate near-black frames to alias each other -- the same character " +
			$"of false positive the 2026-07-18 DINOv2 finding described (0.87-0.97 cosine), just measured here " +
			$"via pHash instead -- got {similarity:P1}.");
	}

	[Fact]
	public void TwoDifferentDarkDetailedFrames_RemainDistinguishable_NotAliased() {
		// The actual risk question for this fix: two genuinely DIFFERENT dark-with-real-detail
		// frames (different patch position -- a different "logo") both now pass SelectUsable (the
		// fix's whole point), but must not read as the same content the way degenerate frames do.
		byte[] cardA = DarkFrameWithDetailedPatch(x: 20, y: 20, w: 50, h: 50);
		byte[] cardB = DarkFrameWithDetailedPatch(x: 140, y: 150, w: 50, h: 50);
		Assert.True(FrameQuality.SelectUsable(new[] { cardA })[0]);
		Assert.True(FrameQuality.SelectUsable(new[] { cardB })[0]);
		float similarity = FrameHashing.Similarity(FrameHashing.ComputePHash(cardA), FrameHashing.ComputePHash(cardB));
		Assert.True(similarity < 0.90f,
			$"Expected two genuinely different dark-with-detail cards to stay distinguishable (below a " +
			$"90% presence-style bar), got {similarity:P1} -- if they alias, the dark-override fix risks " +
			"the exact false-positive class it was designed not to reopen.");
	}
}
