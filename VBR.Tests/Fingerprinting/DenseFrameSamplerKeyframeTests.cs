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
using System.Threading.Tasks;
using VBR.Core.Fingerprinting;
using Xunit;

namespace VBR.Tests.Fingerprinting;

/// <summary>
/// Plumbing coverage for <see cref="DenseFrameSampler.SampleKeyframes"/> — the library scan's
/// whole-file sparse pass (docs/iterativeplan.md, Step 1). A synthetic solid-color clip can't
/// exercise DINOv2/pHash content quality (that needs real bumper media — see the env-var-gated
/// tests alongside <c>VisualBumperMatcherMixedDensityTests</c>), but it does prove the
/// <c>-skip_frame nokey</c> ffmpeg command actually runs and yields the right frame count/size —
/// same rationale and shell-out-to-ffmpeg-on-PATH convention as
/// <c>LibraryCleanerTests.GenerateSyntheticVideo</c>.
/// </summary>
public class DenseFrameSamplerKeyframeTests {
	const int Side = 224;
	const int FrameBytes = Side * Side * 3;

	[Fact]
	public void SampleKeyframes_ReturnsExpectedFrameCountAndSize_ForAKnownDurationClip() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_keyframe_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			string path = Path.Combine(dir, "synthetic.mp4");
			GenerateSyntheticVideo(path, durationSeconds: 4.0);

			byte[][] frames = DenseFrameSampler.SampleKeyframes(path, intervalSeconds: 1.0, maxFrames: 400);

			// fps=1/1:round=up over a 4s clip -> 4 samples.
			Assert.Equal(4, frames.Length);
			Assert.All(frames, f => Assert.Equal(FrameBytes, f.Length));
		}
		finally { try { Directory.Delete(dir, recursive: true); } catch { } }
	}

	[Fact]
	public void SampleKeyframes_RespectsMaxFramesCap() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_keyframe_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try {
			string path = Path.Combine(dir, "synthetic.mp4");
			GenerateSyntheticVideo(path, durationSeconds: 4.0);

			byte[][] frames = DenseFrameSampler.SampleKeyframes(path, intervalSeconds: 1.0, maxFrames: 2);

			Assert.Equal(2, frames.Length);
		}
		finally { try { Directory.Delete(dir, recursive: true); } catch { } }
	}

	// Same rationale as LibraryCleanerTests.GenerateSyntheticVideo: VBR.Tests has no
	// InternalsVisibleTo from VDF.Core, so this shells out to ffmpeg on PATH directly.
	static void GenerateSyntheticVideo(string path, double durationSeconds) {
		var psi = new ProcessStartInfo {
			FileName = "ffmpeg",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(FormattableString.Invariant($"color=c=blue:s=64x64:d={durationSeconds}:r=10"));
		// A solid-color source has no scene changes, so libx264's default GOP would place just one
		// keyframe for the whole clip -- useless for testing -skip_frame nokey. Force one every 10
		// frames (1s at this 10fps source) so there's actually more than one keyframe to sample.
		psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("10");
		psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
		psi.ArgumentList.Add(path);
		using var process = Process.Start(psi)!;
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		bool exited = process.WaitForExit(30_000);
		if (!exited) {
			try { process.Kill(); } catch { }
			throw new InvalidOperationException("ffmpeg timed out generating a synthetic test video.");
		}
		Task.WaitAll(stdoutTask, stderrTask);
		if (process.ExitCode != 0 || !File.Exists(path))
			throw new InvalidOperationException($"Failed to generate a synthetic test video via ffmpeg — is it on PATH? stderr: {stderrTask.Result}");
	}
}
