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

// Validates a specific claim behind the future edge-focused/variable-density library scan (ADR
// 0006): VisualBumperMatcher's presence matching does not require the reference clip and a
// candidate's search window to share a sampling grid, density, or edge-anchored extraction point.
// A long bumper (a multi-card end-stack, or the "coming to DVD" 3-5 minute promos the maintainer
// has observed) can exceed a deliberately small `edge-boundary`; if fingerprinting such a bumper
// ever needs a clip drawn from *inside* it rather than anchored to the true file edge, this proves
// that still matches correctly today, via VBR.Core.Extraction.ClipRegion.At (an arbitrary-offset
// region -- distinct from the Head/Tail-only extraction `vbr match` exposes on the CLI, and
// deliberately not added as a CLI flag: this is throwaway validation, not a permanent surface).
//
// Two-step extraction: first pull a generous "outer window" from the true edge (guaranteed to
// contain the whole real bumper plus margin, exactly like `vbr match` does today), then pull the
// actual reference clip from *inside* that window at a caller-chosen offset -- simulating a
// reference clip whose distinctive content sits away from the true edge.
//
// Run (PowerShell), using the real Daredevil end-stack (~20.5s, multiple cards -- ABC Studios /
// Marvel / Goddard / DeKnight -- stacked ahead of a trailing Netflix card right at EOF) already in
// test_materials. The offset below (12s) lands inside the stack, well before that trailing card:
//   $env:BUMPER_CLIP_EPISODE="D:\Data\dev\git\videobumperremover\test_materials\Daredevil\Season 01 - Original\Marvel's Daredevil - S01E01 - Into the Ring.mkv"
//   $env:BUMPER_EPISODES_DIR="D:\Data\dev\git\videobumperremover\test_materials\Daredevil\Season 01 - Original"
//   $env:BUMPER_REGION="end"
//   $env:BUMPER_OFFSET_WINDOW_SECONDS="30"
//   $env:BUMPER_OFFSET_CLIP_START_SECONDS="12"
//   $env:BUMPER_OFFSET_CLIP_LENGTH_SECONDS="8"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~VisualBumperMatcherOffsetTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_OFFSET_SEARCH_SECONDS (default: BUMPER_OFFSET_WINDOW_SECONDS). Set it
// smaller than CLIP_START + CLIP_LENGTH to additionally explore the "search window undershoots"
// failure mode -- this test's default leaves that window comfortably large, since its point is
// the offset/density claim above, not the coverage-gap risk (already understood separately).

using VBR.Core.Extraction;
using VBR.Core.Matching;
using Xunit.Abstractions;

namespace VBR.Tests.Matching;

public class VisualBumperMatcherOffsetTests {
	readonly ITestOutputHelper _out;

	public VisualBumperMatcherOffsetTests(ITestOutputHelper output) => _out = output;

	[SkippableFact]
	public void Match_FindsBumperFromClipExtractedAwayFromTheTrueEdge() {
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		string? regionRaw = Environment.GetEnvironmentVariable("BUMPER_REGION");
		int windowSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_OFFSET_WINDOW_SECONDS"), out var w) ? w : 0;
		int clipStartSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_OFFSET_CLIP_START_SECONDS"), out var cs) ? cs : -1;
		int clipLengthSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_OFFSET_CLIP_LENGTH_SECONDS"), out var cl) ? cl : 0;

		Skip.If(string.IsNullOrWhiteSpace(clipEpisode) || string.IsNullOrWhiteSpace(episodesDir) ||
				string.IsNullOrWhiteSpace(regionRaw) || windowSeconds <= 0 || clipStartSeconds < 0 || clipLengthSeconds <= 0,
			"Set BUMPER_CLIP_EPISODE (source video to auto-extract from), BUMPER_EPISODES_DIR " +
			"(folder of episodes to search), BUMPER_REGION (begin|end), BUMPER_OFFSET_WINDOW_SECONDS " +
			"(a generous window from the true edge containing the whole bumper), " +
			"BUMPER_OFFSET_CLIP_START_SECONDS (how deep into that window, from its far-from-the-true-" +
			"edge side, the reference clip begins) and BUMPER_OFFSET_CLIP_LENGTH_SECONDS to run this test.");
		Skip.If(!File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If(!Enum.TryParse<ClipEdge>(regionRaw, ignoreCase: true, out var region),
			$"BUMPER_REGION must be 'begin' or 'end', got '{regionRaw}'.");

		var window = TimeSpan.FromSeconds(windowSeconds);
		var clipStart = TimeSpan.FromSeconds(clipStartSeconds);
		var clipLength = TimeSpan.FromSeconds(clipLengthSeconds);
		Skip.If(clipStart + clipLength > window,
			$"BUMPER_OFFSET_CLIP_START_SECONDS + BUMPER_OFFSET_CLIP_LENGTH_SECONDS ({(clipStart + clipLength).TotalSeconds}s) " +
			$"must fit inside BUMPER_OFFSET_WINDOW_SECONDS ({window.TotalSeconds}s).");

		int searchSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_OFFSET_SEARCH_SECONDS"), out var ss) && ss > 0 ? ss : windowSeconds;
		var searchLength = TimeSpan.FromSeconds(searchSeconds);

		// Step 1: a generous outer window from the TRUE edge -- guaranteed to contain the whole
		// real bumper (plus margin), same as `vbr match` extracts today.
		using var outerWindow = ClipExtractor.ExtractToTemp(clipEpisode!, ClipRegion.For(region, window));

		// Step 2: the actual reference clip, pulled from INSIDE that window via ClipRegion.At --
		// arbitrary-offset extraction, not anchored to the outer window's own edge. `clipStart` is
		// measured from the side of the window that's FARTHEST from the true file edge, so a small
		// value simulates content deep inside a long bumper: for `end`, that's the window's own
		// start (temp-time 0 == real time `episodeDuration - window`, the farthest point from true
		// EOF); for `begin`, it's the window's own end (temp-time `window` == farthest from true BOF).
		TimeSpan innerStart = region == ClipEdge.end ? clipStart : window - clipStart - clipLength;
		using var referenceClip = ClipExtractor.ExtractToTemp(outerWindow.Path, ClipRegion.At(innerStart, clipLength));

		var searchRegion = ClipRegion.For(region, searchLength);
		using var matcher = new VisualBumperMatcher();
		matcher.PrepareClip(referenceClip.Path);

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
			$"Expected at least one episode to match a reference clip extracted {clipStart.TotalSeconds}s " +
			$"inside the bumper (not anchored to the true '{region}' edge) at >= " +
			$"{VisualBumperMatcher.DefaultPresenceThreshold:P0} presence.");
	}
}
