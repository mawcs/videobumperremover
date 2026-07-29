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

// Graduated from VDF.IntegrationTests/Comparison/BumperMatchProbe.cs (ADR 0005), reshaped for
// AudioBumperMatcher's per-candidate IBumperMatcher API (docs/design/matcher-spec.md). Env var
// naming matches VisualTailProbe's BUMPER_CLIP_EPISODE convention (both auto-extract the
// reference clip from a real episode — no pre-cut clip files, per AGENTS.md "Clip extraction is
// the tool's job").
//
// Run (PowerShell):
//   $env:BUMPER_CLIP_EPISODE="D:\michael\Desktop\Season 01\S01E01.mkv"
//   $env:BUMPER_REGION="end"                # or "begin"
//   $env:BUMPER_CLIP_LENGTH_SECONDS="40"
//   $env:BUMPER_EPISODES_DIR="D:\michael\Desktop\Season 01"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_SEARCH_LENGTH_SECONDS (default: clip length + 20s, matching `vbr match`).

using VBR.Core.Extraction;
using VBR.Core.Matching;
using Xunit.Abstractions;

namespace VBR.Tests.Matching;

public class AudioBumperMatcherTests {
	readonly ITestOutputHelper _out;

	public AudioBumperMatcherTests(ITestOutputHelper output) => _out = output;

	[SkippableFact]
	public void Match_FindsBumperAcrossEpisodes() {
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		string? regionRaw = Environment.GetEnvironmentVariable("BUMPER_REGION");
		int clipLengthSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_CLIP_LENGTH_SECONDS"), out var cl) ? cl : 0;

		Skip.If(string.IsNullOrWhiteSpace(clipEpisode) || string.IsNullOrWhiteSpace(episodesDir) ||
				string.IsNullOrWhiteSpace(regionRaw) || clipLengthSeconds <= 0,
			"Set BUMPER_CLIP_EPISODE (source video to auto-extract the bumper clip from), " +
			"BUMPER_REGION (begin|end), BUMPER_CLIP_LENGTH_SECONDS, and BUMPER_EPISODES_DIR " +
			"(folder of episodes) to run this test.");
		Skip.If(!File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If(!Enum.TryParse<ClipEdge>(regionRaw, ignoreCase: true, out var region),
			$"BUMPER_REGION must be 'begin' or 'end', got '{regionRaw}'.");

		var clipLength = TimeSpan.FromSeconds(clipLengthSeconds);
		int searchLengthSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_SEARCH_LENGTH_SECONDS"), out var sl) ? sl : 0;
		var searchLength = searchLengthSeconds > 0 ? TimeSpan.FromSeconds(searchLengthSeconds) : clipLength + TimeSpan.FromSeconds(20);

		using var referenceClip = ClipExtractor.ExtractToTemp(clipEpisode!, ClipRegion.For(region, clipLength));
		var searchRegion = ClipRegion.For(region, searchLength);
		var matcher = new AudioBumperMatcher();

		var episodes = Directory.EnumerateFiles(episodesDir!)
			.Where(f => ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipEpisode!), StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Skip.If(episodes.Count == 0, "No other episode files found in BUMPER_EPISODES_DIR.");

		var results = episodes.Select(ep => (File: ep, Result: matcher.Match(referenceClip.Path, ep, searchRegion))).ToList();
		foreach (var (file, result) in results.OrderByDescending(r => r.Result.BestScore))
			_out.WriteLine($"{Path.GetFileName(file),-48}  {result.Detail}");

		Assert.True(results.Any(r => r.Result.Present),
			$"Expected at least one episode to match the bumper clip at >= {AudioBumperMatcher.DefaultMinSimilarity:P0}.");
	}

	// Synthetic, real-media-free coverage for MatchFingerprints (docs/iterativeplan.md, "Utilizing
	// Databases" entry) -- unlike Match_FindsBumperAcrossEpisodes above, this needs no ffmpeg/
	// Chromaprint extraction at all: it operates directly on plain uint[] block arrays, exactly the
	// shape LibraryDatabaseEntry.AudioFingerprint/BumperCatalogEntry.AudioFingerprint persist, so it
	// runs unconditionally (no environment variables, no Skip.If).

	[Fact]
	public void MatchFingerprints_FindsClipEmbeddedInLongerFile() {
		uint[] clip = { 111, 222, 333, 444, 555 };
		// 30 "blocks" (~seconds) total; the clip sits at blocks [20, 25) -- squarely inside the
		// tail region ClipRegion.Tail(10) resolves to (the last 10 blocks, [20, 30)).
		uint[] file = new uint[30];
		for (int i = 0; i < file.Length; i++) file[i] = (uint)(1000 + i);
		Array.Copy(clip, 0, file, 20, clip.Length);

		var result = AudioBumperMatcher.MatchFingerprints(clip, file, ClipRegion.Tail(TimeSpan.FromSeconds(10)), AudioBumperMatcher.DefaultMinSimilarity);

		Assert.True(result.Present, $"Expected an exact-block match to be present; got {result.Detail}.");
		Assert.True(result.BestScore > 0.99f, $"Expected near-100% similarity for identical blocks; got {result.BestScore:P1}.");
	}

	[Fact]
	public void MatchFingerprints_NoMatch_WhenBlocksDiffer() {
		// Small integers like {111, 222, ...} share most of their leading zero bits regardless of
		// value, which would make Hamming similarity misleadingly high -- bitwise-complementing
		// gives file blocks that genuinely differ from the clip's across most bits.
		uint[] clip = { 111, 222, 333, 444, 555 };
		uint[] file = new uint[30];
		for (int i = 0; i < file.Length; i++) file[i] = ~(uint)(1000 + i);

		var result = AudioBumperMatcher.MatchFingerprints(clip, file, ClipRegion.Tail(TimeSpan.FromSeconds(10)), AudioBumperMatcher.DefaultMinSimilarity);

		Assert.False(result.Present, $"Expected no match for entirely dissimilar blocks; got {result.Detail}.");
	}

	[Fact]
	public void MatchFingerprints_NullOrTooShortFingerprints_ReportSoftFailure() {
		uint[] file = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

		var noClip = AudioBumperMatcher.MatchFingerprints(null, file, ClipRegion.Tail(TimeSpan.FromSeconds(5)), AudioBumperMatcher.DefaultMinSimilarity);
		Assert.False(noClip.Present);
		Assert.Contains("reference clip", noClip.Detail);

		var noFile = AudioBumperMatcher.MatchFingerprints(new uint[] { 1, 2 }, null, ClipRegion.Tail(TimeSpan.FromSeconds(5)), AudioBumperMatcher.DefaultMinSimilarity);
		Assert.False(noFile.Present);
		Assert.Contains("no usable audio track", noFile.Detail);
	}
}
