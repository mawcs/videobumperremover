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
using VBR.Core.Configuration;
using VBR.Core.Database;
using VBR.Core.Diagnostics;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>How much reporting detail <c>vbr scan</c> produces, for the console
/// (<c>--console-info</c>) and the log file (<c>--log-file</c>/<c>--log-level</c>) independently.
/// Ordered low-to-high so callers can compare with <c>&gt;=</c>. <c>debug</c> adds per-file
/// diagnostic detail (frame counts, low-information-filter drops, audio-fingerprint stats --
/// <see cref="VBR.Core.Diagnostics.ScanTelemetry.NoteDebug"/>) on top of its own per-file
/// name+result line. <c>verbose</c> adds model path/session lifecycle/checkpoint detail on top of
/// that. <c>trace</c> is one step finer still: execution timing
/// (<see cref="VBR.Core.Diagnostics.ScanTelemetry.Time"/>) for every measured phase -- AI/DirectML
/// readiness, per-file ffprobe/sampling/inference, native vs. CLI ffmpeg decode, database
/// checkpointing -- for diagnosing a slow run by phase instead of guessing. debug/trace are both
/// off by default (near-zero cost) since most runs don't need them.</summary>
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
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanEdgeBoundarySeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanEdgeBoundarySeconds)),
	};

	static readonly Option<TimeSpan> SampleInterval = new("--sample-interval") {
		Description = "Dense interval: seconds between sampled frames within --edge-boundary of each " +
			"true edge. Default 0.2s.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanDenseIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanDenseIntervalSeconds)),
	};

	static readonly Option<TimeSpan> SparseInterval = new("--sparse-interval") {
		Description = "Sparse interval: seconds between sampled frames across the whole file " +
			"(the dense edge zones get denser coverage on top of this, not instead of it). Default 4s.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanSparseIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VbrConfig.Current.Sampling.ScanSparseIntervalSeconds)),
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
			"line, an x/total progress line, plus per-file diagnostic detail -- frame counts, " +
			"low-information-filter drops, audio-fingerprint stats); verbose (debug's lines plus " +
			"the underlying model-load/session-lifecycle/checkpoint log detail); trace (verbose's " +
			"lines plus per-phase execution timing -- AI/DirectML readiness, per-file " +
			"ffprobe/sampling/inference, native vs. CLI ffmpeg decode, database checkpointing -- " +
			"for diagnosing a slow run by phase). The final summary and any error are always " +
			"printed regardless of this setting. --verbose is shorthand for '--console-info " +
			"verbose'; an explicit --console-info wins if both are given.",
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

	/// <summary>Compact ETA text for the progress reporting below: seconds under a minute, whole
	/// minutes under an hour, else hours+minutes. A negative/zero <paramref name="eta"/> (the last
	/// file or an average skewed by an unusually slow one) prints as "0s" rather than a negative
	/// value.</summary>
	static string FormatEta(TimeSpan eta) {
		if (eta < TimeSpan.Zero) eta = TimeSpan.Zero;
		if (eta.TotalMinutes < 1) return $"{eta.TotalSeconds:0}s";
		if (eta.TotalHours < 1) return $"{eta.TotalMinutes:0}m";
		return $"{(int)eta.TotalHours}h {eta.Minutes}m";
	}

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
		cmd.Options.Add(LibraryDb);
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
			FileInfo? libraryDbArg = parseResult.GetValue(LibraryDb);
			bool includeVbrOutputs = parseResult.GetValue(IncludeVbrOutputs);
			bool rescan = parseResult.GetValue(Rescan);
			bool verboseFlag = parseResult.GetValue(Verbose);
			ScanReportLevel? consoleInfoArg = parseResult.GetValue(ConsoleInfo);
			FileInfo? logFileArg = parseResult.GetValue(LogFile);
			ScanReportLevel fileLevel = parseResult.GetValue(LogLevel);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);
			HardwareAcceleration.ReportDecodeRequest();

			// An explicit --console-info wins; otherwise --verbose is shorthand for "verbose", else
			// the default is "info" (today's plain x/total counter).
			ScanReportLevel consoleLevel = consoleInfoArg ?? (verboseFlag ? ScanReportLevel.verbose : ScanReportLevel.info);

			// Trace: one step finer than verbose -- execution timing for diagnosing a slow run by
			// phase, independently requestable per destination just like every other level here.
			bool traceConsole = consoleLevel >= ScanReportLevel.trace;
			bool traceFile = fileLevel >= ScanReportLevel.trace;
			ScanTelemetry.Enabled = traceConsole || traceFile;
			var commandStopwatch = System.Diagnostics.Stopwatch.StartNew();

			// Debug: per-file diagnostic detail (frame counts, low-information-filter drops,
			// audio-fingerprint stats) -- coarser than trace, doesn't need full verbose to be useful.
			bool debugConsole = consoleLevel >= ScanReportLevel.debug;
			bool debugFile = fileLevel >= ScanReportLevel.debug;
			ScanTelemetry.DebugEnabled = debugConsole || debugFile;

			// Stays >= verbose, not >= debug: VDF's shared Logger bus carries no per-message tier, so
			// lowering this threshold would leak whatever's raised for verbose (e.g. because the
			// *other* destination defaults to verbose -- --log-level's own default) into a console
			// that only asked for debug. ChromaprintEngine's stats line is reported through
			// ScanTelemetry.NoteDebug instead (see SampleFile), which -- like every other debug/trace
			// line -- is correctly isolated per destination without touching this subscription.
			using IDisposable? consoleLogSubscription = SubscribeVerboseLogging(consoleLevel >= ScanReportLevel.verbose);

			if (libraries.Length == 0) {
				Console.Error.WriteLine("Error: --library is required.");
				return 1;
			}

			// Console-only here (not the full ScanTelemetry event pipeline) -- the trace subscription
			// below needs --log-file's resolved path first, which itself needs library/candidate
			// state this phase produces; not worth reordering the whole action just for file-trace
			// coverage of one directory-enumeration call.
			var candidateResolveSw = System.Diagnostics.Stopwatch.StartNew();
			CandidateSet? resolved = ResolveCandidates(file: null, libraries, excludeFolders, recurse, out string? resolveError);
			if (traceConsole)
				Console.Error.WriteLine($"[trace] resolve candidates: {candidateResolveSw.Elapsed.TotalMilliseconds:0}ms");
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

			// Checked here, before any scanning (or even the AI-component download) starts, not left
			// for LibraryDatabaseStore.Save to discover: an existing directory already sitting at
			// --library-db's path can never work as a database file, and it's not worth wasting an
			// entire run's sampling work to find that out only once a save is attempted.
			if (libraryDbArg is not null && Directory.Exists(libraryDbArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --library-db must be a file path, but a directory already exists there: '{libraryDbArg.FullName}'.");
				return 1;
			}
			// Same class of mistake --library-db used to be exposed to before it became explicit-path
			// (docs/iterativeplan.md, "Post-ship fix #2" / "File-path DB options" entry) -- --log-file
			// is a *file* path, so a trailing separator or an existing directory there is just as much
			// a guaranteed, never-going-to-work destination, worth catching up front rather than
			// discovering only once the first log write silently no-ops.
			if (logFileArg is not null && (
					logFileArg.FullName.EndsWith(Path.DirectorySeparatorChar) ||
					logFileArg.FullName.EndsWith(Path.AltDirectorySeparatorChar) ||
					Directory.Exists(logFileArg.FullName))) {
				Console.Error.WriteLine($"Error: --log-file must name a file, not a directory: '{logFileArg.FullName}'.");
				return 1;
			}

			// With multiple --library folders, a derived default name can only ever be a guess --
			// derived from the first folder given, same "override if you care" philosophy as the
			// single-folder case, just extended to pick one of several. Display-only once resolved
			// (docs/iterativeplan.md, "File-path DB options" entry) -- always re-derived from the
			// resolved file's own stem, whether that stem came from --library-db or the fallback.
			string databasePath = LibraryDatabaseStore.ResolvePath(libraryDbArg?.FullName, libraries[0].FullName);
			string libraryName = Path.GetFileNameWithoutExtension(databasePath);
			string logPath = logFileArg?.FullName ??
				Path.Combine(Path.GetDirectoryName(databasePath)!, Path.GetFileNameWithoutExtension(databasePath) + ".log");

			// The database's own directory is created lazily, inside LibraryDatabaseStore.Save,
			// which doesn't run until the first checkpoint -- but WriteLogLine below needs its
			// directory (usually the same folder, for the default --library-db-derived path) to
			// exist from its very first call. Without this, every WriteLogLine call before the
			// first successful save throws DirectoryNotFoundException (a subtype of IOException),
			// which the catch below swallows exactly like a transient antivirus lock -- silently
			// dropping the start announcement and every per-file line, not just trace detail. Same
			// guard LibraryDatabaseStore.Save itself uses.
			string? logDir = Path.GetDirectoryName(logPath);
			if (logDir is { Length: > 0 })
				Directory.CreateDirectory(logDir);

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

			void EmitTrace(string line) {
				string formatted = $"[trace] {line}";
				if (traceConsole) Console.Error.WriteLine(formatted);
				if (traceFile) WriteLogLine(formatted);
			}
			using IDisposable? traceSubscription = ScanTelemetry.Enabled ? ScanTelemetry.Subscribe(EmitTrace) : null;

			void EmitDebug(string line) {
				string formatted = $"[debug] {line}";
				if (debugConsole) Console.Error.WriteLine(formatted);
				if (debugFile) WriteLogLine(formatted);
			}
			using IDisposable? debugSubscription = ScanTelemetry.DebugEnabled ? ScanTelemetry.SubscribeDebug(EmitDebug) : null;

			// Logger statements gate whether verbose-tier detail (model path, per-file frame counts,
			// checkpoint saves/failures) is even *raised* as an event at all, independent of whether
			// either destination is currently listening for it -- so it must be requested if *either*
			// the console or the file wants it, not just the console the way a plain --verbose bool
			// used to decide alone.
			bool emitDetailedLogging = consoleLevel >= ScanReportLevel.verbose || fileLevel >= ScanReportLevel.verbose;

			await EnsureAiComponentsReadyAsync(HardwareAcceleration.PreferDirectML, ct);

			LibraryDatabase database;
			try {
				using (ScanTelemetry.Time("load database"))
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
			// Recipe-staleness stamp (docs/iterativeplan.md, "File-path DB options" entry, Part 3) --
			// re-captured on every save (including mid-scan checkpoints), reflecting whatever
			// frameQuality settings this run's own sampling actually used.
			database.FrameQualitySnapshot = FrameQualitySnapshot.CaptureCurrent();
			var profile = new EdgeDensityProfile(edgeBoundary, sampleInterval, sparseInterval);

			if (ScanTelemetry.Enabled)
				EmitTrace($"startup (command entry to first candidate scanned): {commandStopwatch.Elapsed.TotalMilliseconds:0}ms");

			string startAnnouncement = $"Scanning '{string.Join("; ", libraries.Select(l => l.FullName))}' -> database '{databasePath}' ({candidatePaths.Count} candidate file(s))...";
			Console.Error.WriteLine(startAnnouncement);
			WriteLogLine(startAnnouncement);

			int sampledCount = 0, skippedCount = 0, failedCount = 0;
			// Collected regardless of --console-info/--log-level tier (those only gate the LIVE
			// per-file line) so a user who ran at the default 'info' level still gets to know which
			// files failed and why once the scan finishes, instead of only a bare count -- a long
			// scan's per-file failures otherwise only ever appeared in a scrolling debug-tier stream
			// nobody was watching, or not at all.
			var failures = new List<(string Path, string Reason)>();
			var scanLoopStopwatch = System.Diagnostics.Stopwatch.StartNew();
			void OnFileScanned(LibraryScanner.FileScanResult result) {
				switch (result.Outcome) {
					case LibraryScanner.ScanOutcome.Sampled: sampledCount++; break;
					case LibraryScanner.ScanOutcome.SkippedUnchanged: skippedCount++; break;
					case LibraryScanner.ScanOutcome.Failed:
						failedCount++;
						failures.Add((result.Path, result.Error ?? result.Detail ?? "unknown error"));
						break;
				}
				int done = sampledCount + skippedCount + failedCount;

				// Adaptive average over every file completed so far (sampled, skipped, or failed
				// alike), not just sampled ones -- self-corrects as the scan's actual skip/sample mix
				// plays out, same as any typical progress-bar ETA. Only meaningful once at least one
				// file has completed, which OnFileScanned guarantees (done >= 1 here).
				double avgSeconds = scanLoopStopwatch.Elapsed.TotalSeconds / done;
				TimeSpan eta = TimeSpan.FromSeconds(avgSeconds * (candidatePaths.Count - done));

				string tag = result.Outcome switch {
					LibraryScanner.ScanOutcome.Sampled => "SAMPLED",
					LibraryScanner.ScanOutcome.SkippedUnchanged => "SKIPPED",
					_ => "FAILED ",
				};
				string resultLine = $"{tag}  {Path.GetFileName(result.Path),-56}  {result.Error ?? result.Detail}";
				string avgEtaLine = $"Avg {avgSeconds:0.0} sec per file. ETA: {FormatEta(eta)}";
				string progressLine = $"{done}/{candidatePaths.Count}";

				if (consoleLevel >= ScanReportLevel.debug) {
					Console.WriteLine(resultLine);
					Console.WriteLine(avgEtaLine);
					Console.WriteLine(progressLine);
				}
				else if (consoleLevel == ScanReportLevel.info) {
					Console.Error.Write($"\rScanning: {done}/{candidatePaths.Count}  " +
						$"(sampled {sampledCount}, unchanged {skippedCount}, failed {failedCount}, " +
						$"avg {avgSeconds:0.0}s, ETA {FormatEta(eta)})   ");
				}
				// quiet: nothing to the console.

				if (fileLevel >= ScanReportLevel.debug) {
					WriteLogLine(resultLine);
					WriteLogLine(avgEtaLine);
					WriteLogLine(progressLine);
				}
				// info/quiet: no per-file lines in the log either -- info's file record is just the
				// start/end announcements (a live-updating counter is a terminal idiom that doesn't
				// translate to an appended file). --log-level defaults to verbose (>= debug), so the
				// log file gets this detail by default even when the console stays terse.
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

			// Always listed here regardless of --console-info tier (unless quiet asked for nothing
			// at all) -- see the failures list's own declaration above for why a bare count wasn't
			// enough on its own.
			if (failures.Count > 0) {
				WriteLogLine($"Failed file(s) ({failures.Count}):");
				if (consoleLevel > ScanReportLevel.quiet)
					Console.WriteLine($"Failed file(s) ({failures.Count}):");
				foreach ((string path, string reason) in failures) {
					string line = $"  {path}  ({reason})";
					WriteLogLine(line);
					if (consoleLevel > ScanReportLevel.quiet)
						Console.WriteLine(line);
				}
			}
			if (ScanTelemetry.Enabled)
				EmitTrace($"vbr scan total: {commandStopwatch.Elapsed.TotalSeconds:0.0}s");

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
