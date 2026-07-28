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

using MemoryPack;
using VBR.Core.Fingerprinting;

namespace VBR.Core.Catalog;

/// <summary>
/// Registers this namespace's MemoryPack-serializable types explicitly, same rationale (and same
/// pattern) as <c>VBR.Core.Index.MemoryPackRegistration</c>: MemoryPack's lazy reflection-based
/// formatter lookup can be trimmed away under Native AOT. Kept separate from the index's own
/// registration rather than added to it — the catalog is a wholly independent store (per
/// docs/iterativeplan.md's "Bumper catalog" plan) and this project has no code path that needs one
/// without the other, so there's no reason to couple their registration. <see cref="TimedFingerprint"/>
/// is registered here too, not just assumed already-registered via the index's own static
/// constructor: a process that only ever touches <see cref="BumperCatalogStore"/> must not depend
/// on <c>LibraryIndexStore</c> having run first. Called from <see cref="BumperCatalogStore"/>'s
/// static constructor.
/// </summary>
static class CatalogMemoryPackRegistration {
	internal static void Register() {
		MemoryPackFormatterProvider.Register<BumperCatalog>();
		MemoryPackFormatterProvider.Register<BumperCatalogEntry>();
		MemoryPackFormatterProvider.Register<TimedFingerprint>();
	}
}
