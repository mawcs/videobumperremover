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
using VBR.Core.Fingerprinting;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>One row of the match report — kept structured (rather than formatting straight to
/// the console) so the same rows can be written to --output, and a machine-readable format
/// (e.g. JSON) can serialize them later without reshaping the command. <c>File</c> is the
/// library-relative path (or just the file name for a --file target); exactly one of
/// <c>Error</c> or the detail fields is populated.</summary>
internal sealed record MatchRow(string File, bool Present, string? VisualDetail, string? AudioDetail, string? PHashDetail, string? Error) {
	internal string ToLine() {
		if (Error is not null)
			return $"     {File,-48}  (error: {Error})";
		var parts = new List<string>(3);
		if (VisualDetail is not null) parts.Add($"visual: {VisualDetail}");
		if (AudioDetail is not null) parts.Add($"audio: {AudioDetail}");
		if (PHashDetail is not null) parts.Add($"phash: {PHashDetail}");
		return $"{(Present ? "MATCH" : "     ")}  {File,-48}  {string.Join("  |  ", parts)}";
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
			"Visual DINOv2 presence matching runs by default; the reference clip is sampled " +
			"internally from --clip-from — you never provide a pre-cut clip.");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(SearchLength);
		cmd.Options.Add(SampleInterval);
		cmd.Options.Add(EdgeBoundary);
		cmd.Options.Add(SparseInterval);
		cmd.Options.Add(PresenceThreshold);
		cmd.Options.Add(PHashPresenceThreshold);
		cmd.Options.Add(MinSimilarity);
		cmd.Options.Add(Mode);
		cmd.Options.Add(Library);
		cmd.Options.Add(ExcludeFolders);
		cmd.Options.Add(TargetFile);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(Output);
		cmd.Options.Add(DumpFrames);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			var clipFrom = parseResult.GetValue(ClipFrom);
			ClipEdge? regionArg = parseResult.GetValue(Region);
			TimeSpan clipLength = parseResult.GetValue(ClipLength);

			// --clip-from/--region/--clip-length lost their declarative Required=true (they're
			// shared Option instances, and remove now needs them optional for --bumper-label — see
			// docs/iterativeplan.md, "Utilizing Databases" entry) -- match still requires all three,
			// just checked here instead of by the parser.
			if (clipFrom is null) {
				Console.Error.WriteLine("Error: --clip-from is required.");
				return 1;
			}
			if (regionArg is null) {
				Console.Error.WriteLine("Error: --region is required.");
				return 1;
			}
			if (clipLength <= TimeSpan.Zero) {
				Console.Error.WriteLine("Error: --clip-length is required.");
				return 1;
			}
			ClipEdge region = regionArg.Value;
			TimeSpan searchLength = parseResult.GetValue(SearchLength);
			if (searchLength <= TimeSpan.Zero)
				searchLength = clipLength + TimeSpan.FromSeconds(20);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			TimeSpan edgeBoundary = parseResult.GetValue(EdgeBoundary);
			if (edgeBoundary <= TimeSpan.Zero)
				edgeBoundary = TimeSpan.MaxValue; // clamps to whatever totalLength each call uses -- "always dense"
			TimeSpan sparseInterval = parseResult.GetValue(SparseInterval);
			if (sparseInterval <= TimeSpan.Zero)
				sparseInterval = sampleInterval;
			var profile = new EdgeDensityProfile(edgeBoundary, sampleInterval, sparseInterval);
			float presenceThreshold = parseResult.GetValue(PresenceThreshold);
			float phashPresenceThreshold = parseResult.GetValue(PHashPresenceThreshold);
			float minSimilarity = parseResult.GetValue(MinSimilarity);
			DetectionMode mode = parseResult.GetValue(Mode);
			DirectoryInfo[] libraries = parseResult.GetValue(Library) ?? Array.Empty<DirectoryInfo>();
			DirectoryInfo[] excludeFolders = parseResult.GetValue(ExcludeFolders) ?? Array.Empty<DirectoryInfo>();
			var targetFile = parseResult.GetValue(TargetFile);
			bool recurse = !parseResult.GetValue(NoRecurse);
			FileInfo? output = parseResult.GetValue(Output);
			DirectoryInfo? dumpFrames = parseResult.GetValue(DumpFrames);
			bool verbose = parseResult.GetValue(Verbose);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			CandidateSet? resolved = ResolveCandidates(targetFile, libraries, excludeFolders, recurse, out string? resolveError);
			if (resolved is null) {
				Console.Error.WriteLine(resolveError);
				return 1;
			}
			var (candidatePaths, libraryRoots) = resolved.Value;

			if (dumpFrames is not null && mode is DetectionMode.audio)
				Console.Error.WriteLine("Note: --dump-frames applies to visual/pHash matching only; --detection-mode audio dumps nothing.");

			(MatchingSession? session, string? prepareError) = await MatchingSession.PrepareAsync(
				mode, clipFrom, region, clipLength, profile, presenceThreshold, phashPresenceThreshold,
				minSimilarity, dumpFrames?.FullName, verbose, ct);
			if (session is null) {
				Console.Error.WriteLine(prepareError);
				return 1;
			}

			using (session) {
				// --clip-from is NOT excluded: it's a normal candidate that (almost
				// certainly) also contains the bumper it was enrolled from, and silently
				// skipping it left it unreported with no indication anywhere that happened.

				int matchCount = 0;
				int comparedCount = 0;
				int dumpIndex = 0;
				var rows = new List<MatchRow>(candidatePaths.Count);
				foreach (string file in candidatePaths) {
					ct.ThrowIfCancellationRequested();
					string display = DisplayName(file, libraryRoots);
					string dumpLabel = $"{++dumpIndex:000}-{Path.GetFileNameWithoutExtension(file)}";
					MatchRow row;
					try {
						SignalResult result = session.Compare(file, searchLength, dumpLabel, ct);
						comparedCount++;
						if (result.Present) matchCount++;
						row = new MatchRow(display, result.Present, result.Visual?.Detail, result.Audio?.Detail, result.PHash?.Detail, null);
					}
					catch (Exception ex) {
						row = new MatchRow(display, false, null, null, null, ex.Message);
					}
					rows.Add(row);
					Console.WriteLine(row.ToLine());
				}

				string summary = $"{matchCount}/{comparedCount} file(s) matched" +
					(candidatePaths.Count > comparedCount ? $" ({candidatePaths.Count - comparedCount} skipped with errors)." : ".");
				Console.WriteLine();
				Console.WriteLine(summary);

				if (output is not null && !WriteReport(output, rows, summary,
						clipFrom, region, clipLength, searchLength, sampleInterval, edgeBoundary, sparseInterval, mode,
						presenceThreshold, phashPresenceThreshold, minSimilarity, libraries, excludeFolders, targetFile, recurse))
					return 1;
			}
			return 0;
		});

		return cmd;
	}

	/// <summary>Writes the report (parameter header + the same rows/summary the console showed)
	/// to <paramref name="output"/>. Returns false after printing an error if the file could not
	/// be written — the caller turns that into a nonzero exit code, since the user explicitly
	/// asked for the file.</summary>
	static bool WriteReport(FileInfo output, IReadOnlyList<MatchRow> rows, string summary,
			FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, TimeSpan edgeBoundary, TimeSpan sparseInterval, DetectionMode mode,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			IReadOnlyList<DirectoryInfo> libraries, IReadOnlyList<DirectoryInfo> excludeFolders,
			FileInfo? targetFile, bool recurse) {
		var report = new StringBuilder();
		report.AppendLine($"vbr match report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		report.AppendLine($"clip-from:      {clipFrom.FullName}");
		report.AppendLine($"region: {region}   clip-length: {FormatSeconds(clipLength)}   " +
			$"search-length: {FormatSeconds(searchLength)}   sample-interval: {FormatSeconds(sampleInterval)}");
		report.AppendLine($"edge-boundary: {(edgeBoundary == TimeSpan.MaxValue ? "(whole window, single-density)" : FormatSeconds(edgeBoundary))}   " +
			$"sparse-interval: {FormatSeconds(sparseInterval)}");
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"detection-mode: {mode}   presence-threshold: {presenceThreshold:0.###}   " +
			$"phash-presence-threshold: {phashPresenceThreshold:0.###}   min-similarity: {minSimilarity:0.###}"));
		report.AppendLine(targetFile is not null
			? $"file:           {targetFile.FullName}"
			: $"library:        {string.Join("; ", libraries.Select(l => l.FullName))}   ({(recurse ? "recursive" : "top level only")})");
		if (excludeFolders.Count > 0)
			report.AppendLine($"exclude-folders: {string.Join("; ", excludeFolders.Select(e => e.FullName))}");
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
