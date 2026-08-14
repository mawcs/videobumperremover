// /*
//     Copyright (C) 2026 0x90d
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
// Derived from AcoustID.NET by wo80 (https://github.com/wo80/AcoustID.NET), LGPL 2.1.
// Modernised for net9.0: fixed Chroma allocation, uses Span<double> for frame view.

using VDF.Core.Chromaprint.Pipeline;

namespace VDF.Core.Chromaprint {

	/// <summary>
	/// Orchestrates the full audio fingerprinting pipeline.
	///
	/// Expected usage:
	/// <code>
	///   var ctx = new ChromaContext();
	///   ctx.Start();
	///   ctx.Feed(monoSamples11025Hz);
	///   ctx.Finish();
	///   uint[] fingerprint = ctx.GetRawFingerprint();
	/// </code>
	///
	/// Input PCM must be mono 16-bit samples at 11025 Hz.
	/// Each element in the returned array represents <see cref="_bucketSeconds"/> of audio encoded
	/// as a 32-bit integer (majority-vote aggregation of the per-frame fingerprints, ~8 per second,
	/// that fall within that bucket).
	/// </summary>
	public sealed class ChromaContext {
		// ──────────────────────────────────────────────────────────────────────
		// Frame / hop parameters  (standard Chromaprint / AcoustID values)
		// ──────────────────────────────────────────────────────────────────────
		private const int FrameHop = 1365; // samples between consecutive frames
		// Frames per second = 11025 / 1365 ≈ 8.07

		// Bucket boundaries are anchored to THIS stream's own decode start (frame index 0), not to
		// any absolute/shared reference -- an independently-decoded excerpt of the same underlying
		// audio (e.g. a reference clip extracted from a source file) will essentially never land its
		// own bucket 0 on the same real-time instant the source's bucket grid does, and majority-vote
		// aggregation is sensitive to which frames fall in which bucket. A smaller bucket shrinks that
		// worst-case phase error proportionally, since VDF.Core.ScanEngine.SlidingWindowCompare only
		// ever searches whole-bucket offsets, never a sub-bucket phase correction (docs/iterativeplan.md,
		// "Audio bucket phase-alignment" entry, 2026-08-14 -- live-measured: byte-identical audio
		// scored 95% at zero phase error vs. 79-82% at a 437-500ms phase error, using the original
		// hardcoded 1.0s bucket). Defaults to 1.0 -- the original hardcoded value -- so every caller
		// that predates this parameter (VDF's own ScanEngine.cs partial-clip-audio dedup feature,
		// which has no knowledge of VBR's config and must not be affected by it) is unchanged.
		private readonly double _bucketSeconds;

		// ──────────────────────────────────────────────────────────────────────
		// Pipeline objects  (allocated once, reused across Start/Finish cycles)
		// ──────────────────────────────────────────────────────────────────────
		private readonly Chroma _chroma = new();
		private readonly ChromaFilter _filter = new();
		private readonly double[] _chromaBuf = new double[12];
		private readonly double[] _filteredBuf = new double[12];

		public ChromaContext(double bucketSeconds = 1.0) {
			if (bucketSeconds <= 0)
				throw new ArgumentOutOfRangeException(nameof(bucketSeconds), "Bucket size must be positive.");
			_bucketSeconds = bucketSeconds;
		}

		// ──────────────────────────────────────────────────────────────────────
		// Per-scan state
		// ──────────────────────────────────────────────────────────────────────
		private short[] _samples = Array.Empty<short>();
		private int _sampleCount;

		private readonly List<uint> _secondFrames = new(16);
		private readonly List<uint> _aggregated = new();
		private int _frameIndex;

		/// <summary>Reset state and prepare for a new file.</summary>
		public void Start() {
			_samples = Array.Empty<short>();
			_sampleCount = 0;
			_frameIndex = 0;
			_secondFrames.Clear();
			_aggregated.Clear();
			_filter.Reset();
		}

		/// <summary>
		/// Accepts a block of mono 16-bit PCM samples at 11025 Hz and processes
		/// all complete frames available.  Any leftover samples are retained for
		/// the next call.
		/// </summary>
		public void Feed(ReadOnlySpan<short> samples) {
			// Append incoming samples to the carry buffer
			int needed = _sampleCount + samples.Length;
			if (_samples.Length < needed) {
				var newBuf = new short[needed + Chroma.FrameSize]; // extra headroom
				_samples.AsSpan(0, _sampleCount).CopyTo(newBuf);
				_samples = newBuf;
			}
			samples.CopyTo(_samples.AsSpan(_sampleCount));
			_sampleCount += samples.Length;

			ProcessFrames();
		}

		/// <summary>
		/// Flushes the last partial bucket.  Call once after all audio
		/// has been fed.
		/// </summary>
		public void Finish() {
			if (_secondFrames.Count > 0) {
				_aggregated.Add(FingerprintCalculator.AggregateMajorityVote(_secondFrames));
				_secondFrames.Clear();
			}
		}

		/// <summary>
		/// Returns the aggregated fingerprint: one <c>uint</c> per <see cref="_bucketSeconds"/> of
		/// audio. Only valid after <see cref="Finish"/> has been called.
		/// </summary>
		public uint[] GetRawFingerprint() => _aggregated.ToArray();

		// ──────────────────────────────────────────────────────────────────────
		// Private helpers
		// ──────────────────────────────────────────────────────────────────────

		private void ProcessFrames() {
			Span<double> frameBuf = stackalloc double[Chroma.FrameSize];
			int pos = 0;

			while (pos + Chroma.FrameSize <= _sampleCount) {
				// Convert short → double normalised to [-1, 1]
				for (int i = 0; i < Chroma.FrameSize; i++)
					frameBuf[i] = _samples[pos + i] * (1.0 / 32768.0);

				// Compute chromagram for this frame
				Array.Clear(_chromaBuf, 0, 12);
				_chroma.Compute(frameBuf, _chromaBuf);

				// Temporal FIR smoothing — produces output only once buffer is primed
				if (_filter.Feed(_chromaBuf, _filteredBuf)) {
					ChromaNormalizer.Normalize(_filteredBuf);
					uint fp = FingerprintCalculator.Compute(_filteredBuf);

					// Determine which bucket this frame belongs to
					double frameSec = (double)_frameIndex * FrameHop / Chroma.SampleRate;
					double bucket = Math.Floor(frameSec / _bucketSeconds);

					if (_secondFrames.Count > 0 && bucket > _aggregated.Count) {
						// Close the previous bucket
						_aggregated.Add(FingerprintCalculator.AggregateMajorityVote(_secondFrames));
						_secondFrames.Clear();
					}

					_secondFrames.Add(fp);
					_frameIndex++;
				} else {
					_frameIndex++;
				}

				pos += FrameHop;
			}

			// Shift leftover samples to the front of the buffer
			int leftover = _sampleCount - pos;
			if (leftover > 0 && pos > 0)
				Array.Copy(_samples, pos, _samples, 0, leftover);
			_sampleCount = leftover;
		}
	}
}
