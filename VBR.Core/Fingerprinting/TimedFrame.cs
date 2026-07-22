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
/// One embedded frame with an explicit real timestamp (seconds from the start of the sampled
/// region), replacing the implicit <c>index × interval</c> that <c>DenseEmbeddingStore.DenseRecord</c>
/// relies on — a formula that only holds for a single uniform interval. The minimal slice of ADR
/// 0006 decisions 4/5's non-uniform <c>(timestamp, value)</c> model needed to represent
/// mixed-density data at all; not the persistent sidecar record, just the in-memory shape.
///
/// Deliberately embedding-only for now, not a general multi-signal container: the maintainer
/// intends to add pHash as a second per-position signal soon (docs/iterativeplan.md). When that
/// lands, it plugs into <see cref="MixedDensitySampler"/>'s frame-gathering stage (already
/// factored out from embedding — see that class) rather than into this type, since a pHash is
/// computed from the same already-decoded RGB24 frame, not from an embedding.
/// </summary>
public readonly record struct TimedFrame(double TimestampSeconds, byte[] Embedding);
