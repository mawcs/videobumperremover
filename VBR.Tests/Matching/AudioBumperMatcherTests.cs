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

// Graduated from VDF.IntegrationTests/Comparison/BumperMatchProbe.cs (ADR 0005): same
// env-var-driven real-media check, now exercising VBR.Core's production AudioBumperMatcher.
// Env var naming matches VisualTailProbe's BUMPER_CLIP_EPISODE convention (both auto-extract
// the reference clip from a real episode — no pre-cut clip files, per AGENTS.md "Clip
// extraction is the tool's job").
//
// Run (PowerShell):
//   $env:BUMPER_CLIP_EPISODE="D:\michael\Desktop\Season 01\S01E01.mkv"
//   $env:BUMPER_CLIP_TAIL_SECONDS="40"      # or BUMPER_CLIP_HEAD_SECONDS — exactly one
//   $env:BUMPER_EPISODES_DIR="D:\michael\Desktop\Season 01"
//   dotnet test VBR.Tests --filter "FullyQualifiedName~AudioBumperMatcherTests" -l "console;verbosity=detailed"
//
// Optional: $env:BUMPER_SEARCH_HEAD_SECONDS / $env:BUMPER_SEARCH_TAIL_SECONDS to also search
// positional windows within each candidate file.

using VBR.Core.Matching;
using Xunit.Abstractions;

namespace VBR.Tests.Matching;

public class AudioBumperMatcherTests {
	readonly ITestOutputHelper _out;

	public AudioBumperMatcherTests(ITestOutputHelper output) => _out = output;

	[SkippableFact]
	public void FindInLibrary_MatchesBumperAcrossEpisodes() {
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		int clipHeadSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_CLIP_HEAD_SECONDS"), out var chs) ? chs : 0;
		int clipTailSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_CLIP_TAIL_SECONDS"), out var cts) ? cts : 0;

		Skip.If(string.IsNullOrWhiteSpace(clipEpisode) || string.IsNullOrWhiteSpace(episodesDir),
			"Set BUMPER_CLIP_EPISODE (source video to auto-extract the bumper clip from), " +
			"BUMPER_EPISODES_DIR (folder of episodes), and exactly one of BUMPER_CLIP_HEAD_SECONDS / " +
			"BUMPER_CLIP_TAIL_SECONDS to run this test.");
		Skip.If(!File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If((clipHeadSeconds > 0) == (clipTailSeconds > 0),
			"Set exactly one of BUMPER_CLIP_HEAD_SECONDS / BUMPER_CLIP_TAIL_SECONDS (neither or both is ambiguous).");

		var region = clipHeadSeconds > 0
			? ClipRegion.Head(TimeSpan.FromSeconds(clipHeadSeconds))
			: ClipRegion.Tail(TimeSpan.FromSeconds(clipTailSeconds));

		int searchHeadSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_SEARCH_HEAD_SECONDS"), out var hs) ? hs : 0;
		int searchTailSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_SEARCH_TAIL_SECONDS"), out var ts) ? ts : 0;

		var results = AudioBumperMatcher.FindInLibrary(clipEpisode!, region, episodesDir!, searchHeadSeconds, searchTailSeconds);
		Assert.NotEmpty(results);

		foreach (var m in results.OrderByDescending(m => m.Full?.Similarity ?? 0)) {
			string full = m.Full is { } f ? $"full={f.Similarity,6:P0}@{f.OffsetSeconds,5}s" : "full=n/a";
			string head = m.Head is { } h ? $"  head={h.Similarity,6:P0}@{h.OffsetSeconds,4}s" : "";
			string tail = m.Tail is { } t ? $"  tail={t.Similarity,6:P0}@{t.OffsetSeconds,5}s" : "";
			_out.WriteLine($"{Path.GetFileName(m.FilePath),-48}  {full}{head}{tail}");
		}

		Assert.True(results.Any(m =>
				(m.Full?.Similarity ?? 0) >= AudioBumperMatcher.DefaultMinSimilarity ||
				(m.Head?.Similarity ?? 0) >= AudioBumperMatcher.DefaultMinSimilarity ||
				(m.Tail?.Similarity ?? 0) >= AudioBumperMatcher.DefaultMinSimilarity),
			$"Expected at least one episode to match the bumper clip at >= {AudioBumperMatcher.DefaultMinSimilarity:P0}.");
	}
}
