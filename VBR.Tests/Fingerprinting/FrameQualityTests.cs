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
}
