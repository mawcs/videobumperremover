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

namespace VBR.Core.Catalog;

/// <summary>
/// Which signal(s) must agree for one specific bumper to count as present — docs/iterativeplan.md,
/// "Per-bumper matching strategy" entry (2026-08-13). Motivated by real testing: some bumpers are
/// visually unidentifiable but audibly clear (thin/flowing motion graphics DINOv2 can't represent
/// consistently), and no single global <c>--detection-mode</c> fits every bumper in one catalog at
/// once. Resolves, per entry, onto three independent internal flags
/// (<c>MatchingSession.SignalInclusion</c>: <c>UseVisual</c>/<c>UseAudio</c>/<c>UsePHash</c>) rather
/// than growing a switch per named value — every one of the seven values below leaves at least one
/// flag <c>true</c> by construction, so there is no "excluded everything" case to guard against.
/// Overrides <c>--detection-mode</c> outright for the resolved bumper (not an intersection with it)
/// — e.g. a <see cref="VisualOnly"/> bumper samples/consults only visual even under
/// <c>--detection-mode all</c>, since intersecting the two independently-chosen sets could produce a
/// bumper with nothing left able to decide at all.
/// </summary>
public enum BumperMatchingStrategy {
	/// <summary>Default — every signal that ran must agree (today's behavior, unchanged for every
	/// entry that predates this field): visual, plus pHash unconditionally when it ran, plus audio
	/// when it ran and the reference clip's own audio is real
	/// (<c>MatchingSession.ReferenceHasUsableAudio</c>).</summary>
	Corroborated,

	/// <summary>Only visual is sampled/consulted; audio and pHash are skipped entirely for this
	/// bumper.</summary>
	VisualOnly,

	/// <summary>Only audio is consulted; visual and pHash are skipped entirely (no video frames are
	/// sampled/embedded for this bumper at all) — the fix for content DINOv2 can't represent
	/// consistently but that has real, distinguishing audio.</summary>
	AudioOnly,

	/// <summary>Only pHash is consulted; visual and audio are skipped entirely.</summary>
	PhashOnly,

	/// <summary>Audio and pHash must both agree; visual is excluded.</summary>
	NoVisual,

	/// <summary>Visual and pHash must both agree; audio is excluded — for a bumper whose audio is
	/// real but not its own signature (e.g. borrowed underlying content score/music), so audio would
	/// otherwise veto a genuine match.</summary>
	NoAudio,

	/// <summary>Visual and audio must both agree; pHash is excluded.</summary>
	NoPhash,
}
