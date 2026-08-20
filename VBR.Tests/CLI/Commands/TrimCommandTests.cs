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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3. --length and --paths are
// both Required=true at the Option level, so *omitting* either only exercises System.CommandLine's
// own built-in enforcement, not TrimCommand's own code -- these tests instead give each option a
// value that parses successfully but fails TrimCommand's own manual check, which is the actual
// previously-untested logic: a present-but-non-positive --length, and a present-but-empty-after-split
// --paths (an all-semicolons/whitespace value, distinct from the folder-walk-found-nothing case
// covered by the separate "--paths resolved to no video files" message later in the handler).
// Path.GetTempPath() stands in for "some real folder" where --paths itself must parse (its
// CustomParser validates existence at parse time) without depending on any project-specific fixture.

using System.CommandLine;
using VBR.CLI.Commands;

namespace VBR.Tests.CLI.Commands;

public class TrimCommandTests {
	[Fact]
	public async Task NonPositiveLength_IsRejected() {
		Command cmd = TrimCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--length", "0s", "--region", "begin", "--paths", Path.GetTempPath());

		Assert.Equal(1, exitCode);
		Assert.Contains("--length must be positive", output);
	}

	[Fact]
	public async Task NegativeLength_IsRejected() {
		Command cmd = TrimCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--length", "-5s", "--region", "begin", "--paths", Path.GetTempPath());

		Assert.Equal(1, exitCode);
		Assert.Contains("--length must be positive", output);
	}

	[Fact]
	public async Task PathsResolvingToEmpty_IsRejected() {
		Command cmd = TrimCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--length", "5s", "--region", "begin", "--paths", ";  ;");

		Assert.Equal(1, exitCode);
		Assert.Contains("--paths is required", output);
	}
}
