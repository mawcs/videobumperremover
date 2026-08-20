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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- UnitRangeOrNull backs
// add-bumper's four Group A threshold-override flags (docs/iterativeplan.md, "Per-bumper matching
// strategy" entry); previously only exercised by hand.

using VBR.CLI.Commands;

namespace VBR.Tests.CLI.Commands;

public class AddBumperValidationTests {
	[Theory]
	[InlineData(0.85f)]
	[InlineData(0.001f)] // just above 0, the open end of the (0, 1] range
	[InlineData(1.0f)]   // the closed end
	public void InRangeValue_ReturnsTrueWithNoError(float value) {
		bool ok = AddBumperCommand.UnitRangeOrNull(value, "--presence-threshold-override", out string? error);
		Assert.True(ok);
		Assert.Null(error);
	}

	[Fact]
	public void NullValue_OmittedFlag_ReturnsTrueWithNoError() {
		// The "inherit the global config value" default -- omitting the flag must never itself be
		// treated as an error.
		bool ok = AddBumperCommand.UnitRangeOrNull(null, "--presence-threshold-override", out string? error);
		Assert.True(ok);
		Assert.Null(error);
	}

	[Theory]
	[InlineData(0f)]     // the open end itself is excluded
	[InlineData(-0.5f)]
	[InlineData(1.5f)]
	[InlineData(100f)]
	public void OutOfRangeValue_ReturnsFalseWithError(float value) {
		bool ok = AddBumperCommand.UnitRangeOrNull(value, "--presence-threshold-override", out string? error);
		Assert.False(ok);
		Assert.NotNull(error);
		Assert.Contains("--presence-threshold-override", error);
	}

	[Fact]
	public void ErrorMessage_NamesTheOffendingFlagAndValue() {
		AddBumperCommand.UnitRangeOrNull(2.5f, "--audio-min-similarity-override", out string? error);
		Assert.Contains("--audio-min-similarity-override", error);
		Assert.Contains("2.5", error);
	}
}
