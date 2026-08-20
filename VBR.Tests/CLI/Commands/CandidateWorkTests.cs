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

// docs/iterativeplan.md, "CLI test coverage" entry (2026-08-17), Step 3 -- the permanent
// regression test for the 2026-08-15 Ctrl+C bug: OperationCanceledException was being swallowed by
// the same bare `catch (Exception ex)` shape, duplicated inline in match/remove/trim's candidate
// loops, so a canceled candidate silently became "this file failed" instead of actually stopping
// the command. CandidateWork.Run is the shared, now-single implementation of the fix; this file is
// what fails immediately if the `when (ex is not OperationCanceledException)` filter is ever
// accidentally reverted.

using VBR.CLI.Commands;

namespace VBR.Tests.CLI.Commands;

public class CandidateWorkTests {
	[Fact]
	public void Run_WorkSucceeds_ReturnsWorkResult() {
		int result = CandidateWork.Run(() => 42, ex => -1);
		Assert.Equal(42, result);
	}

	[Fact]
	public void Run_WorkThrowsOrdinaryException_ReturnsOnErrorResult() {
		var thrown = new InvalidOperationException("boom");
		string result = CandidateWork.Run<string>(
			() => throw thrown,
			ex => $"error: {ex.Message}");
		Assert.Equal("error: boom", result);
	}

	[Fact]
	public void Run_OnErrorNeverInvokedOnSuccess() {
		bool onErrorCalled = false;
		CandidateWork.Run(() => "ok", ex => { onErrorCalled = true; return "unused"; });
		Assert.False(onErrorCalled);
	}

	// The actual regression test: cancellation must propagate uncaught, not become an ordinary
	// per-candidate error.
	[Fact]
	public void Run_WorkThrowsOperationCanceledException_PropagatesUncaught() {
		Assert.Throws<OperationCanceledException>(() =>
			CandidateWork.Run<int>(() => throw new OperationCanceledException(), ex => -1));
	}

	[Fact]
	public void Run_WorkThrowsTaskCanceledException_AlsoPropagatesUncaught() {
		// TaskCanceledException derives from OperationCanceledException -- the exact exception a
		// canceled CancellationToken check (ct.ThrowIfCancellationRequested / a canceled
		// ReadLineAsync) can surface as, not just the base type directly.
		Assert.Throws<TaskCanceledException>(() =>
			CandidateWork.Run<int>(() => throw new TaskCanceledException(), ex => -1));
	}
}
