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

		// Compact local timestamp (yyyyMMddHHmm, no dashes) — used in the header and filename
		// so successive runs are kept as distinct files instead of overwriting.
		string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
		Line($"Probe run {stamp}");

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

		// Optional positional windows: constrain the offset search to the first
		// BUMPER_HEAD_SECONDS and/or last BUMPER_TAIL_SECONDS of each episode (blocks ≈ 1s).
		// Shrinking the offset space is the point — it should drop the false-positive floor,
		// so a short edge-bumper becomes discriminable where a whole-file search failed.
		int headSec = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_HEAD_SECONDS"), out var hs) && hs > 0 ? hs : 0;
		int tailSec = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_TAIL_SECONDS"), out var ts) && ts > 0 ? ts : 0;
		Line($"Windows: full{(headSec > 0 ? $" + head {headSec}s" : "")}{(tailSec > 0 ? $" + tail {tailSec}s" : "")}");
		Line(new string('-', 78));

		// Compare the clip against ep[start .. start+count]; returns (similarity, absolute offset).
		(float sim, int off) Win(uint[] ep, int start, int count) {
			if (count < clipFp!.Length) return (0f, -1);
			var (s, o) = ScanEngine.SlidingWindowCompare(clipFp, ep[start..(start + count)], minSim: 0f);
			return (s, start + o);
		}

		foreach (var ep in episodes) {
			uint[]? epFp = ChromaprintEngine.ExtractFingerprint(ep, extendedLogging: false);
			if (epFp is not { Length: >= 2 }) {
				Line($"{Path.GetFileName(ep),-48}  (no audio fingerprint)");
				continue;
			}
			var row = new StringBuilder($"{Path.GetFileName(ep),-48}");
			var (fSim, fOff) = Win(epFp, 0, epFp.Length);
			row.Append($"  full={fSim,6:P0}@{fOff,5}s");
			if (headSec > 0) { var (s, o) = Win(epFp, 0, Math.Min(headSec, epFp.Length)); row.Append($"  head={s,6:P0}@{o,4}s"); }
			if (tailSec > 0) { int n = Math.Min(tailSec, epFp.Length); var (s, o) = Win(epFp, epFp.Length - n, n); row.Append($"  tail={s,6:P0}@{o,5}s"); }
			Line(row.ToString());
		}
		Line(new string('-', 78));

		string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clipPath!))!, $"bumper-probe-results-{stamp}.txt");
		try { File.WriteAllText(outPath, log.ToString()); _out.WriteLine($"\nWrote results to: {outPath}"); }
		catch (Exception e) { _out.WriteLine($"(could not write results file: {e.Message})"); }
	}
}
