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

namespace VBR.Core.Index;

/// <summary>
/// Registers this project's MemoryPack-serializable types explicitly, same rationale as VDF's own
/// <c>VDF.Core.Utils.MemoryPackRegistration</c> (see that class's doc comment): MemoryPack's lazy
/// reflection-based formatter lookup can be trimmed away under Native AOT, so every type this
/// project serializes is rooted here at compile time instead. Called from
/// <see cref="LibraryIndexStore"/>'s static constructor — the only place this project's MemoryPack
/// serialization happens.
/// </summary>
static class MemoryPackRegistration {
	internal static void Register() {
		MemoryPackFormatterProvider.Register<LibraryIndex>();
		MemoryPackFormatterProvider.Register<LibraryIndexEntry>();
		MemoryPackFormatterProvider.Register<TimedFingerprint>();
	}
}
