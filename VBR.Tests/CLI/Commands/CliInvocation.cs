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

using System.CommandLine;

namespace VBR.Tests.CLI.Commands;

/// <summary>
/// Shared in-process CLI invocation helper for the argument-validation tests (docs/iterativeplan.md,
/// "CLI test coverage" entry, 2026-08-17, Step 3) — no subprocess, no `dotnet run`: parses real
/// arguments through the command's own (already-`internal`) `Build()` and invokes it directly,
/// capturing whatever it wrote to <see cref="Console.Out"/>/<see cref="Console.Error"/>. Safe only
/// for argument combinations that fail validation before any real file/catalog/network access — see
/// each test file's own notes on which checks that covers.
/// </summary>
static class CliInvocation {
	/// <summary>Runs <paramref name="cmd"/> against <paramref name="args"/>, returning the process
	/// exit code and everything printed to stdout/stderr combined (order preserved, matching what a
	/// real terminal session would show). Console redirection is restored before returning, even on
	/// an unexpected exception, so one test's redirection can never leak into another's.</summary>
	internal static async Task<(int ExitCode, string Output)> RunAsync(Command cmd, params string[] args) {
		var writer = new StringWriter();
		TextWriter originalOut = Console.Out;
		TextWriter originalError = Console.Error;
		Console.SetOut(writer);
		Console.SetError(writer);
		try {
			ParseResult parseResult = cmd.Parse(args);
			int exitCode = await parseResult.InvokeAsync();
			return (exitCode, writer.ToString());
		}
		finally {
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}
}
