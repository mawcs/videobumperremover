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

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VBR.Core.Configuration;

/// <summary>
/// The one place VBR's per-OS state-root resolution algorithm lives — extracted 2026-08-12 (docs/
/// iterativeplan.md, "File-path DB options" entry, Part 3) from what used to be two independent
/// copies inside <see cref="Catalog.BumperCatalogStore"/> and <see cref="Database.LibraryDatabaseStore"/>.
/// Both stores, plus <see cref="VbrConfigLoader"/>'s own config-file discovery, need the identical
/// per-OS base folder — this is that shared root; each caller appends its own leaf (<c>catalog</c>,
/// <c>database</c>, or nothing, for config).
/// </summary>
public static class VbrPaths {
	/// <summary>VBR's own per-OS state root (not created here — callers create whatever leaf
	/// subfolder they actually need, or nothing at all for a read-only lookup like the config
	/// loader's).</summary>
	public static string GetStateRootFolder() {
		string baseFolder;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
			baseFolder = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
		}
		else {
			baseFolder = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
		}
		return Path.Combine(baseFolder, "VideoBumperRemover");
	}
}
