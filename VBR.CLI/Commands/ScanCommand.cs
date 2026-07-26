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
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Index;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>
/// <c>vbr scan</c> — builds/updates a named library's cached fingerprint index
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

	static readonly Option<string> LibraryNameOption = new("--library-name") {
		Description = "Label for this library's index — also names its default index file. " +
			"Default: --library's own folder name.",
	};

	static readonly Option<FileInfo> IndexPath = new("--index") {
		Description = "Where this library's index file lives. Default: a dedicated per-library " +
			"file under VBR's own state folder, named after --library-name.",
	};

	static readonly Option<bool> IncludeVbrOutputs = new("--include-vbr-outputs") {
		Description = "Also index 'name.vbr.ext' outputs from a prior 'vbr remove' run — excluded " +
			"by default since they're transitional staging artifacts (a review window before " +
			"'vbr cleanup' promotes or discards them) that usually near-duplicate the original.",
	};

	static readonly Option<bool> Rescan = new("--rescan") {
		Description = "Bypass change detection and re-sample every candidate, even ones the cache " +
			"says are unchanged. Needed after a sampling-parameter change to invalidate stale entries.",
		Aliases = { "--force" },
	};

	internal static Command Build() {
		var cmd = new Command("scan",
			"Build or update a library's cached fingerprint index — samples every file's true edges " +
			"and whole-file middle so a bumper can be found later without re-decoding. Does not match " +
			"against any specific bumper; see 'vbr match'/'vbr remove' for that.");
		cmd.Options.Add(Library);
		cmd.Options.Add(NoRecurse);
		cmd.Options.Add(EdgeBoundary);
		cmd.Options.Add(SampleInterval);
		cmd.Options.Add(SparseInterval);
		cmd.Options.Add(LibraryNameOption);
		cmd.Options.Add(IndexPath);
		cmd.Options.Add(IncludeVbrOutputs);
		cmd.Options.Add(Rescan);
		cmd.Options.Add(Verbose);

		cmd.SetAction(async (parseResult, ct) => {
			var library = parseResult.GetValue(Library);
			bool recurse = !parseResult.GetValue(NoRecurse);
			TimeSpan edgeBoundary = parseResult.GetValue(EdgeBoundary);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			TimeSpan sparseInterval = parseResult.GetValue(SparseInterval);
			string? libraryNameArg = parseResult.GetValue(LibraryNameOption);
			FileInfo? indexPathArg = parseResult.GetValue(IndexPath);
			bool includeVbrOutputs = parseResult.GetValue(IncludeVbrOutputs);
			bool rescan = parseResult.GetValue(Rescan);
			bool verbose = parseResult.GetValue(Verbose);

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			if (library is null) {
				Console.Error.WriteLine("Error: --library is required.");
				return 1;
			}

			CandidateSet? resolved = ResolveCandidates(file: null, library, recurse, out string? resolveError);
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

			string libraryName = string.IsNullOrWhiteSpace(libraryNameArg)
				? LibraryIndexStore.DeriveLibraryName(library.FullName)
				: libraryNameArg;
			string indexPath = LibraryIndexStore.ResolveIndexPath(indexPathArg?.FullName, libraryName);

			if (!AiComponents.IsReady) {
				Console.Error.WriteLine("AI matching components not found — downloading (one-time, ~100MB)...");
				await AiComponents.DownloadAsync(progress: null, ct);
				Console.Error.WriteLine("AI components ready.");
			}

			LibraryIndex index;
			try {
				index = LibraryIndexStore.Load(indexPath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return 1;
			}
			index.LibraryName = libraryName;
			index.EdgeBoundarySeconds = edgeBoundary.TotalSeconds;
			index.DenseIntervalSeconds = sampleInterval.TotalSeconds;
			index.SparseIntervalSeconds = sparseInterval.TotalSeconds;
			var profile = new EdgeDensityProfile(edgeBoundary, sampleInterval, sparseInterval);

			Console.Error.WriteLine($"Scanning '{library.FullName}' -> index '{indexPath}' ({candidatePaths.Count} candidate file(s))...");

			int sampledCount = 0, skippedCount = 0, failedCount = 0;
			void OnFileScanned(LibraryScanner.FileScanResult result) {
				switch (result.Outcome) {
					case LibraryScanner.ScanOutcome.Sampled: sampledCount++; break;
					case LibraryScanner.ScanOutcome.SkippedUnchanged: skippedCount++; break;
					case LibraryScanner.ScanOutcome.Failed: failedCount++; break;
				}
				if (verbose) {
					string tag = result.Outcome switch {
						LibraryScanner.ScanOutcome.Sampled => "SAMPLED",
						LibraryScanner.ScanOutcome.SkippedUnchanged => "SKIPPED",
						_ => "FAILED ",
					};
					Console.WriteLine($"{tag}  {Path.GetFileName(result.Path),-56}  {result.Error ?? result.Detail}");
				}
				else {
					int done = sampledCount + skippedCount + failedCount;
					Console.Error.Write($"\rScanning: {done}/{candidatePaths.Count}  " +
						$"(sampled {sampledCount}, unchanged {skippedCount}, failed {failedCount})   ");
				}
			}

			using var scanner = new LibraryScanner(verbose);
			LibraryScanner.ScanSummary summary;
			try {
				summary = scanner.Scan(candidatePaths, index, indexPath, profile, rescan, OnFileScanned, ct);
			}
			catch (OperationCanceledException) {
				Console.Error.WriteLine();
				Console.Error.WriteLine("Cancelled — progress up to the last checkpoint was saved.");
				return 1;
			}

			if (!verbose)
				Console.Error.WriteLine();
			Console.WriteLine($"{summary.Scanned} sampled, {summary.SkippedUnchanged} unchanged (skipped), " +
				$"{summary.Failed} failed, {summary.Total} total.");
			Console.WriteLine($"Index: {indexPath}");
			return 0;
		});

		return cmd;
	}
}
