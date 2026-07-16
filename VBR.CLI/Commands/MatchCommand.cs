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

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using VBR.Core.Matching;

namespace VBR.CLI.Commands;

internal static class MatchCommand {

	// CLI numeric arguments parse invariant ('.' decimal) regardless of host locale — see
	// VDF.CLI.Commands.SharedOptions for the same rationale (comma-decimal locales otherwise
	// turn "0.8" into 8).
	static float ParseInvariantFloat(ArgumentResult result, float fallback) {
		if (result.Tokens.Count == 0) return fallback;
		string token = result.Tokens[0].Value;
		if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
			return value;
		result.AddError($"'{token}' is not a valid number (use '.' as the decimal separator, e.g. 0.8).");
		return fallback;
	}

	// Clip extraction is this tool's job, never the user's (see AGENTS.md "Clip extraction is
	// the tool's job" / docs/design/bumper-catalog.md "Precision is the tool's job"): --source
	// points at a real video, and exactly one of --clip-head-seconds / --clip-tail-seconds says
	// which rough region to auto-extract as the reference clip. There is deliberately no
	// "--clip <file>" option that accepts a pre-cut clip.

	static readonly Option<FileInfo> Source = new("--source") {
		Description = "Source video containing the bumper. The reference clip is extracted from " +
			"it internally — this never takes a pre-cut clip file.",
		Required = true,
	};

	static readonly Option<int> ClipHeadSeconds = new("--clip-head-seconds") {
		Description = "Use the first N seconds of --source as the bumper clip. Exactly one of " +
			"--clip-head-seconds / --clip-tail-seconds is required.",
		DefaultValueFactory = _ => 0,
	};

	static readonly Option<int> ClipTailSeconds = new("--clip-tail-seconds") {
		Description = "Use the last N seconds of --source as the bumper clip. Exactly one of " +
			"--clip-head-seconds / --clip-tail-seconds is required.",
		DefaultValueFactory = _ => 0,
	};

	static readonly Option<DirectoryInfo> Library = new("--library") {
		Description = "Folder of video files to search (non-recursive).",
		Required = true,
	};

	static readonly Option<float> MinSimilarity = new("--min-similarity") {
		Description = "Similarity (0-1) at or above which a file is flagged as a match.",
		DefaultValueFactory = _ => AudioBumperMatcher.DefaultMinSimilarity,
		CustomParser = r => ParseInvariantFloat(r, AudioBumperMatcher.DefaultMinSimilarity),
	};

	// Distinct from the --clip-* options above: these narrow the search *inside each candidate
	// library file*, rather than picking the reference clip out of --source.
	static readonly Option<int> SearchHeadSeconds = new("--search-head-seconds") {
		Description = "Also search only the first N seconds of each library file (rescues short audible bumpers).",
		DefaultValueFactory = _ => 0,
	};

	static readonly Option<int> SearchTailSeconds = new("--search-tail-seconds") {
		Description = "Also search only the last N seconds of each library file (rescues short audible bumpers).",
		DefaultValueFactory = _ => 0,
	};

	internal static Command Build() {
		var cmd = new Command("match",
			"Find a bumper's audio fingerprint inside every video in a library folder. The bumper " +
			"clip is extracted internally from --source — you never provide a pre-cut clip.");
		cmd.Options.Add(Source);
		cmd.Options.Add(ClipHeadSeconds);
		cmd.Options.Add(ClipTailSeconds);
		cmd.Options.Add(Library);
		cmd.Options.Add(MinSimilarity);
		cmd.Options.Add(SearchHeadSeconds);
		cmd.Options.Add(SearchTailSeconds);

		cmd.SetAction((parseResult, ct) => {
			var source = parseResult.GetValue(Source)!;
			int clipHeadSeconds = parseResult.GetValue(ClipHeadSeconds);
			int clipTailSeconds = parseResult.GetValue(ClipTailSeconds);
			var library = parseResult.GetValue(Library)!;
			float minSimilarity = parseResult.GetValue(MinSimilarity);
			int searchHeadSeconds = parseResult.GetValue(SearchHeadSeconds);
			int searchTailSeconds = parseResult.GetValue(SearchTailSeconds);

			if ((clipHeadSeconds > 0) == (clipTailSeconds > 0)) {
				Console.Error.WriteLine("Error: specify exactly one of --clip-head-seconds or --clip-tail-seconds.");
				return Task.FromResult(1);
			}
			ClipRegion region = clipHeadSeconds > 0
				? ClipRegion.Head(TimeSpan.FromSeconds(clipHeadSeconds))
				: ClipRegion.Tail(TimeSpan.FromSeconds(clipTailSeconds));

			IReadOnlyList<BumperMatch> results;
			try {
				results = AudioBumperMatcher.FindInLibrary(
					source.FullName, region, library.FullName, searchHeadSeconds, searchTailSeconds, ct);
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
					or InvalidOperationException or ArgumentOutOfRangeException) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return Task.FromResult(1);
			}

			static string Cell(string label, WindowResult? r) =>
				r is { } w ? $"  {label}={w.Similarity,6:P0}@{w.OffsetSeconds,5}s" : "";

			bool IsMatch(BumperMatch m) =>
				(m.Full?.Similarity ?? 0) >= minSimilarity ||
				(m.Head?.Similarity ?? 0) >= minSimilarity ||
				(m.Tail?.Similarity ?? 0) >= minSimilarity;

			int matchCount = 0;
			foreach (var m in results.OrderByDescending(m => m.Full?.Similarity ?? 0)) {
				bool isMatch = IsMatch(m);
				if (isMatch) matchCount++;
				string flag = isMatch ? "MATCH" : "     ";
				Console.WriteLine($"{flag}  {Path.GetFileName(m.FilePath),-48}{Cell("full", m.Full)}{Cell("head", m.Head)}{Cell("tail", m.Tail)}");
			}

			Console.WriteLine();
			Console.WriteLine($"{matchCount}/{results.Count} file(s) matched at >= {minSimilarity:P0} " +
				$"({results.Count} of the folder's video files had a usable audio track).");
			return Task.FromResult(0);
		});

		return cmd;
	}
}
