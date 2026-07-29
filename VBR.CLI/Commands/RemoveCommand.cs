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
using VBR.Core.Removal;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>One row of the remove report. Mirrors <see cref="MatchRow"/> (match/detail fields)
/// plus the removal outcome — kept as its own type rather than extending <c>MatchRow</c> since
/// the extra fields (<c>OutputPath</c>, <c>RemovalError</c>) are meaningless for <c>match</c> and
/// would just be dead weight there.</summary>
internal sealed record RemoveRow(string File, bool Present, string? VisualDetail, string? AudioDetail, string? PHashDetail,
		string? Error, string? OutputPath, string? RemovalError) {
	internal string ToLine() {
		if (Error is not null)
			return $"     {File,-48}  (error: {Error})";
		var parts = new List<string>(3);
		if (VisualDetail is not null) parts.Add($"visual: {VisualDetail}");
		if (AudioDetail is not null) parts.Add($"audio: {AudioDetail}");
		if (PHashDetail is not null) parts.Add($"phash: {PHashDetail}");
		string detail = string.Join("  |  ", parts);
		if (!Present)
			return $"     {File,-48}  {detail}";
		if (RemovalError is not null)
			return $"ERROR    {File,-48}  {detail}  (removal failed: {RemovalError})";
		return $"REMOVED  {File,-48}  {detail}  -> {Path.GetFileName(OutputPath!)}";
	}
}

/// <summary>
/// <c>vbr remove</c> — per ADR 0007 (docs/decisions/0007-removal-command.md): bundles clip
/// extraction + matching + removal in one command, reusing <c>match</c>'s parameter surface.
/// Never modifies the source; writes a sibling <c>name.vbr.ext</c> plus a JSON manifest per cut.
/// Both removal modes are implemented: re-encode (<c>--re-encode true</c>, the default —
/// frame-accurate, correct subtitle realignment, but slow) and stream-copy
/// (<c>--re-encode false</c> — fast, keyframe-bound, built first per the maintainer's stated
/// order for faster iteration while testing).
/// </summary>
internal static class RemoveCommand {

	static readonly Option<bool> ReEncode = new("--re-encode") {
		Description = "Re-encode (Mode B: frame-accurate, correctly realigns subtitle cues) vs. " +
			"stream-copy (Mode A: much faster — no decode/encode — but keyframe-bound, and " +
			"begin-region cuts do NOT realign subtitle cues). Default true. Re-encode decodes " +
			"and re-encodes the entire kept portion of the file, not just the trimmed region, so " +
			"it is far slower than stream-copy — expect it to take roughly as long as encoding " +
			"the video normally would.",
		DefaultValueFactory = _ => true,
	};

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the removal report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	internal static Command Build() {
		var cmd = new Command("remove",
			"Find a bumper's presence across a folder of videos (or a single file, via --file) " +
			"and remove it from each match. Never modifies the source — writes a sibling " +
			"'name.vbr.ext' file instead, plus a JSON manifest recording exactly what was cut. " +
			"Bundles clip extraction, matching, and removal in one command " +
			"(see docs/decisions/0007-removal-command.md).");
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
		cmd.Options.Add(ReEncode);
		cmd.Options.Add(Verbose);

		cmd.SetAction(async (parseResult, ct) => {
			bool reEncode = parseResult.GetValue(ReEncode);
			RemovalMode removalMode = reEncode ? RemovalMode.ReEncode : RemovalMode.StreamCopy;

			var clipFrom = parseResult.GetValue(ClipFrom)!;
			ClipEdge region = parseResult.GetValue(Region);
			TimeSpan clipLength = parseResult.GetValue(ClipLength);
			TimeSpan searchLength = parseResult.GetValue(SearchLength);
			if (searchLength <= TimeSpan.Zero)
				searchLength = clipLength + TimeSpan.FromSeconds(20);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			TimeSpan edgeBoundary = parseResult.GetValue(EdgeBoundary);
			if (edgeBoundary <= TimeSpan.Zero)
				edgeBoundary = TimeSpan.MaxValue;
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

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			CandidateSet? resolved = ResolveCandidates(targetFile, libraries, excludeFolders, recurse, out string? resolveError);
			if (resolved is null) {
				Console.Error.WriteLine(resolveError);
				return 1;
			}
			var (candidatePaths, libraryRoots) = resolved.Value;

			if (region == ClipEdge.begin && !reEncode)
				Console.Error.WriteLine(
					"Note: begin-region stream-copy removal does not realign subtitle cues — " +
					"cues will run out of sync with the removed duration. Use --re-encode true " +
					"(the default) for correct subtitle timing.");
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
				// certainly) also contains the bumper it was enrolled from — skipping it
				// silently left its own copy of the bumper never removed, with no indication
				// anywhere that it had been skipped.
				var candidates = candidatePaths
					// A prior run's own output ("name.vbr.ext") must never be re-matched/re-cut.
					.Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".vbr", StringComparison.OrdinalIgnoreCase))
					.ToList();

				int matchCount = 0;
				int removedCount = 0;
				int comparedCount = 0;
				int dumpIndex = 0;
				var rows = new List<RemoveRow>(candidates.Count);
				foreach (string file in candidates) {
					ct.ThrowIfCancellationRequested();
					string display = DisplayName(file, libraryRoots);
					string dumpLabel = $"{++dumpIndex:000}-{Path.GetFileNameWithoutExtension(file)}";
					RemoveRow row;
					try {
						SignalResult result = session.Compare(file, searchLength, dumpLabel, ct);
						comparedCount++;
						string? outputPath = null;
						string? removalError = null;
						if (result.Present) {
							matchCount++;
							try {
								var removed = ClipRemover.Remove(file, region, clipLength, removalMode,
									result.Visual?.Detail ?? result.Audio?.Detail ?? result.PHash?.Detail, verbose, ct);
								outputPath = removed.OutputPath;
								removedCount++;
							}
							catch (Exception ex) {
								removalError = ex.Message;
							}
						}
						row = new RemoveRow(display, result.Present, result.Visual?.Detail, result.Audio?.Detail, result.PHash?.Detail,
							null, outputPath, removalError);
					}
					catch (Exception ex) {
						row = new RemoveRow(display, false, null, null, null, ex.Message, null, null);
					}
					rows.Add(row);
					Console.WriteLine(row.ToLine());
				}

				string summary = $"{matchCount}/{comparedCount} file(s) matched, {removedCount} removed" +
					(matchCount > removedCount ? $" ({matchCount - removedCount} failed)." : ".") +
					(candidates.Count > comparedCount ? $" ({candidates.Count - comparedCount} skipped with errors.)" : "");
				Console.WriteLine();
				Console.WriteLine(summary);

				if (output is not null && !WriteReport(output, rows, summary,
						clipFrom, region, clipLength, searchLength, sampleInterval, edgeBoundary, sparseInterval, mode,
						presenceThreshold, phashPresenceThreshold, minSimilarity, libraries, excludeFolders, targetFile, recurse, removalMode))
					return 1;
			}
			return 0;
		});

		return cmd;
	}

	static bool WriteReport(FileInfo output, IReadOnlyList<RemoveRow> rows, string summary,
			FileInfo clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, TimeSpan edgeBoundary, TimeSpan sparseInterval, DetectionMode mode,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			IReadOnlyList<DirectoryInfo> libraries, IReadOnlyList<DirectoryInfo> excludeFolders,
			FileInfo? targetFile, bool recurse, RemovalMode removalMode) {
		var report = new StringBuilder();
		report.AppendLine($"vbr remove report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
		report.AppendLine($"mode:           {(removalMode == RemovalMode.ReEncode ? "re-encode (--re-encode true)" : "stream-copy (--re-encode false)")}");
		report.AppendLine(new string('-', 78));
		foreach (RemoveRow row in rows)
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
