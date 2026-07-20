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
using System.Text;
using VBR.Core.Cleanup;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>
/// <c>vbr cleanup</c> — per ADR 0008 (docs/decisions/0008-cleanup-command.md): promotes verified
/// <c>.vbr.</c> outputs to replace their originals. The only command permitted to delete video
/// files. Core algorithm lives in <see cref="VBR.Core.Cleanup.LibraryCleaner"/>; this file is just
/// CLI wiring — resolving <c>--library</c>/<c>--file</c>, walking directories for the former,
/// and reporting results.
/// </summary>
internal static class CleanupCommand {

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the cleanup report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	internal static Command Build() {
		var cmd = new Command("cleanup",
			"Promote verified '.vbr.' outputs (from a prior 'vbr remove' run) to replace their " +
			"originals — deletes the pre-cut original and the manifest. The only command that " +
			"deletes video files (see docs/decisions/0008-cleanup-command.md). Run this between " +
			"bumper-removal passes, after reviewing the '.vbr.' outputs, not instead of reviewing them.");
		cmd.Options.Add(Library);
		cmd.Options.Add(TargetFile);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(ValidateFiles);
		cmd.Options.Add(Output);
		cmd.Options.Add(Verbose);

		cmd.SetAction((parseResult, ct) => {
			var library = parseResult.GetValue(Library);
			var targetFile = parseResult.GetValue(TargetFile);
			bool recurse = !parseResult.GetValue(NoRecurse);
			bool validateFiles = parseResult.GetValue(ValidateFiles);
			FileInfo? output = parseResult.GetValue(Output);
			bool verbose = parseResult.GetValue(Verbose);

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			if (targetFile is null && library is null) {
				Console.Error.WriteLine("Error: one of --library or --file is required.");
				return Task.FromResult(1);
			}
			if (targetFile is not null && library is not null) {
				Console.Error.WriteLine("Error: specify only one of --library or --file, not both.");
				return Task.FromResult(1);
			}

			var results = new List<CleanupFileResult>();
			var recoveries = new List<RecoveryAction>();
			string? libraryRoot = null;

			if (targetFile is not null) {
				if (!targetFile.Exists) {
					Console.Error.WriteLine($"Error: File not found: {targetFile.FullName}");
					return Task.FromResult(1);
				}
				// Decided (ADR 0008, Decision 10): --file touches only this one file's own
				// pairing/marker state, never anything else sharing its directory.
				var (result, recovery) = LibraryCleaner.CleanSingleFile(targetFile.FullName, validateFiles, verbose, ct);
				if (recovery is not null) {
					recoveries.Add(recovery);
					Console.WriteLine(ToLine(recovery, libraryRoot: null));
				}
				results.Add(result);
				Console.WriteLine(ToLine(result, libraryRoot: null));
			}
			else {
				if (!library!.Exists) {
					Console.Error.WriteLine($"Error: Library folder not found: {library.FullName}");
					return Task.FromResult(1);
				}
				libraryRoot = library.FullName;
				foreach (string directory in EnumerateDirectories(library.FullName, recurse)) {
					ct.ThrowIfCancellationRequested();
					CleanupRunResult dirResult = LibraryCleaner.CleanDirectory(directory, validateFiles, verbose, ct);
					foreach (RecoveryAction recovery in dirResult.RecoveryActions) {
						recoveries.Add(recovery);
						Console.WriteLine(ToLine(recovery, libraryRoot));
					}
					foreach (CleanupFileResult result in dirResult.Results) {
						results.Add(result);
						Console.WriteLine(ToLine(result, libraryRoot));
					}
				}
			}

			string summary = Summarize(results, recoveries);
			Console.WriteLine();
			Console.WriteLine(summary);

			if (output is not null && !WriteReport(output, results, recoveries, summary, libraryRoot, library, targetFile, recurse, validateFiles))
				return Task.FromResult(1);

			return Task.FromResult(0);
		});

		return cmd;
	}

	// The library folder itself, plus every subdirectory when recursing — cleanup processes one
	// directory at a time (ADR 0008, Decision 2), unlike match/remove's flat candidate list.
	static IEnumerable<string> EnumerateDirectories(string root, bool recurse) {
		yield return root;
		if (!recurse) yield break;
		foreach (string dir in Directory.EnumerateDirectories(root, "*",
				new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
			yield return dir;
	}

	static string ToLine(CleanupFileResult result, string? libraryRoot) {
		string display = DisplayName(result.OriginalPath, libraryRoot);
		string tag = result.Outcome switch {
			CleanupOutcome.Cleaned => "CLEANED",
			CleanupOutcome.Broken => "BROKEN ",
			CleanupOutcome.PendingReclamation => "PENDING",
			CleanupOutcome.Skipped => "SKIPPED",
			_ => "?",
		};
		return result.Detail is null ? $"{tag}  {display,-48}" : $"{tag}  {display,-48}  ({result.Detail})";
	}

	static string ToLine(RecoveryAction recovery, string? libraryRoot) =>
		$"RECOVER  {DisplayName(recovery.OriginalPath, libraryRoot),-48}  {recovery.Detail}";

	static string Summarize(IReadOnlyList<CleanupFileResult> results, IReadOnlyList<RecoveryAction> recoveries) {
		int cleaned = results.Count(r => r.Outcome == CleanupOutcome.Cleaned);
		int broken = results.Count(r => r.Outcome == CleanupOutcome.Broken);
		int pending = results.Count(r => r.Outcome == CleanupOutcome.PendingReclamation);
		int skipped = results.Count(r => r.Outcome == CleanupOutcome.Skipped);
		string summary = $"{cleaned} cleaned, {broken} broken, {pending} pending reclamation" +
			(skipped > 0 ? $", {skipped} skipped" : "") + ".";
		return recoveries.Count > 0 ? $"{summary} ({recoveries.Count} recovery action(s) taken.)" : summary;
	}

	static bool WriteReport(FileInfo output, IReadOnlyList<CleanupFileResult> results,
			IReadOnlyList<RecoveryAction> recoveries, string summary, string? libraryRoot,
			DirectoryInfo? library, FileInfo? targetFile, bool recurse, bool validateFiles) {
		var report = new StringBuilder();
		report.AppendLine($"vbr cleanup report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		report.AppendLine(targetFile is not null
			? $"file:           {targetFile.FullName}"
			: $"library:        {library!.FullName}   ({(recurse ? "recursive" : "top level only")})");
		report.AppendLine($"validate-files: {validateFiles}");
		report.AppendLine(new string('-', 78));
		foreach (RecoveryAction recovery in recoveries)
			report.AppendLine(ToLine(recovery, libraryRoot));
		foreach (CleanupFileResult result in results)
			report.AppendLine(ToLine(result, libraryRoot));
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
