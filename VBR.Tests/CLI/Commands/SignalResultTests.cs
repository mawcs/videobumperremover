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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- the specific gap
// PROGRESS.md flagged 2026-08-13 ("Multi-signal corroboration"): SignalResult.Present's decision
// rule (docs/iterativeplan.md, 2026-08-13 "multi-signal" entry) had never had a single test. Pure
// logic, no media, no CLI invocation -- SignalResult is already `internal`, reachable the moment
// VBR.Tests references VBR.CLI at all.

using VBR.CLI.Commands;
using VBR.Core.Matching;

namespace VBR.Tests.CLI.Commands;

public class SignalResultTests {
	static MatchResult Result(bool present) => new(present, present ? 1f : 0f, null, present ? "present" : "absent");

	[Fact]
	public void Present_VisualOnly_TruePropagates() {
		var signal = new SignalResult(Result(true), null, null, AudioApplicable: false);
		Assert.True(signal.Present);
	}

	[Fact]
	public void Present_VisualOnly_FalsePropagates() {
		var signal = new SignalResult(Result(false), null, null, AudioApplicable: false);
		Assert.False(signal.Present);
	}

	[Fact]
	public void Present_AudioOnly_UsesAudioViaFallback() {
		Assert.True(new SignalResult(null, Result(true), null, AudioApplicable: true).Present);
		Assert.False(new SignalResult(null, Result(false), null, AudioApplicable: true).Present);
	}

	[Fact]
	public void Present_PHashOnly_UsesPHashViaFallback() {
		Assert.True(new SignalResult(null, null, Result(true), AudioApplicable: false).Present);
		Assert.False(new SignalResult(null, null, Result(false), AudioApplicable: false).Present);
	}

	[Fact]
	public void Present_NoSignalsRanAtAll_ReturnsFalse() {
		var signal = new SignalResult(null, null, null, AudioApplicable: false);
		Assert.False(signal.Present);
	}

	[Fact]
	public void Present_AllSignalsAgree_ReturnsTrue() {
		var signal = new SignalResult(Result(true), Result(true), Result(true), AudioApplicable: true);
		Assert.True(signal.Present);
	}

	// The corroboration rule's whole point (docs/iterativeplan.md, 2026-08-13 "multi-signal" entry):
	// visual alone is no longer enough once audio/phash also ran and are meaningful.
	[Fact]
	public void Present_AudioDisagrees_VetoesMatch() {
		var signal = new SignalResult(Result(true), Result(false), Result(true), AudioApplicable: true);
		Assert.False(signal.Present);
	}

	[Fact]
	public void Present_PHashDisagrees_VetoesMatch() {
		var signal = new SignalResult(Result(true), Result(true), Result(false), AudioApplicable: true);
		Assert.False(signal.Present);
	}

	[Fact]
	public void Present_VisualDisagrees_VetoesMatchRegardlessOfOthers() {
		var signal = new SignalResult(Result(false), Result(true), Result(true), AudioApplicable: true);
		Assert.False(signal.Present);
	}

	// The silent-bumper exemption: audio ran and disagrees, but the reference side has no real audio
	// of its own to compare against, so its "disagreement" is meaningless and must not veto.
	[Fact]
	public void Present_SilentBumper_AudioNotApplicable_DoesNotVeto() {
		var signal = new SignalResult(Result(true), Result(false), Result(true), AudioApplicable: false);
		Assert.True(signal.Present);
	}

	[Fact]
	public void CombinedDetail_OnlyIncludesNonNullSignals_InVisualAudioPHashOrder() {
		var signal = new SignalResult(Result(true), null, Result(true), AudioApplicable: false);
		Assert.Equal("visual: present  |  phash: present", signal.CombinedDetail);
	}

	[Fact]
	public void CombinedDetail_AllThreeSignals_AllAppearInOrder() {
		var signal = new SignalResult(Result(true), Result(true), Result(true), AudioApplicable: true);
		Assert.Equal("visual: present  |  audio: present  |  phash: present", signal.CombinedDetail);
	}

	[Fact]
	public void CombinedDetail_AllNull_ReturnsNull() {
		var signal = new SignalResult(null, null, null, AudioApplicable: false);
		Assert.Null(signal.CombinedDetail);
	}
}
