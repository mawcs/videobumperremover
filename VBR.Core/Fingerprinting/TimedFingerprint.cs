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

namespace VBR.Core.Fingerprinting;

/// <summary>
/// One sampled position in a <see cref="WholeFileSampler"/> result: both signals (DINOv2 embedding
/// and pHash) together, tagged with its real absolute timestamp (seconds from the true start of the
/// file — unlike <see cref="TimedFrame"/>/<see cref="TimedPHash"/>, which are window-relative,
/// <see cref="WholeFileSampler"/> always knows the file's true probed duration, so it can use real
/// absolute time throughout). Deliberately its own type rather than reusing
/// <see cref="TimedFrame"/>/<see cref="TimedPHash"/> as a pair: the whole-file sampler merges three
/// separately-decoded passes (dense begin, dense end, sparse whole-file) into one combined,
/// timestamp-sorted list, and one type per point is simpler to merge/sort/persist than keeping two
/// parallel arrays in lockstep. <c>MemoryPack</c>-serializable directly — this is the shape
/// persisted per-file in the library database (see <c>VBR.Core.Database</c>).
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class TimedFingerprint {
	[MemoryPackOrder(0)]
	public double TimestampSeconds { get; set; }

	[MemoryPackOrder(1)]
	public byte[] Embedding { get; set; } = Array.Empty<byte>();

	[MemoryPackOrder(2)]
	public ulong PHash { get; set; }

	[MemoryPackConstructor]
	public TimedFingerprint(double timestampSeconds, byte[] embedding, ulong pHash) {
		TimestampSeconds = timestampSeconds;
		Embedding = embedding;
		PHash = pHash;
	}
}
