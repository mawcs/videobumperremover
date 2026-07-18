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
using System.Text;
using VBR.Core.Extraction;
using VBR.Core.Matching;
using VDF.Core.AI;

namespace VBR.CLI.Commands;

/// <summary>Which signal(s) <c>vbr match</c> runs. Lowercase members — see
/// VBR.Core.Extraction.ClipEdge for why.</summary>
internal enum DetectionMode { visual, audio, both }

/// <summary>One row of the match report — kept structured (rather than formatting straight to
/// the console) so the same rows can be written to --output, and a machine-readable format
/// (e.g. JSON) can serialize them later without reshaping the command. <c>File</c> is the
/// library-relative path; exactly one of <c>Error</c> or the detail fields is populated.</summary>
internal sealed record MatchRow(string File, bool Present, string? VisualDetail, string? AudioDetail, string? Error) {
	internal string ToLine() {
		if (Error is not null)
			return $"     {File,-48}  (error: {Error})";
		var parts = new List<string>(2);
		if (VisualDetail is not null) parts.Add(VisualDetail);
		if (AudioDetail is not null) parts.Add($"[{AudioDetail}]");
		return $"{(Present ? "MATCH" : "     ")}  {File,-48}  {string.Join("  ", parts)}";
	}
}

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

	// Accepts a bare number (seconds) or a suffixed value ("5.1s", "200ms").
	static TimeSpan ParseDuration(string text) {
		text = text.Trim();
		double unitSeconds = 1.0;
		string numberPart = text;
		if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
			numberPart = text[..^2];
			unitSeconds = 0.001;
		}
		else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
			numberPart = text[..^1];
		}
		if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
			throw new FormatException(
				$"'{text}' is not a valid duration (use a plain number of seconds, or a suffixed value like '5.1s' or '200ms').");
		return TimeSpan.FromSeconds(value * unitSeconds);
	}

	static TimeSpan ParseDurationArg(ArgumentResult result, TimeSpan fallback) {
		if (result.Tokens.Count == 0) return fallback;
		try { return ParseDuration(result.Tokens[0].Value); }
		catch (FormatException ex) {
			result.AddError(ex.Message);
			return fallback;
		}
	}

	static readonly Option<FileInfo> ClipFrom = new("--clip-from") {
		Description = "Source video containing the bumper. The reference clip is extracted from " +
			"it internally — this never takes a pre-cut clip file.",
		Required = true,
	};

	static readonly Option<ClipEdge> Region = new("--region") {
		Description = "Which edge the bumper lives at (begin|end). Drives both reference-clip " +
			"extraction from --clip-from and the search window in each --library file — a bumper " +
			"lives at one edge, so one choice governs both.",
		Required = true,
	};

	static readonly Option<TimeSpan> ClipLength = new("--clip-length") {
		Description = "How much of --clip-from to extract as the reference clip. A plain number " +
			"of seconds, or suffixed like '8s' / '5.1s'.",
		Required = true,
		CustomParser = r => ParseDurationArg(r, TimeSpan.Zero),
	};

	// No DefaultValueFactory: the real default (--clip-length + 20s) depends on another option's
	// value, so it can't be precomputed for the help-text annotation. TimeSpan.Zero here means
	// "not provided" — resolved against --clip-length in the action. A user-requested zero-length
	// search window would be meaningless anyway, so treating <= 0 as "unset" is unambiguous.
	static readonly Option<TimeSpan> SearchLength = new("--search-length") {
		Description = "How much of each candidate's edge to search. Default: --clip-length + 20s " +
			"(the search window needs slack beyond the clip's own length).",
		CustomParser = r => r.Tokens.Count == 0 ? TimeSpan.Zero : ParseDurationArg(r, TimeSpan.Zero),
	};

	static readonly Option<TimeSpan> SampleInterval = new("--sample-interval") {
		Description = "Visual: seconds between sampled frames — smaller is denser. Default 1s; " +
			"short clips (under ~8s) need it as low as ~0.2s to have enough frames to match on " +
			"— no floor is enforced, go as dense as needed.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds)),
	};

	static readonly Option<float> PresenceThreshold = new("--presence-threshold") {
		Description = "Visual: cosine similarity (0-1) at or above which a clip frame counts as present in a candidate.",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultPresenceThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultPresenceThreshold),
	};

	static readonly Option<float> RigidHitThreshold = new("--rigid-hit-threshold") {
		Description = "Visual: cosine threshold for VDF's rigid corroborating matcher (report-only, never gates the decision).",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultRigidHitThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultRigidHitThreshold),
	};

	static readonly Option<float> MinSimilarity = new("--min-similarity") {
		Description = "Audio: similarity (0-1) at or above which a file is flagged as a match.",
		DefaultValueFactory = _ => AudioBumperMatcher.DefaultMinSimilarity,
		CustomParser = r => ParseInvariantFloat(r, AudioBumperMatcher.DefaultMinSimilarity),
	};

	static readonly Option<DetectionMode> Mode = new("--detection-mode") {
		Description = "Which signal(s) to run (visual|audio|both). 'both' runs visual as the " +
			"decision-maker and reports audio alongside as corroboration.",
		DefaultValueFactory = _ => DetectionMode.visual,
	};

	static readonly Option<DirectoryInfo> Library = new("--library") {
		Description = "Folder of video files to search. Subfolders are traversed by default — see --no-recurse.",
		Required = true,
	};

	static readonly Option<bool> NoRecurse = new("--no-recurse") {
		Description = "Search only the top level of --library instead of traversing its subfolders.",
	};

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the match report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	static readonly Option<DirectoryInfo> DumpFrames = new("--dump-frames") {
		Description = "Diagnostic (visual only): write every sampled frame as a PNG under this " +
			"folder — the reference clip's frames under clip/, each candidate's search window " +
			"under a numbered subfolder — to inspect exactly what the matcher compared. Frame " +
			"fNNN sits at NNN × --sample-interval into its extracted clip/window.",
	};

	internal static Command Build() {
		var cmd = new Command("match",
			"Find a bumper's presence across a folder of videos. Visual DINOv2 presence matching " +
			"runs by default; the reference clip is extracted internally from --clip-from — you " +
			"never provide a pre-cut clip.");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(SearchLength);
		cmd.Options.Add(SampleInterval);
		cmd.Options.Add(PresenceThreshold);
		cmd.Options.Add(RigidHitThreshold);
		cmd.Options.Add(MinSimilarity);
		cmd.Options.Add(Mode);
		cmd.Options.Add(Library);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(Output);
		cmd.Options.Add(DumpFrames);

		cmd.SetAction(async (parseResult, ct) => {
			var clipFrom = parseResult.GetValue(ClipFrom)!;
			ClipEdge region = parseResult.GetValue(Region);
			TimeSpan clipLength = parseResult.GetValue(ClipLength);
			TimeSpan searchLength = parseResult.GetValue(SearchLength);
			if (searchLength <= TimeSpan.Zero)
				searchLength = clipLength + TimeSpan.FromSeconds(20);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			float presenceThreshold = parseResult.GetValue(PresenceThreshold);
			float rigidHitThreshold = parseResult.GetValue(RigidHitThreshold);
			float minSimilarity = parseResult.GetValue(MinSimilarity);
			DetectionMode mode = parseResult.GetValue(Mode);
			var library = parseResult.GetValue(Library)!;
			bool recurse = !parseResult.GetValue(NoRecurse);
			FileInfo? output = parseResult.GetValue(Output);
			DirectoryInfo? dumpFrames = parseResult.GetValue(DumpFrames);

			VisualBumperMatcher? visual = null;
			try {
				if (dumpFrames is not null && mode is DetectionMode.audio)
					Console.Error.WriteLine("Note: --dump-frames applies to visual matching only; --detection-mode audio dumps nothing.");
				if (mode is DetectionMode.visual or DetectionMode.both) {
					if (!AiComponents.IsReady) {
						Console.Error.WriteLine("AI matching components not found — downloading (one-time, ~100MB)...");
						await AiComponents.DownloadAsync(progress: null, ct);
						Console.Error.WriteLine("AI components ready.");
					}
					visual = new VisualBumperMatcher(sampleInterval.TotalSeconds, presenceThreshold, rigidHitThreshold,
						dumpFramesDir: dumpFrames?.FullName);
				}
				AudioBumperMatcher? audio = mode is DetectionMode.audio or DetectionMode.both
					? new AudioBumperMatcher(minSimilarity)
					: null;

				ExtractedClip referenceClip;
				try {
					referenceClip = ClipExtractor.ExtractToTemp(clipFrom.FullName, ClipRegion.For(region, clipLength));
				}
				catch (Exception ex) when (ex is FileNotFoundException or ArgumentOutOfRangeException or InvalidOperationException) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}

				using (referenceClip) {
					if (!library.Exists) {
						Console.Error.WriteLine($"Error: Library folder not found: {library.FullName}");
						return 1;
					}

					ClipRegion searchRegion = ClipRegion.For(region, searchLength);
					var candidates = Directory.EnumerateFiles(library.FullName, "*",
							new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true })
						.Where(f => ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
						.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipFrom.FullName), StringComparison.OrdinalIgnoreCase))
						.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
						.ToList();

					int matchCount = 0;
					int comparedCount = 0;
					var rows = new List<MatchRow>(candidates.Count);
					foreach (string file in candidates) {
						ct.ThrowIfCancellationRequested();
						// Library-relative: with recursive traversal, same-named files in
						// different subfolders must stay distinguishable.
						string display = Path.GetRelativePath(library.FullName, file);
						MatchResult? visualResult = null;
						MatchResult? audioResult = null;
						MatchRow row;
						try {
							if (visual != null) visualResult = visual.Match(referenceClip.Path, file, searchRegion, ct);
							if (audio != null) audioResult = audio.Match(referenceClip.Path, file, searchRegion, ct);
							comparedCount++;
							bool present = visualResult?.Present ?? audioResult?.Present ?? false;
							if (present) matchCount++;
							row = new MatchRow(display, present, visualResult?.Detail, audioResult?.Detail, null);
						}
						catch (Exception ex) {
							row = new MatchRow(display, false, null, null, ex.Message);
						}
						rows.Add(row);
						Console.WriteLine(row.ToLine());
					}

					string summary = $"{matchCount}/{comparedCount} file(s) matched" +
						(candidates.Count > comparedCount ? $" ({candidates.Count - comparedCount} skipped with errors)." : ".");
					Console.WriteLine();
					Console.WriteLine(summary);

					if (output is not null && !WriteReport(output, rows, summary,
							clipFrom, region, clipLength, searchLength, sampleInterval, mode,
							presenceThreshold, rigidHitThreshold, minSimilarity, library, recurse))
						return 1;
				}
				return 0;
			}
			finally {
				visual?.Dispose();
			}
		});

		return cmd;
	}

	static string FormatSeconds(TimeSpan t) =>
		t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

	/// <summary>Writes the report (parameter header + the same rows/summary the console showed)
	/// to <paramref name="output"/>. Returns false after printing an error if the file could not
	/// be written — the caller turns that into a nonzero exit code, since the user explicitly
	/// asked for the file.</summary>
	static bool WriteReport(FileInfo output, IReadOnlyList<MatchRow> rows, string summary,
			FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, DetectionMode mode, float presenceThreshold,
			float rigidHitThreshold, float minSimilarity, DirectoryInfo library, bool recurse) {
		var report = new StringBuilder();
		report.AppendLine($"vbr match report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		report.AppendLine($"clip-from:      {clipFrom.FullName}");
		report.AppendLine($"region: {region}   clip-length: {FormatSeconds(clipLength)}   " +
			$"search-length: {FormatSeconds(searchLength)}   sample-interval: {FormatSeconds(sampleInterval)}");
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"detection-mode: {mode}   presence-threshold: {presenceThreshold:0.###}   " +
			$"rigid-hit-threshold: {rigidHitThreshold:0.###}   min-similarity: {minSimilarity:0.###}"));
		report.AppendLine($"library:        {library.FullName}   ({(recurse ? "recursive" : "top level only")})");
		report.AppendLine(new string('-', 78));
		foreach (MatchRow row in rows)
			report.AppendLine(row.ToLine());
		report.AppendLine();
		report.AppendLine(summary);
		try {
			if (output.DirectoryName is { Length: > 0 } dir)
				Directory.CreateDirectory(dir);
			File.WriteAllText(output.FullName, report.ToString());
			Console.Error.WriteLine($"Report written to: {output.FullName}");
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
			Console.Error.WriteLine($"Error: could not write report to '{output.FullName}': {ex.Message}");
			return false;
		}
	}
}
