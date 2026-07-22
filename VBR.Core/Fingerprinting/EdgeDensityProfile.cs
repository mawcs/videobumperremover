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

namespace VBR.Core.Fingerprinting;

/// <summary>
/// The three knobs a mixed-density edge scan needs (docs/iterativeplan.md, "Mixed-density
/// edge/middle fingerprinting"): how far from the true edge the ultra-dense zone extends, and the
/// sampling interval on each side of that boundary. Bundled as one value so they thread through
/// <see cref="MixedDensitySampler"/> together instead of as three loose parameters.
/// </summary>
public readonly record struct EdgeDensityProfile(TimeSpan EdgeBoundary, TimeSpan DenseInterval, TimeSpan SparseInterval);
