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
using System.Numerics;
using VDF.Core.AI;
using VDF.Core.pHash;

namespace VBR.Core.Fingerprinting;

/// <summary>
/// Perceptual hash (pHash) computed directly from an already-decoded 224×224 RGB24 frame — per
/// ADR 0006's 2026-07-21 amendment, a "free byproduct" of the same frame sampled for DINOv2
/// embedding. Downsamples in-process (box-averaging each 7×7 block down to pHash's 32×32 working
/// size, then standard BT.601 luma) rather than going through VDF's own ffmpeg-side 32×32
/// grayscale extraction, which would be a second decode of the same content — exactly what
/// <see cref="MixedDensitySampler.GatherFrames"/> exists to avoid.
///
/// Calls into <c>VDF.Core.pHash</c>'s internal DCT hash, visible here via VDF.Core's
/// <c>InternalsVisibleTo("VBR.Core")</c> (see VDF.Core.csproj) — the same mechanism
/// <see cref="VDF.Core.AI.EmbeddingMath"/> and friends already rely on.
/// </summary>
public static class FrameHashing {
	const int Side = OnnxEmbedder.InputSide; // 224 — the frame size DenseFrameSampler emits
	const int HashSide = 32;                 // pHash's fixed working size
	const int Block = Side / HashSide;       // 7 — divides evenly, no fractional sampling

	/// <summary>Computes a 64-bit pHash from a 224×224 RGB24 frame (the exact shape
	/// <see cref="DenseFrameSampler.SampleFrames"/>/<see cref="MixedDensitySampler.GatherFrames"/>
	/// produce).</summary>
	public static ulong ComputePHash(ReadOnlySpan<byte> rgb24) {
		int expected = Side * Side * 3;
		if (rgb24.Length != expected)
			throw new ArgumentException($"expected {Side}x{Side}x3={expected} bytes, got {rgb24.Length}.", nameof(rgb24));

		Span<byte> gray = stackalloc byte[HashSide * HashSide];
		for (int by = 0; by < HashSide; by++) {
			for (int bx = 0; bx < HashSide; bx++) {
				int sum = 0;
				for (int y = 0; y < Block; y++) {
					int rowStart = ((by * Block + y) * Side + bx * Block) * 3;
					for (int x = 0; x < Block; x++) {
						int idx = rowStart + x * 3;
						sum += (299 * rgb24[idx] + 587 * rgb24[idx + 1] + 114 * rgb24[idx + 2]) / 1000;
					}
				}
				gray[by * HashSide + bx] = (byte)(sum / (Block * Block));
			}
		}
		return PerceptualHash.ComputePHashFromGray32x32(gray);
	}

	/// <summary>0..1 similarity between two pHashes: 1 - (Hamming distance / 64), the same scale
	/// <see cref="VDF.Core.pHash.PHashCompare"/> reports for its own duplicate gate.</summary>
	public static float Similarity(ulong a, ulong b) => 1f - BitOperations.PopCount(a ^ b) / 64f;
}
