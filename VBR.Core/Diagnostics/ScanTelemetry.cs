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

using System;
using System.Diagnostics;

namespace VBR.Core.Diagnostics;

/// <summary>
/// Execution diagnostics for `vbr scan` (docs/running_and_building.md's <c>--console-info</c>/
/// <c>--log-level</c> `debug`/`trace` tiers) — off by default, near-zero cost when disabled.
/// Two independent tiers, each with its own gate/event pair so a run can request one without the
/// other:
/// <list type="bullet">
/// <item><c>trace</c> (<see cref="Enabled"/>/<see cref="Time"/>/<see cref="Note"/>/<see cref="Measured"/>):
/// execution timing -- *how long* each phase took (AI/DirectML readiness, ffmpeg spawn vs. native
/// decode, ONNX inference, database checkpointing, ...) -- plus any single-line detail too chatty
/// for `verbose` (per-batch inference counts).</item>
/// <item><c>debug</c> (<see cref="DebugEnabled"/>/<see cref="NoteDebug"/>/<see cref="DebugNoted"/>):
/// per-file diagnostic detail (frame counts, low-information-filter drops, audio-fingerprint
/// stats) that doesn't need full `verbose` (model paths, session lifecycle) to be useful.</item>
/// </list>
///
/// Call sites use <c>using var scope = ScanTelemetry.Time("label");</c> around the code being
/// measured — <see cref="Scope.Detail"/> can be set inside the block for a short trailing note
/// (e.g. which of two code paths actually ran) before it's disposed and the line is emitted. Both
/// gates are set once per command invocation (same convention as
/// <see cref="Extraction.HardwareAcceleration.Mode"/>) — not meant to change mid-run.
/// </summary>
public static class ScanTelemetry {
	public static bool Enabled { get; set; }

	/// <summary>Raised with one ready-to-print line ("label: 123ms" or "label: 123ms (detail)")
	/// whenever a <see cref="Scope"/> completes while <see cref="Enabled"/> is true. The CLI
	/// subscribes to route lines to whichever destination(s) (console/log file) requested trace
	/// detail — this class has no opinion on where lines end up.</summary>
	public static event Action<string>? Measured;

	/// <summary>Starts timing a phase. Returns a real (never null) <see cref="Scope"/> even when
	/// <see cref="Enabled"/> is false, so callers can always set <see cref="Scope.Detail"/> without
	/// a null check — disposing a disabled scope is a no-op (skips the Stopwatch read and the
	/// event entirely, not just the string formatting).</summary>
	public static Scope Time(string label) => new(Enabled ? label : null);

	/// <summary>Subscribes <paramref name="handler"/> to <see cref="Measured"/>, returning an
	/// <see cref="IDisposable"/> that unsubscribes — same convention as
	/// <c>SharedOptions.SubscribeLogging</c>, so a stale handler never outlives the command that
	/// registered it (matters most for VBR.Tests running multiple scans in one process).</summary>
	public static IDisposable Subscribe(Action<string> handler) {
		Measured += handler;
		return new Unsubscriber(() => Measured -= handler);
	}

	/// <summary>A one-off trace-tier line with no duration attached (e.g. per-batch ONNX inference
	/// detail — too chatty for <c>verbose</c>, real diagnostic value only at <c>trace</c>). Routed
	/// through the same <see cref="Measured"/> event/<see cref="Enabled"/> gate as <see cref="Time"/>,
	/// so it needs no separate subscription on the CLI side.</summary>
	public static void Note(string message) {
		if (!Enabled) return;
		Measured?.Invoke(message);
	}

	/// <summary>Debug tier: one step coarser than <see cref="Enabled"/>/trace — per-file diagnostic
	/// detail (frame counts, low-information-filter drops, audio-fingerprint stats) that's too
	/// chatty for the default run but doesn't need full <c>verbose</c> (model paths, session
	/// lifecycle) to be useful. Independent gate/event pair from <see cref="Enabled"/>/<see cref="Measured"/>
	/// since a run can request one tier without the other (e.g. <c>--console-info debug</c> without
	/// <c>verbose</c>/<c>trace</c>).</summary>
	public static bool DebugEnabled { get; set; }

	/// <summary>Raised with one ready-to-print line whenever <see cref="NoteDebug"/> is called while
	/// <see cref="DebugEnabled"/> is true — same routing contract as <see cref="Measured"/>.</summary>
	public static event Action<string>? DebugNoted;

	public static void NoteDebug(string message) {
		if (!DebugEnabled) return;
		DebugNoted?.Invoke(message);
	}

	/// <summary>Subscribes <paramref name="handler"/> to <see cref="DebugNoted"/> — same
	/// unsubscribe-on-dispose contract as <see cref="Subscribe"/>.</summary>
	public static IDisposable SubscribeDebug(Action<string> handler) {
		DebugNoted += handler;
		return new Unsubscriber(() => DebugNoted -= handler);
	}

	public sealed class Scope : IDisposable {
		readonly string? label;
		readonly Stopwatch stopwatch;

		/// <summary>Optional short trailing note, settable any time before disposal (e.g. "fell back
		/// to CLI" once a native-vs-process decision is known) — appended in parentheses if set.</summary>
		public string? Detail;

		internal Scope(string? label) {
			this.label = label;
			stopwatch = label is null ? null! : Stopwatch.StartNew();
		}

		public void Dispose() {
			if (label is null) return; // Enabled was false when Time() was called.
			stopwatch.Stop();
			string line = Detail is null
				? $"{label}: {stopwatch.Elapsed.TotalMilliseconds:0}ms"
				: $"{label}: {stopwatch.Elapsed.TotalMilliseconds:0}ms ({Detail})";
			Measured?.Invoke(line);
		}
	}

	sealed class Unsubscriber(Action unsubscribe) : IDisposable {
		public void Dispose() => unsubscribe();
	}
}
