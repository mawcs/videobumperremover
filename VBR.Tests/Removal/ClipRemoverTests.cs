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

// Env-var-gated real-media test, same convention as AudioBumperMatcherTests (skips cleanly
// without env vars). Run (PowerShell):
//   $env:BUMPER_REMOVE_SOURCE="D:\Media\Show\S01E02.mkv"   # a file the bumper clip matches
//   $env:BUMPER_REMOVE_REGION="end"                        # or "begin"
//   $env:BUMPER_REMOVE_LENGTH_SECONDS="10"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~ClipRemoverTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_REMOVE_MODE = "reencode" (default "streamcopy"). Re-encode decodes and
// re-encodes the ENTIRE kept portion of the source, not just the trimmed region — point this at
// a short clip, not a full episode, or expect the test to run for as long as a normal encode of
// that file's length would take.

using System.Diagnostics;
using System.Globalization;
using VBR.Core.Extraction;
using VBR.Core.Removal;
using Xunit.Abstractions;

namespace VBR.Tests.Removal;

public class ClipRemoverTests {
	readonly ITestOutputHelper _out;
	public ClipRemoverTests(ITestOutputHelper output) => _out = output;

	[Theory]
	[InlineData(@"D:\Media\S01E01.mkv", @"D:\Media\S01E01.vbr.mkv")]
	[InlineData(@"D:\Media\Some.Show.2020.mp4", @"D:\Media\Some.Show.2020.vbr.mp4")]
	[InlineData(@"D:\Media\no-extension", @"D:\Media\no-extension.vbr")]
	public void BuildOutputPath_InsertsVbrBeforeExtension_BesideSource(string source, string expected) {
		Assert.Equal(expected, ClipRemover.BuildOutputPath(source));
	}

	[Theory]
	[InlineData(RemovalMode.StreamCopy)]
	[InlineData(RemovalMode.ReEncode)]
	public void Remove_RejectsNonPositiveBumperLength(RemovalMode mode) {
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			ClipRemover.Remove(@"D:\doesnt-matter.mkv", ClipEdge.end, TimeSpan.Zero, mode));
	}

	[Theory]
	[InlineData(RemovalMode.StreamCopy)]
	[InlineData(RemovalMode.ReEncode)]
	public void Remove_RejectsMissingSource(RemovalMode mode) {
		string missing = Path.Combine(Path.GetTempPath(), $"vbr_missing_{Guid.NewGuid():N}.mkv");
		Assert.Throws<FileNotFoundException>(() =>
			ClipRemover.Remove(missing, ClipEdge.end, TimeSpan.FromSeconds(5), mode));
	}

	[SkippableFact]
	public void Remove_ProducesShorterFileAndManifest_WithoutTouchingSource() {
		string? source = Environment.GetEnvironmentVariable("BUMPER_REMOVE_SOURCE");
		string? regionRaw = Environment.GetEnvironmentVariable("BUMPER_REMOVE_REGION");
		int lengthSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_REMOVE_LENGTH_SECONDS"), out var l) ? l : 0;
		string modeRaw = Environment.GetEnvironmentVariable("BUMPER_REMOVE_MODE") ?? "streamcopy";
		RemovalMode mode = string.Equals(modeRaw, "reencode", StringComparison.OrdinalIgnoreCase)
			? RemovalMode.ReEncode : RemovalMode.StreamCopy;

		Skip.If(string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(regionRaw) || lengthSeconds <= 0,
			"Set BUMPER_REMOVE_SOURCE, BUMPER_REMOVE_REGION (begin|end), and BUMPER_REMOVE_LENGTH_SECONDS to run this test.");
		Skip.If(!File.Exists(source), $"Source not found: {source}");
		Skip.If(!Enum.TryParse<ClipEdge>(regionRaw, ignoreCase: true, out var region),
			$"BUMPER_REMOVE_REGION must be 'begin' or 'end', got '{regionRaw}'.");

		string outputPath = ClipRemover.BuildOutputPath(source!);
		string manifestPath = Path.ChangeExtension(outputPath, ".json");
		CleanupLeftovers(outputPath, manifestPath);

		var sourceInfo = new FileInfo(source!);
		long sourceSizeBefore = sourceInfo.Length;
		DateTime sourceWriteTimeBefore = sourceInfo.LastWriteTimeUtc;

		try {
			var result = ClipRemover.Remove(source!, region, TimeSpan.FromSeconds(lengthSeconds), mode);
			_out.WriteLine($"Mode: {mode}");
			_out.WriteLine($"Output: {result.OutputPath}");
			_out.WriteLine($"Manifest: {result.ManifestPath}");
			_out.WriteLine($"Cut point: {result.CutPoint.TotalSeconds:0.###}s of source duration {result.SourceDuration.TotalSeconds:0.###}s");

			Assert.True(File.Exists(result.OutputPath), "Expected an output file to be created.");
			Assert.True(File.Exists(result.ManifestPath), "Expected a manifest JSON sidecar to be created.");

			// The source must be completely untouched — the whole point of ADR 0007.
			sourceInfo.Refresh();
			Assert.Equal(sourceSizeBefore, sourceInfo.Length);
			Assert.Equal(sourceWriteTimeBefore, sourceInfo.LastWriteTimeUtc);

			// Independently probe the OUTPUT file's real duration (not just trusting the
			// RemovalResult's own arithmetic) to confirm ffmpeg actually cut where intended.
			TimeSpan outputDuration = ProbeDuration(result.OutputPath);
			_out.WriteLine($"Output actual duration: {outputDuration.TotalSeconds:0.###}s");
			double actuallyRemoved = (result.SourceDuration - outputDuration).TotalSeconds;
			Assert.True(actuallyRemoved >= lengthSeconds - 0.5,
				$"Expected at least ~{lengthSeconds}s to be removed; only {actuallyRemoved:0.###}s was.");
		}
		finally {
			CleanupLeftovers(outputPath, manifestPath);
		}
	}

	static void CleanupLeftovers(string outputPath, string manifestPath) {
		try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
		try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
	}

	// VBR.Tests has no InternalsVisibleTo from VDF.Core (only VBR.Core does), so this probes
	// duration directly via ffprobe on PATH rather than VDF.Core.FFTools.FFProbeEngine.
	static TimeSpan ProbeDuration(string path) {
		var psi = new ProcessStartInfo {
			FileName = "ffprobe",
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
		psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("csv=p=0");
		psi.ArgumentList.Add(path);
		using var process = Process.Start(psi)!;
		string stdout = process.StandardOutput.ReadToEnd();
		process.WaitForExit(15_000);
		return TimeSpan.FromSeconds(double.Parse(stdout.Trim(), CultureInfo.InvariantCulture));
	}
}
