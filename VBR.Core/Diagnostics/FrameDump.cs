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
using System.Diagnostics;
using System.IO;
using VDF.Core.AI;
using VDF.Core.FFTools;

namespace VBR.Core.Diagnostics;

/// <summary>
/// Writes the visual matcher's sampled frames to disk as PNGs. The frames the matcher embeds are
/// several transforms away from what a media player shows (extracted edge window → decode →
/// 224×224 rescale), so when a match looks wrong, the only trustworthy evidence is the exact
/// frames that were compared — dumping them turns "why did this match?" into a glance instead of
/// a pipeline reconstruction (see docs/iterativeplan.md, the black-frame false-positive
/// diagnosis, for the session that motivated this).
/// </summary>
public static class FrameDump {
	/// <summary>
	/// Writes each raw 224×224 RGB24 frame as <c>fNNN.png</c> under
	/// <paramref name="directory"/> (created if missing; existing files overwritten). NNN is the
	/// 0-based sample index, so at sample interval <c>i</c> frame <c>fNNN</c> sits ≈ NNN·i
	/// seconds into the extracted clip/window it was sampled from.
	/// </summary>
	/// <exception cref="InvalidOperationException">ffmpeg failed or timed out.</exception>
	public static void WritePngs(byte[][] frames, string directory) {
		Directory.CreateDirectory(directory);
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardInput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
		psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgb24");
		psi.ArgumentList.Add("-video_size");
		psi.ArgumentList.Add(FormattableString.Invariant($"{OnnxEmbedder.InputSide}x{OnnxEmbedder.InputSide}"));
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
		psi.ArgumentList.Add("-start_number"); psi.ArgumentList.Add("0");
		psi.ArgumentList.Add(Path.Combine(directory, "f%03d.png"));

		try {
			using var p = Process.Start(psi)!;
			// Drain stderr concurrently so a chatty ffmpeg can't deadlock against our stdin writes.
			var stderr = p.StandardError.ReadToEndAsync();
			try {
				using Stream stdin = p.StandardInput.BaseStream;
				foreach (byte[] frame in frames) {
					if (frame is null || frame.Length == 0) continue;
					stdin.Write(frame, 0, frame.Length);
				}
			}
			catch (IOException) {
				// ffmpeg exited early (broken pipe) — the exit-code check below reports its stderr.
			}
			if (!p.WaitForExit(60_000)) {
				try { p.Kill(); } catch { }
				throw new InvalidOperationException($"Frame dump to '{directory}' timed out.");
			}
			if (p.ExitCode != 0)
				throw new InvalidOperationException(
					$"Frame dump to '{directory}' failed (ffmpeg exit {p.ExitCode}): {stderr.Result.Trim()}");
		}
		catch (Exception e) when (e is not InvalidOperationException) {
			throw new InvalidOperationException($"Frame dump to '{directory}' failed: {e.Message}", e);
		}
	}
}
