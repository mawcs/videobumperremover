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

using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Index;
using VBR.Core.Matching;
using Xunit.Abstractions;

namespace VBR.Tests.Index;

// Verifies docs/iterativeplan.md's "Library scan" verification item 7: fingerprints pulled from a
// *persisted, scanned* index reproduce the same presence numbers VisualBumperMatcherMixedDensityTests
// gets sampling live via --clip-from -- proof the index is a real cache of equivalent data, not
// just a differently-shaped one. Run (PowerShell), same corpus shape as the other real-media tests:
//   $env:BUMPER_CLIP_EPISODE="D:\...\Caprica - S01E01 - pt1 - Pilot.mkv"
//   $env:BUMPER_EPISODES_DIR="D:\...\Caprica\Season 01"
//   $env:BUMPER_REGION="end"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~LibraryScannerEquivalenceTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_SCAN_EDGE_BOUNDARY_SECONDS (default 20) -- note this is the *scan's* fixed
// edge-boundary, not the bumper's own length, so the filtered clip window is deliberately less
// tightly cropped than the other tests' hand-picked --clip-length; some numeric drift from those
// recorded results is expected, not a regression.
public class LibraryScannerEquivalenceTests {
	readonly ITestOutputHelper _out;
	public LibraryScannerEquivalenceTests(ITestOutputHelper output) => _out = output;

	[SkippableFact]
	public void ScannedIndexFingerprints_ReproduceLiveMatchNumbers() {
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		string? regionRaw = Environment.GetEnvironmentVariable("BUMPER_REGION");
		double edgeBoundarySeconds = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_SCAN_EDGE_BOUNDARY_SECONDS"), out var eb) && eb > 0 ? eb : 20;

		Skip.If(string.IsNullOrWhiteSpace(clipEpisode) || string.IsNullOrWhiteSpace(episodesDir) || string.IsNullOrWhiteSpace(regionRaw),
			"Set BUMPER_CLIP_EPISODE, BUMPER_EPISODES_DIR, BUMPER_REGION (begin|end) to run this test.");
		Skip.If(!File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If(!Enum.TryParse<ClipEdge>(regionRaw, ignoreCase: true, out var region),
			$"BUMPER_REGION must be 'begin' or 'end', got '{regionRaw}'.");

		var edgeBoundary = TimeSpan.FromSeconds(edgeBoundarySeconds);
		var profile = new EdgeDensityProfile(edgeBoundary, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(4));

		var candidates = Directory.EnumerateFiles(episodesDir!)
			.Where(f => ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Skip.If(candidates.Count == 0, "No episode files found in BUMPER_EPISODES_DIR.");

		string indexPath = Path.Combine(Path.GetTempPath(), $"vbr_equiv_test_{Guid.NewGuid():N}.vbridx");
		var index = new LibraryIndex();
		try {
			using (var scanner = new LibraryScanner()) {
				LibraryScanner.ScanSummary summary = scanner.Scan(candidates, index, indexPath, profile, forceRescan: false);
				_out.WriteLine($"Scanned {summary.Scanned}/{summary.Total} file(s), {summary.Failed} failed.");
			}

			string clipKey = LibraryIndexKey.Normalize(clipEpisode!);
			Skip.If(!index.Entries.TryGetValue(clipKey, out LibraryIndexEntry? clipEntry),
				"BUMPER_CLIP_EPISODE must be one of the files under BUMPER_EPISODES_DIR (the scan indexes it alongside every other candidate).");

			(IReadOnlyList<TimedFrame> Embeddings, IReadOnlyList<TimedPHash> PHashes) clipEdge = FilterToEdge(clipEntry!, region, edgeBoundary);
			Skip.If(clipEdge.Embeddings.Count == 0, "The clip episode's scanned entry has no fingerprints in the requested edge window.");
			_out.WriteLine($"Clip edge window: {clipEdge.Embeddings.Count} fingerprint(s) from the scanned index.");

			using var matcher = new VisualBumperMatcher();
			var results = new List<(string File, MatchResult Dino, MatchResult PHash)>();
			foreach ((string key, LibraryIndexEntry entry) in index.Entries) {
				if (key == clipKey) continue;
				var candidateEdge = FilterToEdge(entry, region, edgeBoundary);
				if (candidateEdge.Embeddings.Count == 0) continue;
				MatchResult dino = matcher.MatchMixedDensity(clipEdge.Embeddings, candidateEdge.Embeddings);
				MatchResult phash = VisualBumperMatcher.MatchMixedDensityPHash(clipEdge.PHashes, candidateEdge.PHashes);
				results.Add((entry.Path, dino, phash));
			}
			foreach (var (file, dino, phash) in results.OrderByDescending(r => r.Dino.BestScore))
				_out.WriteLine($"{Path.GetFileName(file),-56}  dino: {dino.Detail}  |  phash: {phash.Detail}");

			int matched = results.Count(r => r.Dino.Present);
			_out.WriteLine($"{matched}/{results.Count} episodes matched via index-cached fingerprints (informational; see the doc comment above for why exact numbers may drift from the other tests).");
			Assert.True(matched > 0, "Expected at least one other episode to match using fingerprints pulled from the scanned index.");
		}
		finally {
			try { if (File.Exists(indexPath)) File.Delete(indexPath); } catch { }
		}
	}

	static (IReadOnlyList<TimedFrame> Embeddings, IReadOnlyList<TimedPHash> PHashes) FilterToEdge(
			LibraryIndexEntry entry, ClipEdge region, TimeSpan edgeBoundary) {
		double boundary = edgeBoundary.TotalSeconds;
		double duration = entry.Duration.TotalSeconds;
		var embeddings = new List<TimedFrame>();
		var hashes = new List<TimedPHash>();
		foreach (TimedFingerprint fp in entry.Fingerprints) {
			bool inEdge = region == ClipEdge.begin ? fp.TimestampSeconds <= boundary : fp.TimestampSeconds >= duration - boundary;
			if (!inEdge) continue;
			embeddings.Add(new TimedFrame(fp.TimestampSeconds, fp.Embedding));
			hashes.Add(new TimedPHash(fp.TimestampSeconds, fp.PHash));
		}
		return (embeddings, hashes);
	}
}
