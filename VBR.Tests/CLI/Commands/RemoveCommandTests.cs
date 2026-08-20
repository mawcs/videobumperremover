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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- the mutual-exclusivity/
// requiredness checks in RemoveCommand's action, previously only exercised by hand. Every case here
// fails at one of these checks, which run before any file/catalog access, so no real bumper/library
// content is ever needed -- --clip-from/--file/--library-db paths below are deliberately fake.
// The one exception is --library itself, whose CustomParser validates folder existence at PARSE
// time (before the handler ever runs) -- Path.GetTempPath() stands in for "some real folder,"
// without creating or depending on any project-specific fixture.

using System.CommandLine;
using VBR.CLI.Commands;

namespace VBR.Tests.CLI.Commands;

public class RemoveCommandTests {
	[Fact]
	public async Task BumperLabelWithClipFrom_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--bumper-label", "some-bumper",
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s",
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("--bumper-label is invalid together with --clip-from/--region/--clip-length", output);
	}

	[Fact]
	public async Task NeitherAdHocNorBumperLabel_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("one of --clip-from/--region/--clip-length or --bumper-label is required", output);
	}

	[Fact]
	public async Task MatchingStrategyWithBumperLabel_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--bumper-label", "some-bumper", "--matching-strategy", "visualonly",
			"--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("--matching-strategy is invalid together with --bumper-label", output);
	}

	[Fact]
	public async Task LibraryWithFile_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s",
			"--library", Path.GetTempPath(), "--file", "C:\\nonexistent-candidate.mkv");

		Assert.Equal(1, exitCode);
		Assert.Contains("specify only one of --library, --library-db, or --file", output);
	}

	[Fact]
	public async Task LibraryWithLibraryDb_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s",
			"--library", Path.GetTempPath(), "--library-db", "C:\\nonexistent.vbrdb");

		Assert.Equal(1, exitCode);
		Assert.Contains("--library-db is invalid together with --library", output);
	}

	[Fact]
	public async Task NoCandidateSourceGiven_IsRejected() {
		Command cmd = RemoveCommand.Build();
		(int exitCode, string output) = await CliInvocation.RunAsync(cmd,
			"--clip-from", "C:\\nonexistent.mkv", "--region", "begin", "--clip-length", "5s");

		Assert.Equal(1, exitCode);
		Assert.Contains("one of --library, --library-db, or --file is required", output);
	}
}
