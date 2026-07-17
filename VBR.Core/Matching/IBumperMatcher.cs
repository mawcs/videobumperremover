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

using System.Threading;
using VBR.Core.Extraction;

namespace VBR.Core.Matching;

/// <summary>One matcher's verdict for a single (reference clip, candidate file) pair, scoped to
/// the requested search region. <c>Detail</c> is a pre-formatted, matcher-specific summary for
/// CLI/report printing (e.g. "present=24/24  rigid=98%@8s" or "full=97%@40s") — there is exactly
/// one consumer of this today (the CLI), so a formatted string is deliberately simpler than a
/// speculative structured detail hierarchy.</summary>
public readonly record struct MatchResult(bool Present, float BestScore, double? BestOffsetSeconds, string Detail);

/// <summary>
/// Common contract for a bumper-matching signal (visual, audio, ...). See
/// docs/design/matcher-spec.md for the priority order between signals — this interface only
/// covers a single-signal, single-candidate comparison; orchestrating multiple signals and
/// looping over a library folder is the caller's job (currently VBR.CLI), not this contract's.
/// Implementations own their own extraction/fingerprinting internally — callers pass file paths
/// and a region, not pre-computed fingerprints, so different signals can use completely
/// different representations under the hood.
/// </summary>
public interface IBumperMatcher {
	/// <summary>Short name for reporting, e.g. "visual", "audio".</summary>
	string Name { get; }

	/// <summary>
	/// Does <paramref name="referenceClipPath"/> (an already-extracted clip — see
	/// <see cref="ClipExtractor"/>, never a user-supplied file) appear in
	/// <paramref name="candidatePath"/>, searching only <paramref name="searchRegion"/> of the
	/// candidate?
	/// </summary>
	MatchResult Match(string referenceClipPath, string candidatePath, ClipRegion searchRegion, CancellationToken ct = default);
}
