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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- MatchingSession's
// ApplyMatchingStrategy table (docs/iterativeplan.md, "Per-bumper matching strategy" entry,
// 2026-08-13) had never had a direct test; only ever exercised indirectly through a full
// PrepareFromCatalogEntry call, which needs a real catalog entry and sampling machinery. The
// constructor and Wants*/ApplyMatchingStrategy were widened from private to internal specifically
// to make this file possible (docs/iterativeplan.md, "CLI test coverage" entry, Step 2).

using VBR.CLI.Commands;
using VBR.Core.Catalog;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;

namespace VBR.Tests.CLI.Commands;

public class MatchingSessionStrategyTests {
	// None of these values matter to the strategy table itself -- only ApplyMatchingStrategy's own
	// switch and the resulting Wants* properties are under test here. mode is deliberately `all`
	// (every signal on) so it never coincidentally matches what a strategy override would produce,
	// making it obvious in each assertion that the override, not the mode fallback, decided.
	static MatchingSession NewSession(DetectionMode mode = DetectionMode.all) =>
		new(mode, ClipEdge.begin, new EdgeDensityProfile(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
			presenceThreshold: 0.9f, rigidHitThreshold: 0.89f, phashPresenceThreshold: 0.96f, minSimilarity: 0.8f,
			dumpFramesDir: null, verboseLogging: false);

	[Theory]
	[InlineData(BumperMatchingStrategy.Corroborated, true, true, true)]
	[InlineData(BumperMatchingStrategy.VisualOnly, true, false, false)]
	[InlineData(BumperMatchingStrategy.AudioOnly, false, true, false)]
	[InlineData(BumperMatchingStrategy.PhashOnly, false, false, true)]
	[InlineData(BumperMatchingStrategy.NoVisual, false, true, true)]
	[InlineData(BumperMatchingStrategy.NoAudio, true, false, true)]
	[InlineData(BumperMatchingStrategy.NoPhash, true, true, false)]
	public void ApplyMatchingStrategy_ResolvesTheDocumentedTable(
			BumperMatchingStrategy strategy, bool expectVisual, bool expectAudio, bool expectPHash) {
		// Started under --detection-mode audio deliberately (the opposite of most expected results
		// above) -- if a strategy override ever silently fell back to intersecting with mode instead
		// of overriding it outright, at least one of these seven cases would catch it.
		MatchingSession session = NewSession(DetectionMode.audio);
		session.ApplyMatchingStrategy(strategy);

		Assert.Equal(expectVisual, session.WantsVisual);
		Assert.Equal(expectAudio, session.WantsAudio);
		Assert.Equal(expectPHash, session.WantsPHash);
	}

	// DetectionMode is internal (not public), so it can't appear in a public [Theory] method's
	// signature (CS0051) -- five separate [Fact]s instead of one [Theory]/[InlineData] set.
	// ApplyMatchingStrategy is never called in any of these -- this is PrepareAsync's own ad hoc
	// shape (no BumperMatchingStrategy exists for a --clip-from bumper unless --matching-strategy
	// was explicitly given), confirming the nullable override fields genuinely default to "unset"
	// rather than silently defaulting to some strategy's flag triple.
	[Fact]
	public void AdHocSession_VisualMode_WantsVisualOnly() {
		MatchingSession session = NewSession(DetectionMode.visual);
		Assert.True(session.WantsVisual);
		Assert.False(session.WantsAudio);
		Assert.False(session.WantsPHash);
	}

	[Fact]
	public void AdHocSession_AudioMode_WantsAudioOnly() {
		MatchingSession session = NewSession(DetectionMode.audio);
		Assert.False(session.WantsVisual);
		Assert.True(session.WantsAudio);
		Assert.False(session.WantsPHash);
	}

	[Fact]
	public void AdHocSession_PHashMode_WantsPHashOnly() {
		MatchingSession session = NewSession(DetectionMode.phash);
		Assert.False(session.WantsVisual);
		Assert.False(session.WantsAudio);
		Assert.True(session.WantsPHash);
	}

	[Fact]
	public void AdHocSession_BothMode_WantsVisualAndAudioNotPHash() {
		MatchingSession session = NewSession(DetectionMode.both);
		Assert.True(session.WantsVisual);
		Assert.True(session.WantsAudio);
		Assert.False(session.WantsPHash);
	}

	[Fact]
	public void AdHocSession_AllMode_WantsAllThree() {
		MatchingSession session = NewSession(DetectionMode.all);
		Assert.True(session.WantsVisual);
		Assert.True(session.WantsAudio);
		Assert.True(session.WantsPHash);
	}
}
