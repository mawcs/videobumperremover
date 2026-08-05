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
using VBR.Core.Catalog;
using VBR.Core.Database;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Removal;
using VDF.Core.AI;
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
///
/// Per docs/iterativeplan.md, "Utilizing Databases" entry: both the bumper (reference) side and
/// the library (candidate) side each independently accept either an ad hoc source (the original,
/// still-default behavior) or a persisted, pre-sampled one — freely mixable, four combinations:
/// <list type="bullet">
/// <item>ad hoc library (<c>--library</c>/<c>--file</c>) + ad hoc bumper (<c>--clip-from</c>/
/// <c>--region</c>/<c>--clip-length</c>) — the original, unchanged behavior.</item>
/// <item>ad hoc library + catalog bumper (<c>--bumper-label</c>/<c>--catalog-name</c>/
/// <c>--catalog-db-folder</c>) — the bumper's fingerprints/audio are reused from the catalog, not
/// re-extracted; candidates are still freshly sampled per file.</item>
/// <item>scanned library database (<c>--library-name</c>/<c>--library-db-folder</c>) + ad hoc
/// bumper — candidates' fingerprints/audio are reused from the database, not re-scanned; the
/// bumper is still freshly sampled from <c>--clip-from</c>.</item>
/// <item>scanned library database + catalog bumper — both sides fully cached; matching touches
/// no ffmpeg/ONNX at all (only the removal cut itself, for files that actually match, decodes
/// anything).</item>
/// </list>
/// See <see cref="MatchingSession"/>'s class doc comment for how the two independent axes are
/// implemented.
/// </summary>
internal static class RemoveCommand {
	const string DefaultCatalogName = "default";

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

	static readonly Option<string> BumperLabel = new("--bumper-label") {
		Description = "Use a named bumper from a catalog instead of an ad hoc --clip-from clip -- " +
			"looked up (case-insensitively) in --catalog-name, or the 'default' catalog if that's " +
			"not given. The catalog entry's own region and measured duration are used; " +
			"--clip-from/--region/--clip-length are invalid together with this.",
	};

	static readonly Option<string> CatalogName = new("--catalog-name") {
		Description = $"Which catalog --bumper-label is looked up in. Must be accompanied by " +
			$"--bumper-label. Default: '{DefaultCatalogName}' when omitted.",
	};

	static readonly Option<DirectoryInfo> CatalogDbFolder = new("--catalog-db-folder") {
		Description = "Folder holding --catalog-name's file. Must be accompanied by --bumper-label " +
			"and --catalog-name. Default: the same dedicated folder 'vbr add-bumper' writes to.",
	};

	internal static Command Build() {
		var cmd = new Command("remove",
			"Find a bumper's presence across a folder of videos (or a single file, via --file) " +
			"and remove it from each match. Never modifies the source — writes a sibling " +
			"'name.vbr.ext' file instead, plus a JSON manifest recording exactly what was cut. " +
			"Bundles clip extraction, matching, and removal in one command " +
			"(see docs/decisions/0007-removal-command.md). The bumper can be ad hoc or a named " +
			"catalog entry (--bumper-label); candidates can be an ad hoc folder/file or a scanned " +
			"library database (--library-name) — see docs/iterativeplan.md, \"Utilizing Databases\".");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(BumperLabel);
		cmd.Options.Add(CatalogName);
		cmd.Options.Add(CatalogDbFolder);
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
		cmd.Options.Add(LibraryName);
		cmd.Options.Add(LibraryDbFolder);
		cmd.Options.Add(Output);
		cmd.Options.Add(DumpFrames);
		cmd.Options.Add(ReEncode);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			bool reEncode = parseResult.GetValue(ReEncode);
			RemovalMode removalMode = reEncode ? RemovalMode.ReEncode : RemovalMode.StreamCopy;

			FileInfo? clipFromArg = parseResult.GetValue(ClipFrom);
			ClipEdge? regionArg = parseResult.GetValue(Region);
			TimeSpan clipLengthArg = parseResult.GetValue(ClipLength);
			string? bumperLabel = parseResult.GetValue(BumperLabel);
			string? catalogNameArg = parseResult.GetValue(CatalogName);
			DirectoryInfo? catalogDbFolderArg = parseResult.GetValue(CatalogDbFolder);

			TimeSpan searchLength = parseResult.GetValue(SearchLength);
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
			string? libraryNameArg = parseResult.GetValue(LibraryName);
			DirectoryInfo? libraryDbFolderArg = parseResult.GetValue(LibraryDbFolder);
			FileInfo? output = parseResult.GetValue(Output);
			DirectoryInfo? dumpFrames = parseResult.GetValue(DumpFrames);
			bool verbose = parseResult.GetValue(Verbose);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);
			HardwareAcceleration.ReportDecodeRequest();

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			// ---- Reference (bumper) side: ad hoc clip vs. catalog entry ----
			bool adHocRefGiven = clipFromArg is not null || regionArg is not null || clipLengthArg > TimeSpan.Zero;
			bool catalogRefGiven = !string.IsNullOrWhiteSpace(bumperLabel);
			if (adHocRefGiven && catalogRefGiven) {
				Console.Error.WriteLine("Error: --bumper-label is invalid together with --clip-from/--region/--clip-length.");
				return 1;
			}
			if (!adHocRefGiven && !catalogRefGiven) {
				Console.Error.WriteLine("Error: one of --clip-from/--region/--clip-length or --bumper-label is required.");
				return 1;
			}
			if (!string.IsNullOrWhiteSpace(catalogNameArg) && !catalogRefGiven) {
				Console.Error.WriteLine("Error: --catalog-name must be accompanied by --bumper-label.");
				return 1;
			}
			if (catalogDbFolderArg is not null && !(catalogRefGiven && !string.IsNullOrWhiteSpace(catalogNameArg))) {
				Console.Error.WriteLine("Error: --catalog-db-folder must be accompanied by --bumper-label and --catalog-name.");
				return 1;
			}
			if (!catalogRefGiven) {
				// Ad hoc mode: today's original requiredness, just checked here instead of by the
				// parser (see SharedOptions' own note on why these three lost their declarative
				// Required=true).
				if (clipFromArg is null) {
					Console.Error.WriteLine("Error: --clip-from is required (or use --bumper-label).");
					return 1;
				}
				if (regionArg is null) {
					Console.Error.WriteLine("Error: --region is required (or use --bumper-label).");
					return 1;
				}
				if (clipLengthArg <= TimeSpan.Zero) {
					Console.Error.WriteLine("Error: --clip-length is required (or use --bumper-label).");
					return 1;
				}
			}

			// ---- Candidate side: ad hoc library/file vs. scanned library database ----
			bool hasLibrary = libraries.Length > 0;
			bool hasFile = targetFile is not null;
			bool hasLibraryDb = !string.IsNullOrWhiteSpace(libraryNameArg);
			if (hasLibraryDb && hasLibrary) {
				Console.Error.WriteLine("Error: --library-name is invalid together with --library.");
				return 1;
			}
			if (libraryDbFolderArg is not null && !hasLibraryDb) {
				Console.Error.WriteLine("Error: --library-db-folder must be accompanied by --library-name.");
				return 1;
			}
			int candidateSourceCount = (hasLibrary ? 1 : 0) + (hasLibraryDb ? 1 : 0) + (hasFile ? 1 : 0);
			if (candidateSourceCount == 0) {
				Console.Error.WriteLine("Error: one of --library, --library-name, or --file is required.");
				return 1;
			}
			if (candidateSourceCount > 1) {
				Console.Error.WriteLine("Error: specify only one of --library, --library-name, or --file.");
				return 1;
			}
			if (libraryDbFolderArg is not null && File.Exists(libraryDbFolderArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --library-db-folder must be a folder, but a file already exists there: '{libraryDbFolderArg.FullName}'.");
				return 1;
			}
			if (catalogDbFolderArg is not null && File.Exists(catalogDbFolderArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --catalog-db-folder must be a folder, but a file already exists there: '{catalogDbFolderArg.FullName}'.");
				return 1;
			}

			// ---- Resolve the reference (bumper): region + length + (if catalog) the entry itself ----
			ClipEdge region;
			TimeSpan clipLength;
			BumperCatalogEntry? catalogEntry = null;
			string? resolvedCatalogName = null;
			string? catalogPath = null;
			if (catalogRefGiven) {
				resolvedCatalogName = string.IsNullOrWhiteSpace(catalogNameArg) ? DefaultCatalogName : catalogNameArg;
				catalogPath = BumperCatalogStore.ResolveCatalogPath(catalogDbFolderArg?.FullName, resolvedCatalogName);
				BumperCatalog catalog;
				try {
					catalog = BumperCatalogStore.Load(catalogPath);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}
				catalogEntry = catalog.Entries.Values.FirstOrDefault(
					e => string.Equals(e.Label, bumperLabel, StringComparison.OrdinalIgnoreCase));
				if (catalogEntry is null) {
					Console.Error.WriteLine($"Error: catalog '{resolvedCatalogName}' has no bumper labeled '{bumperLabel}'.");
					return 1;
				}
				region = catalogEntry.Region;
				clipLength = catalogEntry.Duration;
			}
			else {
				region = regionArg!.Value;
				clipLength = clipLengthArg;
			}
			if (searchLength <= TimeSpan.Zero)
				searchLength = clipLength + TimeSpan.FromSeconds(20);

			if (region == ClipEdge.begin && !reEncode)
				Console.Error.WriteLine(
					"Note: begin-region stream-copy removal does not realign subtitle cues — " +
					"cues will run out of sync with the removed duration. Use --re-encode true " +
					"(the default) for correct subtitle timing.");
			if (dumpFrames is not null && mode is DetectionMode.audio)
				Console.Error.WriteLine("Note: --dump-frames applies to visual/pHash matching only; --detection-mode audio dumps nothing.");
			if (dumpFrames is not null && hasLibraryDb)
				Console.Error.WriteLine(
					"Note: --dump-frames cannot dump candidate frames sourced from --library-name (no frames " +
					"are decoded for a database candidate); only the reference clip's own frames (if ad hoc) are dumped.");
			if (recurse == false && hasLibraryDb)
				Console.Error.WriteLine("Note: --no-recurse has no effect with --library-name -- the database's own file list is used as-is.");

			// ---- Resolve candidates: ad hoc folder/file vs. a scanned library database ----
			IReadOnlyList<string> candidatePaths;
			IReadOnlyList<string> libraryRoots = Array.Empty<string>();
			Dictionary<string, LibraryDatabaseEntry>? candidateDbEntries = null;
			string? databasePath = null;
			if (hasLibraryDb) {
				string libraryName = libraryNameArg!;
				databasePath = LibraryDatabaseStore.ResolveDatabasePath(libraryDbFolderArg?.FullName, libraryName);
				LibraryDatabase database;
				try {
					database = LibraryDatabaseStore.Load(databasePath);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}

				candidateDbEntries = new Dictionary<string, LibraryDatabaseEntry>(StringComparer.OrdinalIgnoreCase);
				var paths = new List<string>();
				foreach (LibraryDatabaseEntry entry in database.Entries.Values) {
					// Tombstoned (or otherwise now-missing) entries have nothing on disk to remove
					// the bumper from -- skip rather than fail the whole run over stale database rows.
					if (entry.TombstonedUtc is not null || !File.Exists(entry.Path)) continue;
					if (Path.GetFileNameWithoutExtension(entry.Path).EndsWith(".vbr", StringComparison.OrdinalIgnoreCase)) continue;
					if (IsUnderAny(entry.Path, excludeFolders)) continue;
					string full = Path.GetFullPath(entry.Path);
					if (candidateDbEntries.TryAdd(full, entry))
						paths.Add(entry.Path);
				}
				paths.Sort(StringComparer.OrdinalIgnoreCase);
				candidatePaths = paths;
			}
			else {
				CandidateSet? resolved = ResolveCandidates(targetFile, libraries, excludeFolders, recurse, out string? resolveError);
				if (resolved is null) {
					Console.Error.WriteLine(resolveError);
					return 1;
				}
				var (paths, roots) = resolved.Value;
				// --clip-from is NOT excluded: it's a normal candidate that (almost certainly) also
				// contains the bumper it was enrolled from — skipping it silently left its own copy
				// of the bumper never removed, with no indication anywhere that it had been skipped.
				// A prior run's own output ("name.vbr.ext") must never be re-matched/re-cut.
				candidatePaths = paths.Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".vbr", StringComparison.OrdinalIgnoreCase)).ToList();
				libraryRoots = roots;
			}

			// ---- AI-component readiness: only needed where something still gets freshly sampled ----
			bool wantsVisual = mode is DetectionMode.visual or DetectionMode.both or DetectionMode.all;
			if (wantsVisual && (!catalogRefGiven || !hasLibraryDb))
				await EnsureAiComponentsReadyAsync(HardwareAcceleration.PreferDirectML, ct);

			(MatchingSession? session, string? prepareError) = catalogEntry is not null
				? MatchingSession.PrepareFromCatalogEntry(mode, catalogEntry, profile, presenceThreshold,
					phashPresenceThreshold, minSimilarity, dumpFrames?.FullName, verbose)
				: await MatchingSession.PrepareAsync(mode, clipFromArg!, region, clipLength, profile, presenceThreshold,
					phashPresenceThreshold, minSimilarity, dumpFrames?.FullName, verbose, ct);
			if (session is null) {
				Console.Error.WriteLine(prepareError);
				return 1;
			}

			using (session) {
				int matchCount = 0;
				int removedCount = 0;
				int comparedCount = 0;
				int dumpIndex = 0;
				int fileIndex = 0;
				var rows = new List<RemoveRow>(candidatePaths.Count);
				foreach (string file in candidatePaths) {
					ct.ThrowIfCancellationRequested();
					string display = candidateDbEntries is not null ? Path.GetFileName(file) : DisplayName(file, libraryRoots);
					string dumpLabel = $"{++dumpIndex:000}-{Path.GetFileNameWithoutExtension(file)}";
					// Printed unconditionally, before any comparison/removal work starts on this file
					// -- comparing (an ad hoc candidate's fresh decode+embed) and especially removing
					// (a re-encode can take as long as encoding the whole file normally would) can
					// both run for a long time with nothing else to show for it otherwise, which reads
					// as "hung," not "working" (docs/iterativeplan.md, "CLI feedback during remove").
					Console.Error.WriteLine($"[{++fileIndex}/{candidatePaths.Count}] Checking: {display}");
					RemoveRow row;
					try {
						SignalResult result = candidateDbEntries is not null
							? session.CompareUsingDatabase(candidateDbEntries[Path.GetFullPath(file)], searchLength)
							: session.Compare(file, searchLength, dumpLabel, ct);
						comparedCount++;
						string? outputPath = null;
						string? removalError = null;
						if (result.Present) {
							matchCount++;
							string modeDescription = removalMode == RemovalMode.ReEncode
								? "re-encode -- this may take a while for large files"
								: "stream-copy -- fast";
							Console.Error.WriteLine($"  Match found ({result.Visual?.Detail ?? result.Audio?.Detail ?? result.PHash?.Detail}) — removing bumper ({modeDescription})...");
							bool printedProgress = false;
							void OnRemovalProgress(RemovalProgress p) {
								printedProgress = true;
								double fraction = p.Total > TimeSpan.Zero ? Math.Clamp(p.Processed.TotalSeconds / p.Total.TotalSeconds, 0, 1) : 0;
								string speedText = p.SpeedMultiplier is { } s ? $"{s:0.##}x" : "?";
								Console.Error.Write($"\r    {fraction:P0}  ({FormatSeconds(p.Processed)} / {FormatSeconds(p.Total)}, {speedText} realtime)   ");
							}
							try {
								var removed = ClipRemover.Remove(file, region, clipLength, removalMode,
									result.Visual?.Detail ?? result.Audio?.Detail ?? result.PHash?.Detail, verbose, OnRemovalProgress, ct);
								if (printedProgress) Console.Error.WriteLine();
								outputPath = removed.OutputPath;
								removedCount++;
							}
							catch (Exception ex) {
								if (printedProgress) Console.Error.WriteLine();
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
					(candidatePaths.Count > comparedCount ? $" ({candidatePaths.Count - comparedCount} skipped with errors.)" : "");
				Console.WriteLine();
				Console.WriteLine(summary);

				if (output is not null && !WriteReport(output, rows, summary,
						clipFromArg, region, clipLength, searchLength, sampleInterval, edgeBoundary, sparseInterval, mode,
						presenceThreshold, phashPresenceThreshold, minSimilarity, libraries, excludeFolders, targetFile, recurse, removalMode,
						bumperLabel, resolvedCatalogName, catalogPath, libraryNameArg, databasePath))
					return 1;
			}
			return 0;
		});

		return cmd;
	}

	static bool WriteReport(FileInfo output, IReadOnlyList<RemoveRow> rows, string summary,
			FileInfo? clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, TimeSpan edgeBoundary, TimeSpan sparseInterval, DetectionMode mode,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			IReadOnlyList<DirectoryInfo> libraries, IReadOnlyList<DirectoryInfo> excludeFolders,
			FileInfo? targetFile, bool recurse, RemovalMode removalMode,
			string? bumperLabel, string? catalogName, string? catalogPath, string? libraryName, string? databasePath) {
		var report = new StringBuilder();
		report.AppendLine($"vbr remove report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
		if (bumperLabel is not null)
			report.AppendLine($"bumper:         '{bumperLabel}' from catalog '{catalogName}' ({catalogPath})");
		else
			report.AppendLine($"clip-from:      {clipFrom!.FullName}");
		report.AppendLine($"region: {region}   {(bumperLabel is not null ? "bumper-length" : "clip-length")}: {FormatSeconds(clipLength)}   " +
			$"search-length: {FormatSeconds(searchLength)}   sample-interval: {FormatSeconds(sampleInterval)}");
		report.AppendLine($"edge-boundary: {(edgeBoundary == TimeSpan.MaxValue ? "(whole window, single-density)" : FormatSeconds(edgeBoundary))}   " +
			$"sparse-interval: {FormatSeconds(sparseInterval)}");
		report.AppendLine(string.Create(CultureInfo.InvariantCulture,
			$"detection-mode: {mode}   presence-threshold: {presenceThreshold:0.###}   " +
			$"phash-presence-threshold: {phashPresenceThreshold:0.###}   min-similarity: {minSimilarity:0.###}"));
		if (libraryName is not null)
			report.AppendLine($"library-name:   {libraryName}   database: {databasePath}");
		else if (targetFile is not null)
			report.AppendLine($"file:           {targetFile.FullName}");
		else
			report.AppendLine($"library:        {string.Join("; ", libraries.Select(l => l.FullName))}   ({(recurse ? "recursive" : "top level only")})");
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
