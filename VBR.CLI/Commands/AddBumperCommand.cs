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
using VBR.Core.Catalog;
using VBR.Core.Configuration;
using VBR.Core.Extraction;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>
/// <c>vbr add-bumper</c> — adds one bumper to a named catalog (docs/iterativeplan.md, "Bumper
/// catalog"). Samples the requested region of <c>--clip-from</c>, extracts a reference clip and a
/// thumbnail, and writes a new entry — mirrors <c>match</c>/<c>remove</c>'s "never accept a
/// pre-cut clip" contract (<c>--clip-from</c>/<c>--region</c>/<c>--clip-length</c>, identical
/// meaning). Does not match or remove anything, and does not read the catalog back — "apply"
/// (catalog-aware scanning) is separate, later work.
///
/// <c>--catalog-db</c> is independent of any media folder — deliberately no <c>--library</c>/folder
/// argument anywhere (post-ship simplification, 2026-07-28). An earlier version mirrored
/// <c>vbr scan</c>'s <c>--library</c>/<c>--library-name</c> pair, which wrongly implied a catalog
/// belongs to one scanned library; tracing the code showed <c>--library</c> was only ever read to
/// derive a name, never used for anything a folder is actually needed for, and the maintainer's own
/// review concluded a catalog should be nameable and reusable independent of any specific media
/// collection. See <see cref="BumperCatalog"/>'s doc comment. <c>--catalog-db</c> itself replaced
/// the original <c>--catalog-name</c>/<c>--catalog-db-folder</c> pair (docs/iterativeplan.md,
/// "File-path DB options" entry, 2026-08-12) with one explicit file path.
/// </summary>
internal static class AddBumperCommand {
	// Config-aware since 2026-08-12 (VbrConfig.Current.Limits) -- Program.cs loads config before
	// any command's Build() runs, so these static field initializers (LabelOption/DescriptionOption
	// below, whose Description text interpolates these) already see the resolved config value.
	static int MaxLabelLength => VbrConfig.Current.Limits.MaxLabelLength;
	static int MaxDescriptionLength => VbrConfig.Current.Limits.MaxDescriptionLength;

	static readonly Option<string> LabelOption = new("--label") {
		Description = $"Short, human-facing name for this bumper (max {MaxLabelLength} characters), " +
			"e.g. 'Disney FBI warning 2003' -- the one field you must supply yourself; no auto-suggestion. " +
			"Must be unique within the target --catalog-db (case-insensitive); other catalogs may " +
			"reuse the same label freely.",
		Required = true,
	};

	static readonly Option<string> DescriptionOption = new("--description") {
		Description = $"Optional longer free text for curation context (max {MaxDescriptionLength} characters).",
	};

	static readonly Option<string> TagsOption = new("--tags") {
		Description = "Optional comma-separated tags, e.g. \"disney,fbi-warning,2003\".",
	};

	// A local definition, not SharedOptions.SampleInterval -- that one defaults to 1s (match/remove's
	// own convention), which would silently change add-bumper's existing effective behavior for
	// anyone not passing the flag. add-bumper's own default (0.2s) matches what it already hardcoded
	// before this option existed at all -- same reasoning ScanCommand's own local --sample-interval
	// already applies for the identical reason (docs/iterativeplan.md, 2026-08-09).
	static readonly Option<TimeSpan> SampleInterval = new("--sample-interval") {
		Description = "Seconds between sampled frames within the bumper's region. Default 0.2s -- " +
			"bumper clips are always short, so this stays dense by default rather than match/remove's " +
			"1s (tuned for their longer default search windows).",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VbrConfig.Current.Sampling.AddBumperSampleIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VbrConfig.Current.Sampling.AddBumperSampleIntervalSeconds)),
	};

	internal static Command Build() {
		var cmd = new Command("add-bumper",
			"Add one bumper to a named catalog -- samples --clip-from's requested region, extracts " +
			"a reference clip and thumbnail, and writes a new catalog entry. Does not match or remove " +
			"anything; see 'vbr match'/'vbr remove' for that.");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(LabelOption);
		cmd.Options.Add(DescriptionOption);
		cmd.Options.Add(TagsOption);
		cmd.Options.Add(CatalogDb);
		cmd.Options.Add(SampleInterval);
		cmd.Options.Add(DumpFrames);
		cmd.Options.Add(Verbose);
		cmd.Options.Add(HardwareAccel);
		cmd.Options.Add(NoNativeFfmpegBinding);

		cmd.SetAction(async (parseResult, ct) => {
			FileInfo? clipFrom = parseResult.GetValue(ClipFrom);
			ClipEdge? regionArg = parseResult.GetValue(Region);
			TimeSpan clipLength = parseResult.GetValue(ClipLength);
			string label = parseResult.GetValue(LabelOption) ?? string.Empty;
			string? description = parseResult.GetValue(DescriptionOption);
			string? tagsArg = parseResult.GetValue(TagsOption);
			FileInfo? catalogDbArg = parseResult.GetValue(CatalogDb);
			TimeSpan sampleInterval = parseResult.GetValue(SampleInterval);
			DirectoryInfo? dumpFrames = parseResult.GetValue(DumpFrames);
			bool verbose = parseResult.GetValue(Verbose);
			HardwareAcceleration.Mode = parseResult.GetValue(HardwareAccel);
			HardwareAcceleration.NativeFfmpegBinding = !parseResult.GetValue(NoNativeFfmpegBinding);
			HardwareAcceleration.ReportDecodeRequest();

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			if (clipFrom is null) {
				Console.Error.WriteLine("Error: --clip-from is required.");
				return 1;
			}
			// --region/--clip-length lost their declarative Required=true (they're shared Option
			// instances, and remove now needs them optional for --bumper-label — see
			// docs/iterativeplan.md, "Utilizing Databases" entry) -- add-bumper still requires both,
			// just checked here instead of by the parser.
			if (regionArg is null) {
				Console.Error.WriteLine("Error: --region is required.");
				return 1;
			}
			if (clipLength <= TimeSpan.Zero) {
				Console.Error.WriteLine("Error: --clip-length is required.");
				return 1;
			}
			ClipEdge region = regionArg.Value;
			if (string.IsNullOrWhiteSpace(label)) {
				Console.Error.WriteLine("Error: --label is required.");
				return 1;
			}
			if (label.Length > MaxLabelLength) {
				Console.Error.WriteLine($"Error: --label must be {MaxLabelLength} characters or fewer (got {label.Length}).");
				return 1;
			}
			if (description is not null && description.Length > MaxDescriptionLength) {
				Console.Error.WriteLine($"Error: --description must be {MaxDescriptionLength} characters or fewer (got {description.Length}).");
				return 1;
			}
			// Same class of mistake --library-db/--log-file guard against elsewhere (docs/
			// iterativeplan.md, "Post-ship fix #2" / "File-path DB options" entry) -- --catalog-db
			// names a *file*, so an existing directory already sitting at that path can never work,
			// worth catching up front.
			if (catalogDbArg is not null && Directory.Exists(catalogDbArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --catalog-db must be a file path, but a directory already exists there: '{catalogDbArg.FullName}'.");
				return 1;
			}

			string[] tags = string.IsNullOrWhiteSpace(tagsArg)
				? Array.Empty<string>()
				: tagsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			string catalogPath = BumperCatalogStore.ResolvePath(catalogDbArg?.FullName);
			string clipsFolder = Path.Combine(Path.GetDirectoryName(catalogPath)!, "clips");

			await EnsureAiComponentsReadyAsync(HardwareAcceleration.PreferDirectML, ct);

			BumperCatalog catalog;
			try {
				catalog = BumperCatalogStore.Load(catalogPath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return 1;
			}
			// Display-only (docs/iterativeplan.md, "File-path DB options" entry, Part 1) -- derived
			// from the file itself rather than a separately-specified name, so there is exactly one
			// thing to keep in sync between a catalog and its on-disk identity.
			string catalogName = Path.GetFileNameWithoutExtension(catalogPath);
			catalog.CatalogName = catalogName;
			// Recipe-staleness stamp (docs/iterativeplan.md, "File-path DB options" entry, Part 3) --
			// re-captured on every save, reflecting whatever frameQuality settings this run's own
			// AddBumper call below actually used.
			catalog.FrameQualitySnapshot = VBR.Core.Configuration.FrameQualitySnapshot.CaptureCurrent();

			// Decided (docs/iterativeplan.md, "CLI terminology & multi-folder libraries" entry,
			// 2026-07-29): labels are unique *within* a catalog, not globally -- two different
			// catalogs may each have their own "Studio ident" without conflict. Checked here,
			// before the (expensive: ffmpeg decode + ONNX inference) builder call, not after --
			// same "fail fast on a cheap check before expensive work" principle already applied to
			// --catalog-db's existing-directory guard above.
			if (catalog.Entries.Values.Any(e => string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase))) {
				Console.Error.WriteLine(
					$"Error: catalog '{catalogName}' already has a bumper labeled '{label}' -- labels must " +
					"be unique within a catalog. Use a different --label (bumper rename/edit isn't a CLI " +
					"command yet, so an existing entry can't be renamed out of the way).");
				return 1;
			}

			BumperCatalogEntry entry;
			try {
				entry = BumperCatalogBuilder.AddBumper(
					clipFrom.FullName, region, clipLength, label, description, tags, clipsFolder,
					sampleInterval, dumpFrames?.FullName, verbose, ct);
			}
			catch (OperationCanceledException) {
				Console.Error.WriteLine("Cancelled — nothing was added.");
				return 1;
			}
			catch (Exception ex) when (ex is FileNotFoundException or ArgumentOutOfRangeException or InvalidOperationException) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return 1;
			}

			catalog.Entries[entry.Id] = entry;
			try {
				BumperCatalogStore.Save(catalog, catalogPath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: could not save the catalog: {ex.Message}");
				return 1;
			}

			Console.WriteLine($"Added bumper '{entry.Label}' (id {entry.Id}) to catalog '{catalogName}'.");
			Console.WriteLine($"  Region: {entry.Region}, Duration: {entry.Duration.TotalSeconds:0.###}s, " +
				$"Fingerprints: {entry.Fingerprints.Length}, Thumbnail: {(entry.Thumbnail.Length > 0 ? $"{entry.Thumbnail.Length:N0} bytes" : "none")}");
			Console.WriteLine($"  Reference clip: {Path.Combine(clipsFolder, entry.Id + ".mkv")}");
			Console.WriteLine($"Catalog: {catalogPath}");
			return 0;
		});

		return cmd;
	}
}
