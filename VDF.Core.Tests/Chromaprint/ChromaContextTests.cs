// /*
//     Copyright (C) 2026 mawcs
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using VDF.Core.Chromaprint;

namespace VDF.Core.Tests.Chromaprint;

/// <summary>
/// Regression coverage for the bucket-size bug described in docs/iterativeplan.md's "Audio bucket
/// phase-alignment" entry (2026-08-14): <c>ChromaContext</c> aggregates per-frame fingerprints into
/// buckets anchored to whatever stream is fed in (frame index 0 of *that* decode), not to any
/// absolute time reference. An independently-extracted reference clip is essentially never
/// phase-aligned with the same audio's position inside a candidate file, and
/// <c>ScanEngine.SlidingWindowCompare</c> only ever searches whole-bucket offsets — so even
/// byte-identical audio can score far below 100% purely from a sub-bucket timing offset. Uses pure
/// synthetic PCM (no real media files, no ffmpeg dependency), so this runs unconditionally as part
/// of the normal suite, unlike the real-media Chromaprint/audio tests gated behind environment
/// variables elsewhere in this project.
/// </summary>
public class ChromaContextTests {
	const int SampleRate = 11025;

	/// <summary>A few summed tones plus a little deterministic noise — enough spectral structure for
	/// Chromaprint to produce non-degenerate bits (pure silence or pure white noise both fingerprint
	/// to near-constant/uninteresting output).</summary>
	static short[] GenerateTestAudio(int sampleCount, int seed) {
		var rnd = new Random(seed);
		var samples = new short[sampleCount];
		double phase1 = 0, phase2 = 0, phase3 = 0;
		for (int i = 0; i < sampleCount; i++) {
			phase1 += 2 * Math.PI * 440.0 / SampleRate;
			phase2 += 2 * Math.PI * 660.0 / SampleRate;
			phase3 += 2 * Math.PI * 990.0 / SampleRate;
			double value = 0.3 * Math.Sin(phase1) + 0.2 * Math.Sin(phase2) + 0.15 * Math.Sin(phase3)
				+ 0.1 * (rnd.NextDouble() * 2 - 1);
			samples[i] = (short)Math.Clamp(value * 20000, short.MinValue, short.MaxValue);
		}
		return samples;
	}

	static uint[] Fingerprint(short[] samples, double bucketSeconds) {
		var ctx = new ChromaContext(bucketSeconds);
		ctx.Start();
		ctx.Feed(samples);
		ctx.Finish();
		return ctx.GetRawFingerprint();
	}

	[Fact]
	public void GetRawFingerprint_SmallerBucket_ProducesProportionallyLongerArray() {
		short[] samples = GenerateTestAudio(SampleRate * 6, seed: 1); // 6 seconds
		uint[] oneSecondBuckets = Fingerprint(samples, 1.0);
		uint[] quarterSecondBuckets = Fingerprint(samples, 0.25);

		// ~6 buckets at 1.0s, ~24 at 0.25s -- allow slack for a partial trailing bucket.
		Assert.InRange(oneSecondBuckets.Length, 5, 7);
		Assert.InRange(quarterSecondBuckets.Length, 22, 25);
	}

	[Fact]
	public void ConstructorRejectsNonPositiveBucketSeconds() {
		Assert.Throws<ArgumentOutOfRangeException>(() => new ChromaContext(0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new ChromaContext(-1));
	}

	[Fact]
	public void SlidingWindowCompare_SubBucketPhaseOffset_SmallerBucketScoresCloserToAPerfectMatch() {
		// Simulates the real, live-measured bug: the same underlying audio, decoded independently
		// with a sub-second lead-in (exactly what an independently-extracted reference clip vs. a
		// candidate file's own continuous decode-from-BOF produces in practice), compared at the
		// original hardcoded 1.0s bucket vs. a smaller one. Live numbers on real media (2026-08-14):
		// a 437ms phase offset scored 82% at bucketSeconds=1.0 and 96% at bucketSeconds=0.25.
		short[] baseAudio = GenerateTestAudio(SampleRate * 8, seed: 42); // 8 seconds
		int phaseOffsetSamples = (int)(0.437 * SampleRate); // 437ms -- matches the live-measured case
		var phaseShifted = new short[phaseOffsetSamples + baseAudio.Length];
		Array.Copy(baseAudio, 0, phaseShifted, phaseOffsetSamples, baseAudio.Length);

		uint[] refAt1s = Fingerprint(baseAudio, 1.0);
		uint[] fileAt1s = Fingerprint(phaseShifted, 1.0);
		(float sim1s, _) = ScanEngine.SlidingWindowCompare(refAt1s, fileAt1s, minSim: 0f);

		uint[] refAtQuarter = Fingerprint(baseAudio, 0.25);
		uint[] fileAtQuarter = Fingerprint(phaseShifted, 0.25);
		(float simQuarter, _) = ScanEngine.SlidingWindowCompare(refAtQuarter, fileAtQuarter, minSim: 0f);

		Assert.True(simQuarter > sim1s,
			$"Expected a smaller bucket to score higher under a sub-bucket phase offset (0.25s={simQuarter:P1} vs 1.0s={sim1s:P1}).");
		Assert.True(simQuarter >= 0.90f,
			$"Expected >=90% similarity at bucketSeconds=0.25 for byte-identical audio, got {simQuarter:P1}.");
	}

	[Fact]
	public void SlidingWindowCompare_WholeBucketPhaseOffset_BothBucketSizesScoreNearPerfect() {
		// Control case: when the phase offset IS an exact multiple of the bucket size, there's no
		// sub-bucket misalignment at either resolution, so both should score high -- confirms the
		// smaller-bucket win above is specifically about the sub-bucket remainder, not just "smaller
		// buckets are always better" for some unrelated reason.
		short[] baseAudio = GenerateTestAudio(SampleRate * 8, seed: 7);
		int phaseOffsetSamples = SampleRate * 2; // exactly 2.0s -- a whole multiple of both bucket sizes
		var phaseShifted = new short[phaseOffsetSamples + baseAudio.Length];
		Array.Copy(baseAudio, 0, phaseShifted, phaseOffsetSamples, baseAudio.Length);

		uint[] refAt1s = Fingerprint(baseAudio, 1.0);
		uint[] fileAt1s = Fingerprint(phaseShifted, 1.0);
		(float sim1s, _) = ScanEngine.SlidingWindowCompare(refAt1s, fileAt1s, minSim: 0f);

		Assert.True(sim1s >= 0.90f, $"Expected >=90% similarity with zero sub-bucket phase error, got {sim1s:P1}.");
	}
}
