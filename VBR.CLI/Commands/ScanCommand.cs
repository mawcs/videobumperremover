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
using VBR.Core.Database;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>How much reporting detail <c>vbr scan</c> produces, for the console
/// (<c>--console-info</c>) and the log file (<c>--log-file</c>/<c>--log-level</c>) independently.
/// Ordered low-to-high so callers can compare with <c>&gt;=</c>. <c>trace</c> is reserved for
/// finer-grained diagnostic detail than any statement in the codebase emits today — it behaves the
/// same as <c>verbose</c> until something actually logs at that granularity.</summary>
internal enum ScanReportLevel { quiet, info, debug, verbose, trace }

/// <summary>
/// <c>vbr scan</c> — builds/updates a named library's cached fingerprint database
/// (docs/iterativeplan.md, "Library scan — cached fingerprint index"). Samples every candidate
/// file's true edges (dense) and whole-file middle (sparse) up front so a bumper can be found later
/// without re-decoding — but doesn't know or check *which* bumper; that stays a separate, later
/// catalog-apply effort (decision 1). No <c>--clip-from</c>/<c>--region</c>/<c>--detection-mode</c>
/// here — there's nothing to match against yet, only fingerprints to gather.
/// </summary>
internal static class ScanCommand {
	static readonly Option<TimeSpan> EdgeBoundary = new("--edge-boundary") {
		Description = "How deep from each file's true BOF/EOF is sampled densely (--sample-interval); " +
			"sampled sparser beyond it (--sparse-interval). Default 20s.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(20),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(20)),
	};

	static readonly Option<TimeSpan> SampleInterval = new("--sample-interval") {
		Description = "Dense interval: seconds between sampled frames within --edge-boundary of each " +
			"true edge. Default 0.2s.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(0.2),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(0.2)),
	};

	static readonly Option<TimeSpan> SparseInterval = new("--sparse-interval") {
		Description = "Sparse interval: seconds between sampled frames across the whole file " +
			"(the dense edge zones get denser coverage on top of this, not instead of it). Default 4s.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(4),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(4)),
	};

	static readonly Option<bool> IncludeVbrOutputs = new("--include-vbr-outputs") {
		Description = "Also include 'name.vbr.ext' outputs from a prior 'vbr remove' run — excluded " +
			"by default since they're transitional staging artifacts (a review window before " +
			"'vbr commit' promotes or discards them) that usually near-duplicate the original.",
	};

	static readonly Option<bool> Rescan = new("--rescan") {
		Description = "Bypass change detection and re-sample every candidate, even ones the cache " +
			"says are unchanged. Needed after a sampling-parameter change to invalidate stale entries.",
		Aliases = { "--force" },
	};

	static readonly Option<ScanReportLevel?> ConsoleInfo = new("--console-info") {
		Description = "How much progress detail to print to the console: quiet (nothing); info (a " +
			"running x/total counter -- the default); debug (each file's name+result on its own " +
			"line, plus an x/total progress line); verbose (debug's lines plus the underlying " +
			"model-load/frame-count/checkpoint log detail); trace (reserved for finer-grained detail " +
			"-- same as verbose today). The final summary and any error are always printed regardless " +
			"of this setting. --verbose is shorthand for '--console-info verbose'; an explicit " +
			"--console-info wins if both are given.",
	};

	static readonly Option<FileInfo> LogFile = new("--log-file") {
		Description = "Where to write this scan's log (appended to, so repeated runs accumulate " +
			"history). Default: sibling to the database file, same library name with a .log extension.",
	};

	static readonly Option<ScanReportLevel> LogLevel = new("--log-level") {
		Description = "How much detail to write to --log-file -- quiet|info|debug|verbose|trace, " +
			"same meanings as --console-info but applied to the file instead of the console " +
			"(independently: a quiet console with a verbose log file is the point). Default: verbose.",
		DefaultValueFactory = _ => ScanReportLevel.verbose,
	};

	internal static Command Build() {
		var cmd = new Command("scan",
			"Build or update a library's cached fingerprint database — samples every file's true edges " +
			"and whole-file middle so a bumper can be found later without re-decoding. Does not match " +
			"against any specific bumper; see 'vbr match'/'vbr remove' for that.");
		cmd.Options.Add(Library);
		cmd.Options.Add(ExcludeFolders);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(EdgeBoundary);
		cmd.Options.Add(SampleInterval);
		cmd.Options.Add(SparseInterval);
		cmd.Options.Add(LibraryName);
		cmd.Options.Add(LibraryDbFolder);
		cmd.Options.Add(IncludeVbrOutputs);
		cmd.Options.Add(Rescan);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(ConsoleInfo);
		cmd.Options.Add(LogFile);
		cmd.Options.Add(LogLevel);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			DirectoryInfo[] libraries = parseResult.GetValue(Library) ?? Array.Empty<DirectoryInfo>();
			DirectoryInfo[] excludeFolders = parseResult.GetValue(ExcludeFolders) ?? Array.Empty<DirectoryInfo>();
			bool recurse = !parseResult.GetValue(NoRecurse);
			TimeSpan edgeBoundary = parseResult.GetValue(EdgeBoundary);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			TimeSpan sparseInterval = parseResult.GetValue(SparseInterval);
			string? libraryNameArg = parseResult.GetValue(LibraryName);
			DirectoryInfo? libraryDbFolderArg = parseResult.GetValue(LibraryDbFolder);
			bool includeVbrOutputs = parseResult.GetValue(IncludeVbrOutputs);
			bool rescan = parseResult.GetValue(Rescan);
			bool verboseFlag = parseResult.GetValue(Verbose);
			ScanReportLevel? consoleInfoArg = parseResult.GetValue(ConsoleInfo);
			FileInfo? logFileArg = parseResult.GetValue(LogFile);
			ScanReportLevel fileLevel = parseResult.GetValue(LogLevel);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);

			// An explicit --console-info wins; otherwise --verbose is shorthand for "verbose", else
			// the default is "info" (today's plain x/total counter).
			ScanReportLevel consoleLevel = consoleInfoArg ?? (verboseFlag ? ScanReportLevel.verbose : ScanReportLevel.info);

			using IDisposable? consoleLogSubscription = SubscribeVerboseLogging(consoleLevel >= ScanReportLevel.verbose);

			if (libraries.Length == 0) {
				Console.Error.WriteLine("Error: --library is required.");
				return 1;
			}

			CandidateSet? resolved = ResolveCandidates(file: null, libraries, excludeFolders, recurse, out string? resolveError);
			if (resolved is null) {
				Console.Error.WriteLine(resolveError);
				return 1;
			}
			IReadOnlyList<string> candidatePaths = resolved.Value.Files;
			if (!includeVbrOutputs)
				candidatePaths = candidatePaths
					.Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".vbr", StringComparison.OrdinalIgnoreCase))
					.ToList();
			if (candidatePaths.Count == 0) {
				Console.Error.WriteLine("No candidate files found.");
				return 1;
			}

			// With multiple --library folders, a default name can only ever be a guess -- derived
			// from the first folder given, same "override if you care" philosophy as the
			// single-folder case, just extended to pick one of several.
			string libraryName = string.IsNullOrWhiteSpace(libraryNameArg)
				? LibraryDatabaseStore.DeriveLibraryName(libraries[0].FullName)
				: libraryNameArg;

			// Checked here, before any scanning (or even the AI-component download) starts, not left
			// for LibraryDatabaseStore.Save to discover: a file already sitting at --library-db-folder's
			// path can never work as a folder to hold the database, and it's not worth wasting an entire
			// run's sampling work to find that out only once a save is attempted.
			if (libraryDbFolderArg is not null && File.Exists(libraryDbFolderArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --library-db-folder must be a folder, but a file already exists there: '{libraryDbFolderArg.FullName}'.");
				return 1;
			}
			// Same class of mistake --library-db-folder used to be exposed to before it became
			// folder-only (docs/iterativeplan.md, "Post-ship fix #2") -- --log-file is a *file* path,
			// so a trailing separator or an existing directory there is just as much a guaranteed,
			// never-going-to-work destination, worth catching up front rather than discovering only
			// once the first log write silently no-ops.
			if (logFileArg is not null && (
					logFileArg.FullName.EndsWith(Path.DirectorySeparatorChar) ||
					logFileArg.FullName.EndsWith(Path.AltDirectorySeparatorChar) ||
					Directory.Exists(logFileArg.FullName))) {
				Console.Error.WriteLine($"Error: --log-file must name a file, not a directory: '{logFileArg.FullName}'.");
				return 1;
			}

			string databasePath = LibraryDatabaseStore.ResolveDatabasePath(libraryDbFolderArg?.FullName, libraryName);
			string logPath = logFileArg?.FullName ??
				Path.Combine(Path.GetDirectoryName(databasePath)!, Path.GetFileNameWithoutExtension(databasePath) + ".log");

			// Open-write-close per line (mirrors VDF.Core.Utils.Logger.Add) rather than holding one
			// handle open for the whole scan: keeps writes resilient to another process (antivirus, a
			// tail, another vbr instance) briefly touching the file, and a write failure here must
			// never be allowed to crash the scan the way an unprotected database save once did (fixed
			// 2026-07-26) -- so it's swallowed the same way Logger's own writes are.
			void WriteLogLine(string line) {
				if (fileLevel == ScanReportLevel.quiet) return;
				try {
					using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
					using var writer = new StreamWriter(stream);
					writer.WriteLine($"{DateTime.Now:HH:mm:ss} => {line}");
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}

			using IDisposable? fileLogSubscription = SubscribeLogging(fileLevel >= ScanReportLevel.verbose, WriteLogLine);

			// Logger statements gate whether verbose-tier detail (model path, per-file frame counts,
			// checkpoint saves/failures) is even *raised* as an event at all, independent of whether
			// either destination is currently listening for it -- so it must be requested if *either*
			// the console or the file wants it, not just the console the way a plain --verbose bool
			// used to decide alone.
			bool emitDetailedLogging = consoleLevel >= ScanReportLevel.verbose || fileLevel >= ScanReportLevel.verbose;

			await EnsureAiComponentsReadyAsync(HardwareAcceleration.PreferDirectML, ct);

			LibraryDatabase database;
			try {
				database = LibraryDatabaseStore.Load(databasePath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return 1;
			}
			database.LibraryName = libraryName;
			database.EdgeBoundarySeconds = edgeBoundary.TotalSeconds;
			database.DenseIntervalSeconds = sampleInterval.TotalSeconds;
			database.SparseIntervalSeconds = sparseInterval.TotalSeconds;
			var profile = new EdgeDensityProfile(edgeBoundary, sampleInterval, sparseInterval);

			string startAnnouncement = $"Scanning '{string.Join("; ", libraries.Select(l => l.FullName))}' -> database '{databasePath}' ({candidatePaths.Count} candidate file(s))...";
			Console.Error.WriteLine(startAnnouncement);
			WriteLogLine(startAnnouncement);

			int sampledCount = 0, skippedCount = 0, failedCount = 0;
			void OnFileScanned(LibraryScanner.FileScanResult result) {
				switch (result.Outcome) {
					case LibraryScanner.ScanOutcome.Sampled: sampledCount++; break;
					case LibraryScanner.ScanOutcome.SkippedUnchanged: skippedCount++; break;
					case LibraryScanner.ScanOutcome.Failed: failedCount++; break;
				}
				int done = sampledCount + skippedCount + failedCount;

				string tag = result.Outcome switch {
					LibraryScanner.ScanOutcome.Sampled => "SAMPLED",
					LibraryScanner.ScanOutcome.SkippedUnchanged => "SKIPPED",
					_ => "FAILED ",
				};
				string resultLine = $"{tag}  {Path.GetFileName(result.Path),-56}  {result.Error ?? result.Detail}";
				string progressLine = $"{done}/{candidatePaths.Count}";

				if (consoleLevel >= ScanReportLevel.debug) {
					Console.WriteLine(resultLine);
					Console.WriteLine(progressLine);
				}
				else if (consoleLevel == ScanReportLevel.info) {
					Console.Error.Write($"\rScanning: {done}/{candidatePaths.Count}  " +
						$"(sampled {sampledCount}, unchanged {skippedCount}, failed {failedCount})   ");
				}
				// quiet: nothing to the console.

				if (fileLevel >= ScanReportLevel.debug) {
					WriteLogLine(resultLine);
					WriteLogLine(progressLine);
				}
				// info/quiet: no per-file lines in the log either -- info's file record is just the
				// start/end announcements (a live-updating counter is a terminal idiom that doesn't
				// translate to an appended file).
			}

			using var scanner = new LibraryScanner(emitDetailedLogging);
			LibraryScanner.ScanSummary summary;
			try {
				summary = scanner.Scan(candidatePaths, database, databasePath, profile, rescan, OnFileScanned, ct);
			}
			catch (OperationCanceledException) {
				const string cancelMessage = "Cancelled — progress up to the last checkpoint was saved.";
				Console.Error.WriteLine();
				Console.Error.WriteLine(cancelMessage);
				WriteLogLine(cancelMessage);
				return 1;
			}

			if (consoleLevel == ScanReportLevel.info)
				Console.Error.WriteLine();
			string summaryLine = $"{summary.Scanned} sampled, {summary.SkippedUnchanged} unchanged (skipped), " +
				$"{summary.Failed} failed, {summary.Total} total.";
			string databaseLine = $"Database: {databasePath}";
			Console.WriteLine(summaryLine);
			Console.WriteLine(databaseLine);
			WriteLogLine(summaryLine);
			WriteLogLine(databaseLine);

			if (summary.DatabaseSaveError is not null) {
				string errorLine1 = $"Error: could not save the database: {summary.DatabaseSaveError}";
				const string errorLine2 = "This run's results were not persisted -- re-run 'vbr scan' once the " +
					"underlying issue clears (a common cause is antivirus or another process briefly locking " +
					"the database file). Files already written by an earlier checkpoint this run are unaffected.";
				Console.Error.WriteLine(errorLine1);
				Console.Error.WriteLine(errorLine2);
				WriteLogLine(errorLine1);
				WriteLogLine(errorLine2);
				return 1;
			}
			return 0;
		});

		return cmd;
	}
}
