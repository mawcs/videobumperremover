// Bumper Remover — diagnostic probe (not an upstream VDF test).
//
// Exercises VDF's audio-fingerprint matcher DIRECTLY, bypassing every gate that
// the GUI/scan pipeline applies (min clip/source ratio, the 95% duration ceiling,
// the "already grouped by the visual pass" exclusion, and the visual pass itself).
//
// It fingerprints a short clip and every episode in a folder, runs
// ScanEngine.SlidingWindowCompare(clip, episode) for each, and reports the best
// similarity + offset. This tells us whether the *matching primitive* works,
// independent of the settings maze.
//
// Run (PowerShell):
//   $env:BUMPER_CLIP="D:\michael\Desktop\introclip.mkv"
//   $env:BUMPER_EPISODES_DIR="D:\michael\Desktop\Season 01"
//   dotnet test VDF.IntegrationTests --filter "FullyQualifiedName~BumperMatchProbe" -l "console;verbosity=detailed"
//
// Results are also written to "bumper-probe-results.txt" next to the clip, since
// test-runner console capture is unreliable.

using System;
using System.IO;
using System.Linq;
using System.Text;
using VDF.Core;
using VDF.Core.FFTools;
using VDF.IntegrationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace VDF.IntegrationTests.Comparison;

[Collection("Ffmpeg")]
public class BumperMatchProbe {
	readonly FfmpegFixture _fixture;
	readonly ITestOutputHelper _out;

	public BumperMatchProbe(FfmpegFixture fixture, ITestOutputHelper output) {
		_fixture = fixture;
		_out = output;
	}

	static readonly string[] VideoExts = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".webm", ".wmv" };

	[SkippableFact]
	public void Probe_ClipAgainstEpisodes() {
		Skip.If(!_fixture.FfmpegCliAvailable, _fixture.FfmpegNotFoundReason);

		string? clipPath = Environment.GetEnvironmentVariable("BUMPER_CLIP");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		Skip.If(string.IsNullOrWhiteSpace(clipPath) || string.IsNullOrWhiteSpace(episodesDir),
			"Set BUMPER_CLIP (a short clip) and BUMPER_EPISODES_DIR (folder of episodes) to run this probe.");
		Skip.If(!File.Exists(clipPath), $"Clip not found: {clipPath}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");

		// Use the ffmpeg CLI path — no FFmpeg 8.x shared libraries required.
		using var guard = new FfmpegStaticStateGuard();
		FfmpegEngine.UseNativeBinding = false;

		var log = new StringBuilder();
		void Line(string s) { _out.WriteLine(s); log.AppendLine(s); }

		uint[]? clipFp = ChromaprintEngine.ExtractFingerprint(clipPath!, extendedLogging: false);
		Assert.True(clipFp is { Length: >= 2 },
			$"Clip produced no usable audio fingerprint (blocks={clipFp?.Length ?? 0}). Does it have an audio track?");

		Line($"CLIP: {Path.GetFileName(clipPath)}  ({clipFp!.Length} blocks ≈ {clipFp.Length}s of audio)");
		Line(new string('-', 78));

		var episodes = Directory.EnumerateFiles(episodesDir!)
			.Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipPath!), StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Skip.If(episodes.Count == 0, "No episode files found in BUMPER_EPISODES_DIR.");

		int matched = 0;
		foreach (var ep in episodes) {
			uint[]? epFp = ChromaprintEngine.ExtractFingerprint(ep, extendedLogging: false);
			if (epFp is not { Length: >= 2 }) {
				Line($"{Path.GetFileName(ep),-58}  (no audio fingerprint)");
				continue;
			}

			// The clip is expected to be the shorter sequence; guard just in case.
			uint[] shorter = clipFp.Length <= epFp.Length ? clipFp : epFp;
			uint[] longer = clipFp.Length <= epFp.Length ? epFp : clipFp;

			var (sim, offsetBlocks) = ScanEngine.SlidingWindowCompare(shorter, longer, minSim: 0f);
			if (sim >= 0.80f) matched++;
			Line($"{Path.GetFileName(ep),-58}  sim={sim,7:P1}  offset≈{offsetBlocks,5}s  (ep {epFp.Length}s)");
		}

		Line(new string('-', 78));
		Line($"{matched}/{episodes.Count} episodes matched the clip at ≥80% audio similarity.");

		string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clipPath!))!, "bumper-probe-results.txt");
		try { File.WriteAllText(outPath, log.ToString()); _out.WriteLine($"\nWrote results to: {outPath}"); }
		catch (Exception e) { _out.WriteLine($"(could not write results file: {e.Message})"); }
	}
}
