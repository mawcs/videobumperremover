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
using VBR.Core.Removal;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>One row of the trim report — no match detail (there is no matching), just the cut's
/// own outcome per file.</summary>
internal sealed record TrimRow(string File, string? OutputPath, string? Error) {
	internal string ToLine() =>
		Error is not null
			? $"ERROR    {File,-48}  ({Error})"
			: $"TRIMMED  {File,-48}  -> {Path.GetFileName(OutputPath!)}";
}

/// <summary>
/// <c>vbr trim</c> — unconditionally cuts a fixed <c>--length</c> from the <c>--region</c> edge of
/// every file resolved from <c>--paths</c>, via the same <see cref="ClipRemover.Remove"/> mechanism
/// <c>remove</c> uses for the actual cut. No matching, no fingerprints, no catalog concept at all —
/// this is a standalone top-level command, not a <c>remove</c> mode (docs/iterativeplan.md,
/// "Per-bumper matching strategy" entry, Change 1: "There are enough differences in the command
/// structure for the user to see it as a separate command"), because <c>remove</c>'s entire matching
/// surface (<c>--detection-mode</c>, <c>--presence-threshold</c>, <c>--catalog-db</c>, ...) is
/// meaningless here. Every resolved candidate is trimmed with zero content verification — a folder
/// entry containing files that don't actually have the described segment is silently truncated too;
/// that's inherent to "no matching, on purpose," not a flaw to design around.
/// </summary>
internal static class TrimCommand {
	static readonly Option<TimeSpan> Length = new("--length") {
		Description = "How much to cut from --region, unconditionally. A plain number of seconds, " +
			"or suffixed like '8s' / '5.1s'.",
		Required = true,
		CustomParser = r => ParseDurationArg(r, TimeSpan.Zero),
	};

	static readonly Option<ClipEdge> Region = new("--region") {
		Description = "Which edge to cut from (begin|end).",
		Required = true,
	};

	// Deliberately not SharedOptions.Library/TargetFile/LibraryDb -- this command replaces all three
	// with one option (docs/iterativeplan.md, Change 1: "instead of supporting 'library' or anything
	// like that, it should just support a semicolon-delimited list of paths of either files, or
	// parent folders"). Existence is validated here at parse time (a missing entry is a parse-time
	// error, same convention SharedOptions.Library's own CustomParser already uses); which folder
	// entries actually contain trims-worthy files is resolved later, in the action, since that also
	// needs --exclude-folders/--no-recurse which aren't available to a CustomParser.
	static readonly Option<string[]> Paths = new("--paths") {
		Description = "Semicolon-delimited list of files and/or folders to trim (e.g. " +
			"\"D:\\Show\\ep1.mkv;D:\\Extras\"). A file entry is trimmed as-is (no extension filter, " +
			"same trust convention --file uses elsewhere on other commands); a folder entry is " +
			"walked for recognized video files (see --no-recurse/--exclude-folders). Replaces " +
			"--library/--file/--library-db entirely for this command.",
		Required = true,
		CustomParser = ParsePathListArg,
	};

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the trim report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	static string[] ParsePathListArg(ArgumentResult result) {
		if (result.Tokens.Count == 0) return Array.Empty<string>();
		var paths = new List<string>();
		foreach (string piece in result.Tokens[0].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (!File.Exists(piece) && !Directory.Exists(piece)) {
				result.AddError($"Path not found (neither a file nor a folder): {piece}");
				continue;
			}
			paths.Add(piece);
		}
		return paths.ToArray();
	}

	internal static Command Build() {
		var cmd = new Command("trim",
			"Cut a fixed --length from the --region edge of every file under --paths, unconditionally " +
			"-- no matching, no fingerprints, just a direct cut via the same mechanism 'vbr remove' " +
			"uses for its own cut. For when you already know exactly what needs to come off (e.g. a " +
			"fixed-length intro every file in a folder shares) and don't need presence detection.");
		cmd.Options.Add(Length);
		cmd.Options.Add(Region);
		cmd.Options.Add(Paths);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(ExcludeFolders);
		cmd.Options.Add(ReEncode);
		cmd.Options.Add(Output);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			// Genuinely async (2026-08-15), not a sync body wrapped in Task.FromResult -- this was
			// the one command in the codebase built that way, and it matters: an exception thrown
			// inside a plain (non-async) delegate propagates SYNCHRONOUSLY out of the delegate call
			// itself, whereas an async method's compiler-generated state machine always captures any
			// exception onto the returned Task instead, even with no real "await" inside. Ctrl+C
			// cancellation ("backgrounds the process with no way to foreground it") relies on
			// OperationCanceledException reaching System.CommandLine's own invocation pipeline the
			// normal way every other command's genuinely-async handler already delivers it.
			await Task.Yield();

			TimeSpan length = parseResult.GetValue(Length);
			if (length <= TimeSpan.Zero) {
				Console.Error.WriteLine("Error: --length must be positive.");
				return 1;
			}
			ClipEdge region = parseResult.GetValue(Region);
			string[] rawPaths = parseResult.GetValue(Paths) ?? Array.Empty<string>();
			if (rawPaths.Length == 0) {
				Console.Error.WriteLine("Error: --paths is required.");
				return 1;
			}
			bool recurse = !parseResult.GetValue(NoRecurse);
			DirectoryInfo[] excludeFolders = parseResult.GetValue(ExcludeFolders) ?? Array.Empty<DirectoryInfo>();
			bool reEncode = parseResult.GetValue(ReEncode);
			RemovalMode removalMode = reEncode ? RemovalMode.ReEncode : RemovalMode.StreamCopy;
			FileInfo? output = parseResult.GetValue(Output);
			bool verbose = parseResult.GetValue(Verbose);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);
			HardwareAcceleration.ReportDecodeRequest();

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			// ---- Resolve --paths: files trusted as-is, folders walked -- same dedup/exclude/`.vbr.`
			// filtering RemoveCommand's own folder walk applies (docs/iterativeplan.md, "Per-bumper
			// matching strategy" entry, Change 1). Existence was already checked at parse time
			// (ParsePathListArg), so every raw entry here is genuinely a file or a folder.
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var candidatePaths = new List<string>();
			foreach (string raw in rawPaths) {
				if (File.Exists(raw)) {
					if (seen.Add(Path.GetFullPath(raw)))
						candidatePaths.Add(raw);
				}
				else {
					foreach (string f in Directory.EnumerateFiles(raw, "*",
							new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true })) {
						if (!ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
						if (IsUnderAny(f, excludeFolders)) continue;
						if (seen.Add(Path.GetFullPath(f)))
							candidatePaths.Add(f);
					}
				}
			}
			// A prior run's own output ("name.vbr.ext") must never be re-cut -- same rule
			// RemoveCommand applies to its own resolved candidates, for the same reason.
			candidatePaths = candidatePaths
				.Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".vbr", StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (candidatePaths.Count == 0) {
				Console.Error.WriteLine("Error: --paths resolved to no video files.");
				return 1;
			}

			var rows = new List<TrimRow>(candidatePaths.Count);
			int trimmedCount = 0;
			int fileIndex = 0;
			foreach (string file in candidatePaths) {
				ct.ThrowIfCancellationRequested();
				string display = Path.GetFileName(file);
				// Printed unconditionally, before the cut starts -- same rationale RemoveCommand's own
				// per-file line documents: a re-encode can run long with nothing else to show for it.
				Console.Error.WriteLine($"[{++fileIndex}/{candidatePaths.Count}] Trimming: {display}");
				bool printedProgress = false;
				void OnRemovalProgress(RemovalProgress p) {
					printedProgress = true;
					double fraction = p.Total > TimeSpan.Zero ? Math.Clamp(p.Processed.TotalSeconds / p.Total.TotalSeconds, 0, 1) : 0;
					string speedText = p.SpeedMultiplier is { } s ? $"{s:0.##}x" : "?";
					Console.Error.Write($"\r    {fraction:P0}  ({FormatSeconds(p.Processed)} / {FormatSeconds(p.Total)}, {speedText} realtime)   ");
				}
				TrimRow row;
				try {
					var removed = ClipRemover.Remove(file, region, length, removalMode,
						matchDetail: null, verbose, OnRemovalProgress, ct);
					if (printedProgress) Console.Error.WriteLine();
					trimmedCount++;
					row = new TrimRow(display, removed.OutputPath, null);
				}
				// OperationCanceledException must NOT be caught here (2026-08-15 -- Ctrl+C "backgrounds
				// the process with no way to foreground it"): ClipRemover.Remove's own cancellation
				// path throws it once ffmpeg is killed, and swallowing it as an ordinary per-file error
				// let the loop carry on to the next --paths entry (or, with a single entry, just finish
				// normally) instead of actually stopping -- the ffmpeg subprocess was correctly killed
				// either way, but this process itself never exited in response to the request. Letting
				// it propagate is what the unguarded ct.ThrowIfCancellationRequested() at the top of
				// this loop already relies on for every other cancellation point.
				catch (Exception ex) when (ex is not OperationCanceledException) {
					if (printedProgress) Console.Error.WriteLine();
					row = new TrimRow(display, null, ex.Message);
				}
				rows.Add(row);
				Console.WriteLine(row.ToLine());
			}

			string summary = $"{trimmedCount}/{candidatePaths.Count} file(s) trimmed" +
				(trimmedCount < candidatePaths.Count ? $" ({candidatePaths.Count - trimmedCount} failed)." : ".");
			Console.WriteLine();
			Console.WriteLine(summary);

			if (output is not null && !WriteReport(output, rows, summary, length, region, rawPaths, excludeFolders, recurse, removalMode))
				return 1;

			return 0;
		});

		return cmd;
	}

	static bool WriteReport(FileInfo output, IReadOnlyList<TrimRow> rows, string summary,
			TimeSpan length, ClipEdge region, IReadOnlyList<string> rawPaths, IReadOnlyList<DirectoryInfo> excludeFolders,
			bool recurse, RemovalMode removalMode) {
		var report = new StringBuilder();
		report.AppendLine($"vbr trim report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"region: {region}   length: {FormatSeconds(length)}   ({(recurse ? "recursive" : "top level only")})"));
		report.AppendLine($"paths:          {string.Join("; ", rawPaths)}");
		if (excludeFolders.Count > 0)
			report.AppendLine($"exclude-folders: {string.Join("; ", excludeFolders.Select(e => e.FullName))}");
		report.AppendLine($"mode:           {(removalMode == RemovalMode.ReEncode ? "re-encode (--re-encode true)" : "stream-copy (--re-encode false)")}");
		report.AppendLine(new string('-', 78));
		foreach (TrimRow row in rows)
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
