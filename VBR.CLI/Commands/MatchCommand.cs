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
using System.Globalization;
using System.Text;
using VBR.Core.Extraction;
using VBR.Core.Matching;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>One row of the match report — kept structured (rather than formatting straight to
/// the console) so the same rows can be written to --output, and a machine-readable format
/// (e.g. JSON) can serialize them later without reshaping the command. <c>File</c> is the
/// library-relative path (or just the file name for a --file target); exactly one of
/// <c>Error</c> or the detail fields is populated.</summary>
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

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the match report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	internal static Command Build() {
		var cmd = new Command("match",
			"Find a bumper's presence across a folder of videos (or a single file, via --file). " +
			"Visual DINOv2 presence matching runs by default; the reference clip is extracted " +
			"internally from --clip-from — you never provide a pre-cut clip.");
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
		cmd.Options.Add(TargetFile);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(Output);
		cmd.Options.Add(DumpFrames);
		cmd.Options.Add(Verbose);

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
			var library = parseResult.GetValue(Library);
			var targetFile = parseResult.GetValue(TargetFile);
			bool recurse = !parseResult.GetValue(NoRecurse);
			FileInfo? output = parseResult.GetValue(Output);
			DirectoryInfo? dumpFrames = parseResult.GetValue(DumpFrames);
			bool verbose = parseResult.GetValue(Verbose);

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			CandidateSet? resolved = ResolveCandidates(targetFile, library, recurse, out string? resolveError);
			if (resolved is null) {
				Console.Error.WriteLine(resolveError);
				return 1;
			}
			var (candidatePaths, libraryRoot) = resolved.Value;

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
						dumpFramesDir: dumpFrames?.FullName, verboseLogging: verbose);
				}
				AudioBumperMatcher? audio = mode is DetectionMode.audio or DetectionMode.both
					? new AudioBumperMatcher(minSimilarity, verboseLogging: verbose)
					: null;

				ExtractedClip referenceClip;
				try {
					referenceClip = ClipExtractor.ExtractToTemp(clipFrom.FullName, ClipRegion.For(region, clipLength), verbose);
				}
				catch (Exception ex) when (ex is FileNotFoundException or ArgumentOutOfRangeException or InvalidOperationException) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}

				using (referenceClip) {
					// Embed + validate the clip once up front: an all-black/blank clip is an input
					// error (matching on it only fabricates false positives), so fail the run with
					// one clear message instead of erroring on every candidate row. Also warms the
					// per-instance clip-embedding cache the per-file loop reuses.
					if (visual is not null) {
						try {
							visual.PrepareClip(referenceClip.Path, ct);
						}
						catch (InvalidOperationException ex) {
							Console.Error.WriteLine($"Error: {ex.Message}");
							return 1;
						}
					}

					ClipRegion searchRegion = ClipRegion.For(region, searchLength);
					// --clip-from is NOT excluded: it's a normal candidate that (almost
					// certainly) also contains the bumper it was enrolled from, and silently
					// skipping it left it unreported with no indication anywhere that happened.

					int matchCount = 0;
					int comparedCount = 0;
					var rows = new List<MatchRow>(candidatePaths.Count);
					foreach (string file in candidatePaths) {
						ct.ThrowIfCancellationRequested();
						string display = DisplayName(file, libraryRoot);
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
						(candidatePaths.Count > comparedCount ? $" ({candidatePaths.Count - comparedCount} skipped with errors)." : ".");
					Console.WriteLine();
					Console.WriteLine(summary);

					if (output is not null && !WriteReport(output, rows, summary,
							clipFrom, region, clipLength, searchLength, sampleInterval, mode,
							presenceThreshold, rigidHitThreshold, minSimilarity, library, targetFile, recurse))
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

	/// <summary>Writes the report (parameter header + the same rows/summary the console showed)
	/// to <paramref name="output"/>. Returns false after printing an error if the file could not
	/// be written — the caller turns that into a nonzero exit code, since the user explicitly
	/// asked for the file.</summary>
	static bool WriteReport(FileInfo output, IReadOnlyList<MatchRow> rows, string summary,
			FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, DetectionMode mode, float presenceThreshold,
			float rigidHitThreshold, float minSimilarity, DirectoryInfo? library, FileInfo? targetFile, bool recurse) {
		var report = new StringBuilder();
		report.AppendLine($"vbr match report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		report.AppendLine($"clip-from:      {clipFrom.FullName}");
		report.AppendLine($"region: {region}   clip-length: {FormatSeconds(clipLength)}   " +
			$"search-length: {FormatSeconds(searchLength)}   sample-interval: {FormatSeconds(sampleInterval)}");
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"detection-mode: {mode}   presence-threshold: {presenceThreshold:0.###}   " +
			$"rigid-hit-threshold: {rigidHitThreshold:0.###}   min-similarity: {minSimilarity:0.###}"));
		report.AppendLine(targetFile is not null
			? $"file:           {targetFile.FullName}"
			: $"library:        {library!.FullName}   ({(recurse ? "recursive" : "top level only")})");
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
