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
using System.Linq;
using VBR.Core.Catalog;
using static VBR.CLI.Commands.SharedOptions;

namespace VBR.CLI.Commands;

/// <summary>
/// <c>vbr list-bumpers</c> — lists the bumpers in a catalog (docs/iterativeplan.md, "Bumper CRUD
/// Part 1"). Read-only: loads the catalog named by <c>--catalog-db</c> (default: <c>default.vbrcat</c>
/// under VBR's own state folder — see "File-path DB options" entry) and prints one line per entry,
/// plus materializes each entry's embedded <see
/// cref="BumperCatalogEntry.Thumbnail"/> bytes to a real file under the system temp folder so it can
/// actually be viewed -- the catalog itself only ever stores the thumbnail in-line (see
/// <see cref="BumperCatalog"/>'s doc comment on the "Bumper catalog" plan for why).
/// </summary>
internal static class ListBumpersCommand {
	const string ThumbnailFolderName = ".vbrthumbs";

	static readonly Option<bool> ShowGuids = new("--show-guids") {
		Description = "Print each bumper's GUID on its own line immediately before that bumper's " +
			"regular output line.",
	};

	internal static Command Build() {
		var cmd = new Command("list-bumpers",
			"List the bumpers in a catalog: one line each, '\"label\", region, length, \"thumbnail location\"'.");
		cmd.Options.Add(CatalogDb);
		cmd.Options.Add(ShowGuids);

		cmd.SetAction((parseResult, ct) => {
			FileInfo? catalogDbArg = parseResult.GetValue(CatalogDb);
			bool showGuids = parseResult.GetValue(ShowGuids);

			// Same guard as add-bumper's --catalog-db: it names a file, so an existing directory
			// already sitting at that path can never work.
			if (catalogDbArg is not null && Directory.Exists(catalogDbArg.FullName)) {
				Console.Error.WriteLine(
					$"Error: --catalog-db must be a file path, but a directory already exists there: '{catalogDbArg.FullName}'.");
				return Task.FromResult(1);
			}

			string catalogPath = BumperCatalogStore.ResolvePath(catalogDbArg?.FullName);
			string catalogName = Path.GetFileNameWithoutExtension(catalogPath);

			BumperCatalog catalog;
			try {
				catalog = BumperCatalogStore.Load(catalogPath);
			}
			catch (Exception ex) {
				Console.Error.WriteLine($"Error: {ex.Message}");
				return Task.FromResult(1);
			}

			if (catalog.Entries.Count == 0) {
				Console.WriteLine($"Catalog '{catalogName}' has no bumpers.");
				return Task.FromResult(0);
			}

			string thumbnailFolder = Path.Combine(Path.GetTempPath(), ThumbnailFolderName);
			Directory.CreateDirectory(thumbnailFolder);

			foreach (BumperCatalogEntry entry in catalog.Entries.Values.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase)) {
				string thumbnailLocation = "none";
				if (entry.Thumbnail.Length > 0) {
					string thumbnailPath = Path.Combine(thumbnailFolder, SanitizeFileName(entry.Label) + "-thumbnail.jpg");
					File.WriteAllBytes(thumbnailPath, entry.Thumbnail);
					thumbnailLocation = $"\"{thumbnailPath}\"";
				}

				if (showGuids)
					Console.WriteLine(entry.Id);
				Console.WriteLine($"\"{entry.Label}\", {entry.Region}, {SharedOptions.FormatSeconds(entry.Duration)}, {thumbnailLocation}");
			}

			return Task.FromResult(0);
		});

		return cmd;
	}

	static string SanitizeFileName(string name) {
		foreach (char c in Path.GetInvalidFileNameChars())
			name = name.Replace(c, '_');
		return name.Length == 0 ? "bumper" : name;
	}
}
