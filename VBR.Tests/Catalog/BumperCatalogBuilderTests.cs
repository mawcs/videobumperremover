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
using VBR.Core.Catalog;
using VBR.Core.Extraction;
using Xunit;

namespace VBR.Tests.Catalog;

/// <summary>
/// Covers <see cref="BumperCatalogBuilder.AddBumper"/>'s up-front validation only -- the parts that
/// throw before ever touching the ONNX model or doing real sampling, so these run fast with no AI
/// model download. Full pipeline correctness (real fingerprints, thumbnail quality, reference clip
/// content) needs real bumper media and is validated live against it instead, same convention as
/// <c>VisualBumperMatcherMixedDensityTests</c>/<c>LibraryScannerEquivalenceTests</c>.
/// </summary>
public class BumperCatalogBuilderTests {
	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_addbumper_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	[Fact]
	public void AddBumper_NonexistentSource_ThrowsFileNotFoundException() {
		string dir = CreateTempDir();
		try {
			string missing = Path.Combine(dir, "does-not-exist.mkv");
			Assert.Throws<FileNotFoundException>(() =>
				BumperCatalogBuilder.AddBumper(missing, ClipEdge.begin, TimeSpan.FromSeconds(5),
					"Label", null, Array.Empty<string>(), Path.Combine(dir, "clips")));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void AddBumper_NonPositiveClipLength_ThrowsArgumentOutOfRangeException() {
		string dir = CreateTempDir();
		try {
			// Not a real video -- fine, the clip-length check runs (and throws) before anything
			// ever tries to probe or decode the file.
			string fakeSource = Path.Combine(dir, "fake.mkv");
			File.WriteAllBytes(fakeSource, new byte[] { 1, 2, 3 });

			Assert.Throws<ArgumentOutOfRangeException>(() =>
				BumperCatalogBuilder.AddBumper(fakeSource, ClipEdge.begin, TimeSpan.Zero,
					"Label", null, Array.Empty<string>(), Path.Combine(dir, "clips")));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void AddBumper_ClipLengthExceedsSourceDuration_ThrowsArgumentOutOfRangeException() {
		string dir = CreateTempDir();
		try {
			string source = Path.Combine(dir, "synthetic.mp4");
			GenerateSyntheticVideo(source, durationSeconds: 2.0);

			var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
				BumperCatalogBuilder.AddBumper(source, ClipEdge.begin, TimeSpan.FromSeconds(10),
					"Label", null, Array.Empty<string>(), Path.Combine(dir, "clips")));
			Assert.Contains("must be shorter than the source file's duration", ex.Message);
		}
		finally { DeleteTempDir(dir); }
	}

	// Same rationale as DenseFrameSamplerKeyframeTests.GenerateSyntheticVideo: VBR.Tests has no
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
		psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
		psi.ArgumentList.Add(path);
		using var process = Process.Start(psi)!;
		// Both streams must be drained concurrently, not sequentially/not at all -- ffmpeg can block
		// writing to a full OS pipe buffer with nothing reading it, hanging WaitForExit indefinitely.
		// Same fix, same rationale, as DenseFrameSamplerKeyframeTests' own GenerateSyntheticVideo.
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
