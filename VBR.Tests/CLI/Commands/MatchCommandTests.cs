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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- mirrors
// RemoveCommandTests.cs: match shares this exact validation logic (same wording, same check order)
// with remove, so the same rationale applies here (see that file's own header note).

using System.CommandLine;
using VBR.CLI.Commands;

namespace VBR.Tests.CLI.Commands;

public class MatchCommandTests {
	[Fact]
	public async Task BumperLabelWithClipFrom_IsRejected() {
		Command cmd = MatchCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--bumper-label", "some-bumper",
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s",
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("--bumper-label is invalid together with --clip-from/--region/--clip-length", output);
	}

	[Fact]
	public async Task NeitherAdHocNorBumperLabel_IsRejected() {
		Command cmd = MatchCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("one of --clip-from/--region/--clip-length or --bumper-label is required", output);
	}

	[Fact]
	public async Task MatchingStrategyWithBumperLabel_IsRejected() {
		Command cmd = MatchCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--bumper-label", "some-bumper", "--matching-strategy", "visualonly",
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("--matching-strategy is invalid together with --bumper-label", output);
	}

	[Fact]
	public async Task LibraryWithFile_IsRejected() {
		Command cmd = MatchCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s",
			"--library", Path.GetTempPath(), "--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("specify only one of --library, --library-db, or --file", output);
	}

	[Fact]
	public async Task NoCandidateSourceGiven_IsRejected() {
		Command cmd = MatchCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s");

		Assert.Equal(1, exitCode);
		Assert.Contains("one of --library, --library-db, or --file is required", output);
	}
}
