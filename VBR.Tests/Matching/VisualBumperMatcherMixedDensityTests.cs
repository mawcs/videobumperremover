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

// Validates the actual concern behind docs/iterativeplan.md's "Mixed-density edge/middle
// fingerprinting" spike: a bumper that touches the true edge but is LONGER than a small
// `edge-boundary` needs one fingerprint spanning two densities -- dense near the true edge,
// sparse the rest of the way -- and VisualBumperMatcher.MatchMixedDensity must still find it.
// This is a different claim than VisualBumperMatcherOffsetTests (which touches neither true edge,
// single density) -- see the doc for why the two are not interchangeable.
//
// Run (PowerShell), using the real Avatar: The Last Airbender S01 intro (~47s of identical
// content at the true beginning of every episode -- long enough to genuinely exceed a 20s
// edge-boundary):
//   $env:BUMPER_CLIP_EPISODE="D:\Data\dev\git\videobumperremover\test_materials\Avatar\Season 01\Avatar The Last Airbender - S01E01 - The Boy in the Iceberg.mp4"
//   $env:BUMPER_EPISODES_DIR="D:\Data\dev\git\videobumperremover\test_materials\Avatar\Season 01"
//   $env:BUMPER_REGION="begin"
//   $env:BUMPER_MIXED_TOTAL_LENGTH_SECONDS="47"
//   $env:BUMPER_MIXED_EDGE_BOUNDARY_SECONDS="20"
//   $env:BUMPER_MIXED_DENSE_INTERVAL_SECONDS="0.5"
//   $env:BUMPER_MIXED_SPARSE_INTERVAL_SECONDS="4"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~VisualBumperMatcherMixedDensityTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_MIXED_NEGATIVE_DIR (a folder of unrelated content -- e.g. Doctor Who or
// Daredevil -- asserted to produce zero matches).

using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Matching;
using Xunit.Abstractions;

namespace VBR.Tests.Matching;

public class VisualBumperMatcherMixedDensityTests {
	readonly ITestOutputHelper _out;

	public VisualBumperMatcherMixedDensityTests(ITestOutputHelper output) => _out = output;

	[SkippableFact]
	public void MatchMixedDensity_FindsAnEdgeBumperLongerThanTheBoundary() {
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		string? regionRaw = Environment.GetEnvironmentVariable("BUMPER_REGION");
		double totalLengthSeconds = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_MIXED_TOTAL_LENGTH_SECONDS"), out var tl) ? tl : 0;
		double edgeBoundarySeconds = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_MIXED_EDGE_BOUNDARY_SECONDS"), out var eb) ? eb : -1;
		double denseIntervalSeconds = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_MIXED_DENSE_INTERVAL_SECONDS"), out var di) ? di : 0;
		double sparseIntervalSeconds = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_MIXED_SPARSE_INTERVAL_SECONDS"), out var si) ? si : 0;

		Skip.If(string.IsNullOrWhiteSpace(clipEpisode) || string.IsNullOrWhiteSpace(episodesDir) ||
				string.IsNullOrWhiteSpace(regionRaw) || totalLengthSeconds <= 0 || edgeBoundarySeconds < 0 ||
				denseIntervalSeconds <= 0 || sparseIntervalSeconds <= 0,
			"Set BUMPER_CLIP_EPISODE, BUMPER_EPISODES_DIR, BUMPER_REGION (begin|end), " +
			"BUMPER_MIXED_TOTAL_LENGTH_SECONDS (the full known bumper length), " +
			"BUMPER_MIXED_EDGE_BOUNDARY_SECONDS (the ultra-dense zone length), " +
			"BUMPER_MIXED_DENSE_INTERVAL_SECONDS and BUMPER_MIXED_SPARSE_INTERVAL_SECONDS to run this test.");
		Skip.If(!File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If(!Enum.TryParse<ClipEdge>(regionRaw, ignoreCase: true, out var region),
			$"BUMPER_REGION must be 'begin' or 'end', got '{regionRaw}'.");

		var totalLength = TimeSpan.FromSeconds(totalLengthSeconds);
		var profile = new EdgeDensityProfile(
			TimeSpan.FromSeconds(edgeBoundarySeconds), TimeSpan.FromSeconds(denseIntervalSeconds), TimeSpan.FromSeconds(sparseIntervalSeconds));

		using var sampler = new MixedDensitySampler();
		// Only MatchMixedDensity is used -- it never triggers this instance's own ONNX session,
		// so this doesn't double the AI components this test loads.
		using var matcher = new VisualBumperMatcher();

		IReadOnlyList<TimedFrame> clipFrames = sampler.Sample(clipEpisode!, region, totalLength, profile);
		Skip.If(clipFrames.Count == 0,
			"The reference clip produced no usable frames after low-information filtering -- adjust " +
			"BUMPER_MIXED_TOTAL_LENGTH_SECONDS/BUMPER_MIXED_EDGE_BOUNDARY_SECONDS or pick a different clip episode.");
		_out.WriteLine($"Clip fingerprint: {clipFrames.Count} usable frame(s) across {totalLength.TotalSeconds}s " +
			$"({profile.EdgeBoundary.TotalSeconds}s dense @ {profile.DenseInterval.TotalSeconds}s, " +
			$"{(totalLength - profile.EdgeBoundary).TotalSeconds}s sparse @ {profile.SparseInterval.TotalSeconds}s).");

		var episodes = Directory.EnumerateFiles(episodesDir!)
			.Where(f => ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipEpisode!), StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Skip.If(episodes.Count == 0, "No other episode files found in BUMPER_EPISODES_DIR.");

		var results = episodes.Select(ep => {
			IReadOnlyList<TimedFrame> candidateFrames = sampler.Sample(ep, region, totalLength, profile);
			return (File: ep, Result: matcher.MatchMixedDensity(clipFrames, candidateFrames));
		}).ToList();
		foreach (var (file, result) in results.OrderByDescending(r => r.Result.BestScore))
			_out.WriteLine($"{Path.GetFileName(file),-56}  {result.Detail}");

		Assert.True(results.Any(r => r.Result.Present),
			$"Expected at least one episode to match a {totalLengthSeconds}s mixed-density bumper " +
			$"fingerprint ({edgeBoundarySeconds}s dense / {totalLengthSeconds - edgeBoundarySeconds}s sparse) " +
			$"at >= {VisualBumperMatcher.DefaultPresenceThreshold:P0} presence.");

		string? negativeDir = Environment.GetEnvironmentVariable("BUMPER_MIXED_NEGATIVE_DIR");
		if (!string.IsNullOrWhiteSpace(negativeDir) && Directory.Exists(negativeDir)) {
			var negatives = Directory.EnumerateFiles(negativeDir)
				.Where(f => ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();
			var negativeResults = negatives.Select(f => {
				IReadOnlyList<TimedFrame> candidateFrames = sampler.Sample(f, region, totalLength, profile);
				return (File: f, Result: matcher.MatchMixedDensity(clipFrames, candidateFrames));
			}).ToList();
			foreach (var (file, result) in negativeResults.OrderByDescending(r => r.Result.BestScore))
				_out.WriteLine($"[negative] {Path.GetFileName(file),-46}  {result.Detail}");

			Assert.False(negativeResults.Any(r => r.Result.Present),
				$"Expected zero matches against unrelated content in {negativeDir}, got " +
				$"{negativeResults.Count(r => r.Result.Present)}.");
		}
	}
}
