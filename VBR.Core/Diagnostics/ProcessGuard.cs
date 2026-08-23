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

using VDF.Core.FFTools;

namespace VBR.Core.Diagnostics;

/// <summary>
/// Public bridge onto <see cref="FFToolsUtils"/>'s internal ffmpeg/ffprobe process registry
/// (docs/iterativeplan.md's "Ctrl+C doesn't kill the encode" entry, 2026-08-23) -- VBR.CLI has no
/// <c>InternalsVisibleTo</c> grant into either VDF.Core or VBR.Core, so this one public method is
/// the only way its own raw-signal handler (registered once in <c>Program.cs</c>) can reach the
/// registry every ffmpeg-spawning call site (<c>ClipRemover.RunFfmpeg</c>,
/// <c>ClipExtractor.RunFfmpegExtract</c>, <c>DenseFrameSampler</c>'s CLI fallback,
/// <c>GpuEncoderProbe.ProbeEncoder</c>, VDF.Core's own <c>ChromaprintEngine</c>) registers itself
/// into for its own lifetime.
/// </summary>
public static class ProcessGuard {
	/// <summary>
	/// Force-kills every ffmpeg/ffprobe child process currently tracked, regardless of which
	/// command spawned it. A live repro (2026-08-23) against a real <c>vbr remove --re-encode
	/// true</c> run showed the .NET host process exiting within ~0.5s of Ctrl+C while its ffmpeg
	/// child kept re-encoding for 30+ seconds afterward, completely untouched -- the existing
	/// per-call-site <c>CancellationTokenRegistration</c> kill-on-cancel wiring never got a chance
	/// to run before the host process itself was gone. Call this directly from a raw OS signal
	/// handler (see <c>Program.cs</c>), not from anywhere in the normal
	/// <see cref="System.Threading.CancellationToken"/>/async pipeline -- the whole point is a
	/// second line of defense that can't be starved or skipped by whatever left that pipeline
	/// unreliable here. Safe to call from any thread, any number of times, whether or not anything
	/// is currently running.
	/// </summary>
	public static void KillActiveFfmpegProcesses() => FFToolsUtils.KillAllActiveProcesses();
}
