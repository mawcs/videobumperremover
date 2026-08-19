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

namespace VBR.CLI.Commands;

/// <summary>
/// Shared "process one candidate, don't swallow cancellation" shape for <c>match</c>/<c>remove</c>/
/// <c>trim</c>'s per-candidate loops. Extracted 2026-08-17 (docs/iterativeplan.md, "CLI test
/// coverage" entry) after <see cref="OperationCanceledException"/> was found being silently absorbed
/// by the same <c>catch (Exception ex)</c> shape, duplicated inline in three places (once each in
/// <c>TrimCommand</c>/<c>MatchCommand</c>, twice in <c>RemoveCommand</c>) — a canceled candidate was
/// turned into an ordinary per-file error row instead of actually stopping the command, which is how
/// Ctrl+C ended up "backgrounding the process with no way to foreground it." One shared, tested
/// implementation instead of three independently-maintained copies of the same fix.
/// </summary>
internal static class CandidateWork {
	/// <summary>Runs <paramref name="work"/> for one candidate. A genuine failure (anything other
	/// than cancellation) is handed to <paramref name="onError"/> to become that candidate's own
	/// error row, matching this project's established "one bad file doesn't abort the whole run"
	/// behavior. <see cref="OperationCanceledException"/> is deliberately NOT caught here — it
	/// propagates uncaught, out of the calling command's candidate loop entirely, so a cancellation
	/// (Ctrl+C mid-run) actually stops the command instead of being absorbed as an ordinary per-file
	/// error and the loop carrying on regardless.</summary>
	internal static TRow Run<TRow>(Func<TRow> work, Func<Exception, TRow> onError) {
		try {
			return work();
		}
		catch (Exception ex) when (ex is not OperationCanceledException) {
			return onError(ex);
		}
	}
}
