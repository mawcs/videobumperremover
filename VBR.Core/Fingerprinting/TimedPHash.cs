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

namespace VBR.Core.Fingerprinting;

/// <summary>
/// One pHash with an explicit real timestamp — the pHash counterpart to <see cref="TimedFrame"/>,
/// kept as its own parallel type rather than a field on <see cref="TimedFrame"/> per that type's
/// doc comment: a pHash is a second, independent per-position signal computed from the same
/// already-decoded frame as the DINOv2 embedding, not a property of the embedding itself. See
/// <see cref="FrameHashing"/> for how it's computed and <see cref="MixedDensitySampler.SampleWithPHash"/>
/// for how it's gathered alongside embeddings in one decode pass.
/// </summary>
public readonly record struct TimedPHash(double TimestampSeconds, ulong PHash);
