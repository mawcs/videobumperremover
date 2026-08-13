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
using VBR.Core.Configuration;
using VBR.Core.Database;
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

/// <summary>
/// <c>vbr match</c> — reports a bumper's presence across a folder of videos (or a single file),
/// without cutting anything. Shares its parameter surface and both database axes with
/// <c>vbr remove</c> (docs/iterativeplan.md, "Utilizing Databases" entry, extended to <c>match</c>
/// 2026-08-13 — the original 2026-07-29 entry scoped this to <c>remove</c> only; matching-only
/// investigation against a catalog bumper/scanned library turned out to be a real, wanted use case
/// too, and the underlying plumbing — <see cref="MatchingSession.PrepareFromCatalogEntry"/>/
/// <see cref="MatchingSession.CompareUsingDatabase"/> — was already shared, not remove-specific).
/// Both the bumper (reference) side and the library (candidate) side each independently accept
/// either an ad hoc source (the original, still-default behavior) or a persisted, pre-sampled one —
/// freely mixable, four combinations:
/// <list type="bullet">
/// <item>ad hoc library (<c>--library</c>/<c>--file</c>) + ad hoc bumper (<c>--clip-from</c>/
/// <c>--region</c>/<c>--clip-length</c>) — the original, unchanged behavior.</item>
/// <item>ad hoc library + catalog bumper (<c>--bumper-label</c>/<c>--catalog-db</c>) — the
/// bumper's fingerprints/audio are reused from the catalog, not re-extracted; candidates are
/// still freshly sampled per file.</item>
/// <item>scanned library database (<c>--library-db</c>) + ad hoc bumper — candidates'
/// fingerprints/audio are reused from the database, not re-scanned; the bumper is still freshly
/// sampled from <c>--clip-from</c>.</item>
/// <item>scanned library database + catalog bumper — both sides fully cached; matching touches
/// no ffmpeg/ONNX at all.</item>
/// </list>
/// See <see cref="MatchingSession"/>'s class doc comment for how the two independent axes are
/// implemented, and <see cref="RemoveCommand"/> for the same combinations plus the removal step.
/// </summary>
internal static class MatchCommand {

	static readonly Option<FileInfo> Output = new("--output") {
		Description = "Also write the match report to this file: the same per-file rows and " +
			"summary as the console, plus a header recording the run's parameters.",
	};

	// --label is an alias, not a separate option -- see RemoveCommand's identical declaration for
	// the full history (2026-08-13: the two-name split predates the single --catalog-db/--library-db
	// collapse and had outlived its original reason to exist).
	static readonly Option<string> BumperLabel = new("--bumper-label") {
		Description = "Use a named bumper from a catalog instead of an ad hoc --clip-from clip -- " +
			"looked up (case-insensitively) in --catalog-db, or the default catalog if that's not " +
			"given. The catalog entry's own region and measured duration are used; " +
			"--clip-from/--region/--clip-length are invalid together with this. Alias: --label.",
		Aliases = { "--label" },
	};

	internal static Command Build() {
		var cmd = new Command("match",
			"Find a bumper's presence across a folder of videos (or a single file, via --file). " +
			"Visual DINOv2 presence matching runs by default. The bumper can be ad hoc " +
			"(--clip-from, sampled internally -- you never provide a pre-cut clip) or a named " +
			"catalog entry (--bumper-label); candidates can be an ad hoc folder/file or a scanned " +
			"library database (--library-db) — see docs/iterativeplan.md, \"Utilizing Databases\".");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(BumperLabel);
		cmd.Options.Add(CatalogDb);
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
		cmd.Options.Add(LibraryDb);
		cmd.Options.Add(Output);
		cmd.Options.Add(DumpFrames);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			FileInfo? clipFromArg = parseResult.GetValue(ClipFrom);
			ClipEdge? regionArg = parseResult.GetValue(Region);
			TimeSpan clipLengthArg = parseResult.GetValue(ClipLength);
			string? bumperLabel = parseResult.GetValue(BumperLabel);
			FileInfo? catalogDbArg = parseResult.GetValue(CatalogDb);

			TimeSpan searchLength = parseResult.GetValue(SearchLength);
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
			FileInfo? libraryDbArg = parseResult.GetValue(LibraryDb);
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
			if (catalogDbArg is not null && !catalogRefGiven) {
				Console.Error.WriteLine("Error: --catalog-db must be accompanied by --bumper-label.");
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
			bool hasLibraryDb = libraryDbArg is not null;
			if (hasLibraryDb && hasLibrary) {
				Console.Error.WriteLine("Error: --library-db is invalid together with --library.");
				return 1;
			}
			int candidateSourceCount = (hasLibrary ? 1 : 0) + (hasLibraryDb ? 1 : 0) + (hasFile ? 1 : 0);
			if (candidateSourceCount == 0) {
				Console.Error.WriteLine("Error: one of --library, --library-db, or --file is required.");
				return 1;
			}
			if (candidateSourceCount > 1) {
				Console.Error.WriteLine("Error: specify only one of --library, --library-db, or --file.");
				return 1;
			}
			if (hasLibraryDb && Directory.Exists(libraryDbArg!.FullName)) {
				Console.Error.WriteLine(
					$"Error: --library-db must be a file path, but a directory already exists there: '{libraryDbArg.FullName}'.");
				return 1;
			}
			if (catalogDbArg is not null && Directory.Exists(catalogDbArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --catalog-db must be a file path, but a directory already exists there: '{catalogDbArg.FullName}'.");
				return 1;
			}

			// ---- Resolve the reference (bumper): region + length + (if catalog) the entry itself ----
			ClipEdge region;
			TimeSpan clipLength;
			BumperCatalogEntry? catalogEntry = null;
			string? resolvedCatalogName = null;
			string? catalogPath = null;
			if (catalogRefGiven) {
				catalogPath = BumperCatalogStore.ResolvePath(catalogDbArg?.FullName);
				resolvedCatalogName = Path.GetFileNameWithoutExtension(catalogPath);
				BumperCatalog catalog;
				try {
					catalog = BumperCatalogStore.Load(catalogPath);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}
				// Recipe-staleness check (docs/iterativeplan.md, "File-path DB options" entry, Part 3)
				// -- unconditional, not gated on --verbose: a stale frameQuality recipe can produce
				// silently wrong match results, not just a cosmetic difference.
				string? catalogStalenessWarning = FrameQualitySnapshot.DescribeMismatchFromCurrent(
					catalog.FrameQualitySnapshot, $"Catalog '{resolvedCatalogName}'");
				if (catalogStalenessWarning is not null)
					Console.Error.WriteLine($"Warning: {catalogStalenessWarning}");

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
				searchLength = clipLength + TimeSpan.FromSeconds(VbrConfig.Current.Sampling.SearchLengthSlackSeconds);

			if (dumpFrames is not null && mode is DetectionMode.audio)
				Console.Error.WriteLine("Note: --dump-frames applies to visual/pHash matching only; --detection-mode audio dumps nothing.");
			if (dumpFrames is not null && hasLibraryDb)
				Console.Error.WriteLine(
					"Note: --dump-frames cannot dump candidate frames sourced from --library-db (no frames " +
					"are decoded for a database candidate); only the reference clip's own frames (if ad hoc) are dumped.");
			if (recurse == false && hasLibraryDb)
				Console.Error.WriteLine("Note: --no-recurse has no effect with --library-db -- the database's own file list is used as-is.");

			// ---- Resolve candidates: ad hoc folder/file vs. a scanned library database ----
			IReadOnlyList<string> candidatePaths;
			IReadOnlyList<string> libraryRoots = Array.Empty<string>();
			Dictionary<string, LibraryDatabaseEntry>? candidateDbEntries = null;
			string? databasePath = null;
			if (hasLibraryDb) {
				databasePath = LibraryDatabaseStore.ResolvePath(libraryDbArg!.FullName);
				LibraryDatabase database;
				try {
					database = LibraryDatabaseStore.Load(databasePath);
				}
				catch (Exception ex) {
					Console.Error.WriteLine($"Error: {ex.Message}");
					return 1;
				}
				// Recipe-staleness check -- see the matching check on the catalog side above for why
				// this is unconditional rather than gated on --verbose.
				string? libraryStalenessWarning = FrameQualitySnapshot.DescribeMismatchFromCurrent(
					database.FrameQualitySnapshot, $"Library database '{Path.GetFileNameWithoutExtension(databasePath)}'");
				if (libraryStalenessWarning is not null)
					Console.Error.WriteLine($"Warning: {libraryStalenessWarning}");

				candidateDbEntries = new Dictionary<string, LibraryDatabaseEntry>(StringComparer.OrdinalIgnoreCase);
				var paths = new List<string>();
				foreach (LibraryDatabaseEntry entry in database.Entries.Values) {
					// Tombstoned (or otherwise now-missing) entries have nothing on disk to compare
					// -- skip rather than fail the whole run over stale database rows.
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
				// --clip-from is NOT excluded: it's a normal candidate that (almost
				// certainly) also contains the bumper it was enrolled from, and silently
				// skipping it left it unreported with no indication anywhere that happened.
				// A prior run's own output ("name.vbr.ext") must never be re-matched.
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
				int comparedCount = 0;
				int dumpIndex = 0;
				var rows = new List<MatchRow>(candidatePaths.Count);
				foreach (string file in candidatePaths) {
					ct.ThrowIfCancellationRequested();
					string display = candidateDbEntries is not null ? Path.GetFileName(file) : DisplayName(file, libraryRoots);
					string dumpLabel = $"{++dumpIndex:000}-{Path.GetFileNameWithoutExtension(file)}";
					MatchRow row;
					try {
						SignalResult result = candidateDbEntries is not null
							? session.CompareUsingDatabase(candidateDbEntries[Path.GetFullPath(file)], searchLength)
							: session.Compare(file, searchLength, dumpLabel, ct);
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
						clipFromArg, region, clipLength, searchLength, sampleInterval, edgeBoundary, sparseInterval, mode,
						presenceThreshold, phashPresenceThreshold, minSimilarity, libraries, excludeFolders, targetFile, recurse,
						bumperLabel, resolvedCatalogName, catalogPath, databasePath is not null ? Path.GetFileNameWithoutExtension(databasePath) : null, databasePath))
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
			FileInfo? clipFrom, ClipEdge region, TimeSpan clipLength, TimeSpan searchLength,
			TimeSpan sampleInterval, TimeSpan edgeBoundary, TimeSpan sparseInterval, DetectionMode mode,
			float presenceThreshold, float phashPresenceThreshold, float minSimilarity,
			IReadOnlyList<DirectoryInfo> libraries, IReadOnlyList<DirectoryInfo> excludeFolders,
			FileInfo? targetFile, bool recurse,
			string? bumperLabel, string? catalogName, string? catalogPath, string? libraryName, string? databasePath) {
		var report = new StringBuilder();
		report.AppendLine($"vbr match report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
			report.AppendLine($"library-db:     {libraryName}   database: {databasePath}");
		else if (targetFile is not null)
			report.AppendLine($"file:           {targetFile.FullName}");
		else
			report.AppendLine($"library:        {string.Join("; ", libraries.Select(l => l.FullName))}   ({(recurse ? "recursive" : "top level only")})");
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
