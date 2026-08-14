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
// Modifications Copyright (C) 2026 mawcs — ExtractFingerprintProcess's stdout read loop gained a
// per-read stall timeout (see StallTimeoutMs below), mirroring the stall-not-total-time timeout
// AudioStreamDecoder (the native path) already had. Found via a real hang: a file that fell back to
// process mode sat with zero output for 10+ minutes before the maintainer killed it by hand — the
// blocking `stream.Read` had no timeout of any kind, so a stalled/wedged ffmpeg process (as opposed
// to one that exited with an error) could never be recovered from.

using System.Diagnostics;
using System.Runtime.InteropServices;
using VDF.Core.Chromaprint;
using VDF.Core.Chromaprint.Pipeline;
using VDF.Core.FFTools.FFmpegNative;
using VDF.Core.Utils;

namespace VDF.Core.FFTools {

	/// <summary>
	/// Extracts audio from a video file via FFmpeg and computes a Chromaprint-style
	/// audio fingerprint stored as an array of aggregated 1-second <c>uint</c> blocks.
	/// </summary>
	internal static class ChromaprintEngine {
		private const int TimeoutMs = 30_000; // 30 seconds max for process exit after stream ends
		// Per-read stall timeout for ExtractFingerprintProcess's stdout loop -- matches
		// AudioStreamDecoder's own per-frame stall timeout default (the native path), so both paths
		// give a stalled/wedged ffmpeg the same grace period before being killed. Resets on every
		// successful read, so a long file's total decode time is never capped -- only an individual
		// hung read is.
		private const int StallTimeoutMs = 120_000;
		private const int TargetSampleRate = 11025;
		private const int TargetChannels = 1;
		// Read PCM in 32 KB chunks — keeps memory low while giving ChromaContext
		// enough samples to process multiple frames per iteration.
		private const int ReadBufferSize = 32_768;

		/// <summary>
		/// Extracts the audio fingerprint for <paramref name="filePath"/>.
		/// Returns <c>null</c> when the file has no audio stream or extraction fails.
		/// Returns an empty array when the file has no usable audio.
		/// </summary>
		/// <param name="bucketSeconds">Forwarded to <see cref="ChromaContext"/> — see its own doc
		/// comment. Defaults to 1.0 (the original hardcoded bucket size) so every caller that
		/// predates this parameter, including VDF's own <c>ScanEngine.cs</c> partial-clip-audio dedup
		/// feature, is unaffected; VBR.Core's callers pass <c>VbrConfig.Current.Audio.BucketSeconds</c>
		/// explicitly (VDF.Core cannot reference VBR.Core.Configuration directly — ADR 0005's layering).</param>
		internal static uint[]? ExtractFingerprint(string filePath, bool extendedLogging, CancellationToken ct = default, Action<double>? onProgress = null, double bucketSeconds = 1.0) {
			// Was FfmpegEngine.UseNativeBinding (the raw, ungated toggle) until 2026-08-14 -- that
			// skipped both the CanLoadNativeLibraries probe and the session circuit breaker every
			// other native call site uses, so on a machine where the shared libraries are present
			// but can't actually be loaded/called (issues #793/#795), this attempted -- and logged a
			// fresh warning for -- native decode on every single file instead of falling back once.
			// ShouldUseNativeBinding + RecordNativeSuccess/RecordNativeFailure is the same shared
			// mechanism FfmpegEngine's own native call sites already use for exactly this reason.
			if (FfmpegEngine.ShouldUseNativeBinding) {
				try {
					uint[]? result = ExtractFingerprintNative(filePath, extendedLogging, ct, onProgress, bucketSeconds);
					FfmpegEngine.RecordNativeSuccess();
					return result;
				}
				catch (Exception e) {
					FfmpegEngine.RecordNativeFailure(filePath, e);
				}
			}
			return ExtractFingerprintProcess(filePath, extendedLogging, ct, bucketSeconds);
		}

		/// <summary>Native path: uses FFmpeg.AutoGen bindings — no process spawning.</summary>
		private static uint[]? ExtractFingerprintNative(string filePath, bool extendedLogging, CancellationToken ct, Action<double>? onProgress, double bucketSeconds) {
			var sw = extendedLogging ? Stopwatch.StartNew() : null;

			// Suppress noisy FFmpeg warnings (e.g. AAC "Could not update timestamps
			// for skipped samples") that flood the debug console and can stall threads.
			// The CLI process path uses -loglevel quiet; match that here.
			int prevLogLevel = FFmpeg.AutoGen.ffmpeg.av_log_get_level();
			FFmpeg.AutoGen.ffmpeg.av_log_set_level(extendedLogging
				? FFmpeg.AutoGen.ffmpeg.AV_LOG_ERROR
				: FFmpeg.AutoGen.ffmpeg.AV_LOG_FATAL);
			try {
				using var decoder = new AudioStreamDecoder(filePath, TargetSampleRate, ct);
				if (!decoder.HasAudioStream)
					return Array.Empty<uint>();

				var ctx = new ChromaContext(bucketSeconds);
				ctx.Start();
				int totalSamples = decoder.DecodeAll(samples => ctx.Feed(samples), ct, onProgress);

				if (ct.IsCancellationRequested)
					return null;

				if (totalSamples < Chroma.FrameSize) // too short to fingerprint
					return null;

				ctx.Finish();
				var result = ctx.GetRawFingerprint();

				if (extendedLogging)
					Logger.Instance.Info($"[ChromaprintEngine] {Path.GetFileName(filePath)}: " +
						$"native, total={sw!.ElapsedMilliseconds}ms, " +
						$"samples={totalSamples}, blocks={result.Length}, " +
						$"thread={Environment.CurrentManagedThreadId}");

				return result;
			}
			finally {
				FFmpeg.AutoGen.ffmpeg.av_log_set_level(prevLogLevel);
			}
		}

		/// <summary>CLI fallback: spawns an FFmpeg process and streams PCM from stdout.</summary>
		private static uint[]? ExtractFingerprintProcess(string filePath, bool extendedLogging, CancellationToken ct, double bucketSeconds) {
			var psi = new ProcessStartInfo {
				FileName = FfmpegEngine.FFmpegPath,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = extendedLogging,
				WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = Path.GetDirectoryName(FfmpegEngine.FFmpegPath) ?? string.Empty
			};

			psi.ArgumentList.Add("-hide_banner");
			psi.ArgumentList.Add("-loglevel");
			psi.ArgumentList.Add(extendedLogging ? "error" : "quiet");
			psi.ArgumentList.Add("-nostdin");
			psi.ArgumentList.Add("-i");
			psi.ArgumentList.Add(FFToolsUtils.LongPathFix(filePath));
			psi.ArgumentList.Add("-vn");                                // drop video
			psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(TargetChannels.ToString());
			psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(TargetSampleRate.ToString());
			psi.ArgumentList.Add("-f");  psi.ArgumentList.Add("s16le"); // raw 16-bit LE PCM
			psi.ArgumentList.Add("pipe:1");

			using var process = new Process { StartInfo = psi };
			string errOutput = string.Empty;

			try {
				var sw = extendedLogging ? Stopwatch.StartNew() : null;
				process.Start();
				FFToolsUtils.LowerChildPriority(process);

				if (extendedLogging) {
					process.ErrorDataReceived += (_, e) => {
						if (e.Data?.Length > 0)
							errOutput += Environment.NewLine + e.Data;
					};
					process.BeginErrorReadLine();
				}

				// Stream PCM directly into ChromaContext in small chunks instead of
				// buffering the entire audio into memory.  This allows Chromaprint to
				// process frames in parallel with FFmpeg's decode and keeps memory flat.
				var ctx = new ChromaContext(bucketSeconds);
				ctx.Start();

				var stream = process.StandardOutput.BaseStream;
				var buf = new byte[ReadBufferSize];
				int totalBytes = 0;
				// Carry buffer for an odd trailing byte from the previous read
				// (PCM samples are 2 bytes each, reads may return odd byte counts)
				byte leftoverByte = 0;
				bool hasLeftover = false;

				while (true) {
					if (ct.IsCancellationRequested) {
						KillProcess(process);
						return null;
					}

					var readTask = stream.ReadAsync(buf, 0, buf.Length, ct);
					if (!readTask.Wait(StallTimeoutMs, ct)) {
						// No bytes for StallTimeoutMs -- ffmpeg is wedged, not just slow (a real,
						// long file's total decode time is never bounded here, only an individual
						// hung read). Kill it and give the now-orphaned read a couple seconds to
						// unwind (mirrors DenseFrameSampler.SampleFrames' own KillQuietly) rather
						// than leaving it to complete on its own in the background indefinitely.
						KillProcess(process);
						try { readTask.Wait(2000, CancellationToken.None); } catch { }
						Logger.Instance.Warn($"[ChromaprintEngine] Audio extraction stalled on " +
							$"'{Path.GetFileName(filePath)}' (no data for {StallTimeoutMs / 1000}s) -- " +
							"aborting process-mode fingerprinting for this file.");
						return null;
					}
					int bytesRead = readTask.Result;
					if (bytesRead <= 0) break;

					totalBytes += bytesRead;

					// Prepend leftover byte from previous iteration if any
					byte[]? merged = null;
					if (hasLeftover) {
						merged = new byte[1 + bytesRead];
						merged[0] = leftoverByte;
						Buffer.BlockCopy(buf, 0, merged, 1, bytesRead);
						bytesRead += 1;
						hasLeftover = false;
					}

					// If odd number of bytes, save the last one for next iteration
					byte[] source = merged ?? buf;
					if (bytesRead % 2 != 0) {
						leftoverByte = source[bytesRead - 1];
						hasLeftover = true;
						bytesRead--;
					}

					if (bytesRead >= 2) {
						var samples = MemoryMarshal.Cast<byte, short>(
							source.AsSpan(0, bytesRead));
						ctx.Feed(samples);
					}
				}

				if (ct.IsCancellationRequested) {
					KillProcess(process);
					return null;
				}

				process.WaitForExit(TimeoutMs);

				if (extendedLogging && errOutput.Length > 0)
					Logger.Instance.Info($"[ChromaprintEngine] {Path.GetFileName(filePath)}: {errOutput}");

				if (totalBytes < Chroma.FrameSize * 2) // too short to fingerprint
					return null;

				ctx.Finish();
				var result = ctx.GetRawFingerprint();

				if (extendedLogging)
					Logger.Instance.Info($"[ChromaprintEngine] {Path.GetFileName(filePath)}: " +
						$"process, total={sw!.ElapsedMilliseconds}ms, " +
						$"pcm={totalBytes / 1024}KB, blocks={result.Length}");

				return result;
			}
			catch (OperationCanceledException) {
				KillProcess(process);
				return null;
			}
			catch (Exception ex) {
				Logger.Instance.Warn($"[ChromaprintEngine] Failed on '{filePath}': {ex.Message}");
				KillProcess(process);
				return null;
			}
		}

		private static void KillProcess(Process process) {
			try {
				if (!process.HasExited)
					process.Kill();
			}
			catch { }
		}
	}
}
