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
/// <c>--catalog-name</c> is independent of any media folder — deliberately no
/// <c>--library</c>/folder argument anywhere (post-ship simplification, 2026-07-28). An earlier
/// version mirrored <c>vbr scan</c>'s <c>--library</c>/<c>--library-name</c> pair, which wrongly
/// implied a catalog belongs to one scanned library; tracing the code showed <c>--library</c> was
/// only ever read to derive a name, never used for anything a folder is actually needed for, and
/// the maintainer's own review concluded a catalog should be nameable and reusable independent of
/// any specific media collection. See <see cref="BumperCatalog"/>'s doc comment.
/// </summary>
internal static class AddBumperCommand {
	const int MaxLabelLength = 30;
	const int MaxDescriptionLength = 255;

	static readonly Option<string> LabelOption = new("--label") {
		Description = $"Short, human-facing name for this bumper (max {MaxLabelLength} characters), " +
			"e.g. 'Disney FBI warning 2003' -- the one field you must supply yourself; no auto-suggestion. " +
			"Must be unique within the target --catalog-name (case-insensitive); other catalogs may " +
			"reuse the same label freely.",
		Required = true,
	};

	static readonly Option<string> DescriptionOption = new("--description") {
		Description = $"Optional longer free text for curation context (max {MaxDescriptionLength} characters).",
	};

	static readonly Option<string> TagsOption = new("--tags") {
		Description = "Optional comma-separated tags, e.g. \"disney,fbi-warning,2003\".",
	};

	static readonly Option<string> CatalogNameOption = new("--catalog-name") {
		Description = "Name for this catalog -- also names its file (a .vbrcat under " +
			"--catalog-db-folder). Independent of any media folder, so the same catalog can be used " +
			"across different libraries.",
		Required = true,
	};

	static readonly Option<DirectoryInfo> CatalogDbFolder = new("--catalog-db-folder") {
		Description = "Folder to hold this catalog's file. Doesn't need to exist yet; created on " +
			"first save. Default: a dedicated folder under VBR's own state folder.",
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
		cmd.Options.Add(CatalogNameOption);
		cmd.Options.Add(CatalogDbFolder);
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
			string catalogName = parseResult.GetValue(CatalogNameOption) ?? string.Empty;
			DirectoryInfo? catalogDbFolderArg = parseResult.GetValue(CatalogDbFolder);
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
			if (string.IsNullOrWhiteSpace(catalogName)) {
				Console.Error.WriteLine("Error: --catalog-name is required.");
				return 1;
			}
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
			// Same class of mistake --library-db-folder/--log-file guard against elsewhere
			// (docs/iterativeplan.md, "Post-ship fix #2") -- --catalog-db-folder is a *folder*, so a
			// file already sitting at that path can never work as one, worth catching up front.
			if (catalogDbFolderArg is not null && File.Exists(catalogDbFolderArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --catalog-db-folder must be a folder, but a file already exists there: '{catalogDbFolderArg.FullName}'.");
				return 1;
			}

			string[] tags = string.IsNullOrWhiteSpace(tagsArg)
				? Array.Empty<string>()
				: tagsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			string catalogPath = BumperCatalogStore.ResolveCatalogPath(catalogDbFolderArg?.FullName, catalogName);
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
			catalog.CatalogName = catalogName;

			// Decided (docs/iterativeplan.md, "CLI terminology & multi-folder libraries" entry,
			// 2026-07-29): labels are unique *within* a catalog, not globally -- two different
			// catalogs may each have their own "Studio ident" without conflict. Checked here,
			// before the (expensive: ffmpeg decode + ONNX inference) builder call, not after --
			// same "fail fast on a cheap check before expensive work" principle already applied to
			// --catalog-db-folder's existing-file guard above.
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
					clipFrom.FullName, region, clipLength, label, description, tags, clipsFolder, verbose, ct);
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
