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
using VBR.Core.Index;
using VDF.Core.AI;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>
/// <c>vbr add-bumper</c> — adds one bumper to a named library's catalog
/// (docs/iterativeplan.md, "Bumper catalog"). Samples the requested region of <c>--clip-from</c>,
/// extracts a reference clip and a thumbnail, and writes a new entry — mirrors <c>match</c>/
/// <c>remove</c>'s "never accept a pre-cut clip" contract (<c>--clip-from</c>/<c>--region</c>/
/// <c>--clip-length</c>, identical meaning) and <c>vbr scan</c>'s per-library storage shape
/// (<c>--library</c>/<c>--library-name</c>, its own dedicated <c>--catalog-db-folder</c>). Does not
/// match or remove anything, and does not read the catalog back — "apply" (catalog-aware scanning)
/// is separate, later work.
/// </summary>
internal static class AddBumperCommand {
	const int MaxLabelLength = 30;
	const int MaxDescriptionLength = 255;

	static readonly Option<string> LabelOption = new("--label") {
		Description = $"Short, human-facing name for this bumper (max {MaxLabelLength} characters), " +
			"e.g. 'Disney FBI warning 2003' -- the one field you must supply yourself; no auto-suggestion.",
		Required = true,
	};

	static readonly Option<string> DescriptionOption = new("--description") {
		Description = $"Optional longer free text for curation context (max {MaxDescriptionLength} characters).",
	};

	static readonly Option<string> TagsOption = new("--tags") {
		Description = "Optional comma-separated tags, e.g. \"disney,fbi-warning,2003\".",
	};

	static readonly Option<DirectoryInfo> CatalogDbFolder = new("--catalog-db-folder") {
		Description = "Folder to hold this library's bumper catalog file. Doesn't need to exist yet; " +
			"created on first save. Default: a dedicated per-library folder under VBR's own state folder.",
	};

	internal static Command Build() {
		var cmd = new Command("add-bumper",
			"Add one bumper to a library's catalog -- samples --clip-from's requested region, extracts " +
			"a reference clip and thumbnail, and writes a new catalog entry. Does not match or remove " +
			"anything; see 'vbr match'/'vbr remove' for that.");
		cmd.Options.Add(ClipFrom);
		cmd.Options.Add(Region);
		cmd.Options.Add(ClipLength);
		cmd.Options.Add(LabelOption);
		cmd.Options.Add(DescriptionOption);
		cmd.Options.Add(TagsOption);
		cmd.Options.Add(Library);
		cmd.Options.Add(LibraryName);
		cmd.Options.Add(CatalogDbFolder);
		cmd.Options.Add(Verbose);

		cmd.SetAction(async (parseResult, ct) => {
			FileInfo? clipFrom = parseResult.GetValue(ClipFrom);
			var region = parseResult.GetValue(Region);
			TimeSpan clipLength = parseResult.GetValue(ClipLength);
			string label = parseResult.GetValue(LabelOption) ?? string.Empty;
			string? description = parseResult.GetValue(DescriptionOption);
			string? tagsArg = parseResult.GetValue(TagsOption);
			var library = parseResult.GetValue(Library);
			string? libraryNameArg = parseResult.GetValue(LibraryName);
			DirectoryInfo? catalogDbFolderArg = parseResult.GetValue(CatalogDbFolder);
			bool verbose = parseResult.GetValue(Verbose);

			using IDisposable? logSubscription = SubscribeVerboseLogging(verbose);

			if (clipFrom is null) {
				Console.Error.WriteLine("Error: --clip-from is required.");
				return 1;
			}
			if (library is null) {
				Console.Error.WriteLine("Error: --library is required.");
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

			string libraryName = string.IsNullOrWhiteSpace(libraryNameArg)
				? LibraryIndexStore.DeriveLibraryName(library.FullName)
				: libraryNameArg;
			string catalogPath = BumperCatalogStore.ResolveCatalogPath(catalogDbFolderArg?.FullName, libraryName);
			string clipsFolder = Path.Combine(Path.GetDirectoryName(catalogPath)!, "clips");

			if (!AiComponents.IsReady) {
				Console.Error.WriteLine("AI matching components not found — downloading (one-time, ~100MB)...");
				await AiComponents.DownloadAsync(progress: null, ct);
				Console.Error.WriteLine("AI components ready.");
			}

			BumperCatalog catalog;
			try {
				catalog = BumperCatalogStore.Load(catalogPath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return 1;
			}
			catalog.LibraryName = libraryName;

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

			Console.WriteLine($"Added bumper '{entry.Label}' (id {entry.Id}) to catalog '{libraryName}'.");
			Console.WriteLine($"  Region: {entry.Region}, Duration: {entry.Duration.TotalSeconds:0.###}s, " +
				$"Fingerprints: {entry.Fingerprints.Length}, Thumbnail: {(entry.Thumbnail.Length > 0 ? $"{entry.Thumbnail.Length:N0} bytes" : "none")}");
			Console.WriteLine($"  Reference clip: {Path.Combine(clipsFolder, entry.Id + ".mkv")}");
			Console.WriteLine($"Catalog: {catalogPath}");
			return 0;
		});

		return cmd;
	}
}
