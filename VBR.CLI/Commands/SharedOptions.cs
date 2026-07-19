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
using System.CommandLine.Parsing;
using System.Globalization;
using VBR.Core.Extraction;
using VBR.Core.Matching;

namespace VBR.CLI.Commands;

/// <summary>Which signal(s) a command runs. Lowercase members — see
/// VBR.Core.Extraction.ClipEdge for why.</summary>
internal enum DetectionMode { visual, audio, both }

/// <summary>
/// Option definitions and parsing helpers shared by <c>match</c> and <c>remove</c> — per ADR
/// 0007 (docs/decisions/0007-removal-command.md), <c>remove</c> reuses <c>match</c>'s parameter
/// surface unchanged (it runs the identical extraction+matching, then adds a cut). One shared
/// definition per option keeps their help text and parsing identical rather than two copies
/// drifting apart.
/// </summary>
internal static class SharedOptions {

	// CLI numeric arguments parse invariant ('.' decimal) regardless of host locale — see
	// VDF.CLI.Commands.SharedOptions for the same rationale (comma-decimal locales otherwise
	// turn "0.8" into 8).
	internal static float ParseInvariantFloat(ArgumentResult result, float fallback) {
		if (result.Tokens.Count == 0) return fallback;
		string token = result.Tokens[0].Value;
		if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
			return value;
		result.AddError($"'{token}' is not a valid number (use '.' as the decimal separator, e.g. 0.8).");
		return fallback;
	}

	// Accepts a bare number (seconds) or a suffixed value ("5.1s", "200ms").
	internal static TimeSpan ParseDuration(string text) {
		text = text.Trim();
		double unitSeconds = 1.0;
		string numberPart = text;
		if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
			numberPart = text[..^2];
			unitSeconds = 0.001;
		}
		else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
			numberPart = text[..^1];
		}
		if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
			throw new FormatException(
				$"'{text}' is not a valid duration (use a plain number of seconds, or a suffixed value like '5.1s' or '200ms').");
		return TimeSpan.FromSeconds(value * unitSeconds);
	}

	internal static TimeSpan ParseDurationArg(ArgumentResult result, TimeSpan fallback) {
		if (result.Tokens.Count == 0) return fallback;
		try { return ParseDuration(result.Tokens[0].Value); }
		catch (FormatException ex) {
			result.AddError(ex.Message);
			return fallback;
		}
	}

	internal static string FormatSeconds(TimeSpan t) =>
		t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

	internal static readonly Option<FileInfo> ClipFrom = new("--clip-from") {
		Description = "Source video containing the bumper. The reference clip is extracted from " +
			"it internally — this never takes a pre-cut clip file.",
		Required = true,
	};

	internal static readonly Option<ClipEdge> Region = new("--region") {
		Description = "Which edge the bumper lives at (begin|end). Drives both reference-clip " +
			"extraction from --clip-from and the search window in each --library file — a bumper " +
			"lives at one edge, so one choice governs both.",
		Required = true,
	};

	internal static readonly Option<TimeSpan> ClipLength = new("--clip-length") {
		Description = "How much of --clip-from to extract as the reference clip. A plain number " +
			"of seconds, or suffixed like '8s' / '5.1s'.",
		Required = true,
		CustomParser = r => ParseDurationArg(r, TimeSpan.Zero),
	};

	// No DefaultValueFactory: the real default (--clip-length + 20s) depends on another option's
	// value, so it can't be precomputed for the help-text annotation. TimeSpan.Zero here means
	// "not provided" — resolved against --clip-length in the action. A user-requested zero-length
	// search window would be meaningless anyway, so treating <= 0 as "unset" is unambiguous.
	internal static readonly Option<TimeSpan> SearchLength = new("--search-length") {
		Description = "How much of each candidate's edge to search. Default: --clip-length + 20s " +
			"(the search window needs slack beyond the clip's own length).",
		CustomParser = r => r.Tokens.Count == 0 ? TimeSpan.Zero : ParseDurationArg(r, TimeSpan.Zero),
	};

	internal static readonly Option<TimeSpan> SampleInterval = new("--sample-interval") {
		Description = "Visual: seconds between sampled frames — smaller is denser. Default 1s; " +
			"short clips (under ~8s) need it as low as ~0.2s to have enough frames to match on " +
			"— no floor is enforced, go as dense as needed.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds)),
	};

	internal static readonly Option<float> PresenceThreshold = new("--presence-threshold") {
		Description = "Visual: cosine similarity (0-1) at or above which a clip frame counts as present in a candidate.",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultPresenceThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultPresenceThreshold),
	};

	internal static readonly Option<float> RigidHitThreshold = new("--rigid-hit-threshold") {
		Description = "Visual: cosine threshold for VDF's rigid corroborating matcher (report-only, never gates the decision).",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultRigidHitThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultRigidHitThreshold),
	};

	internal static readonly Option<float> MinSimilarity = new("--min-similarity") {
		Description = "Audio: similarity (0-1) at or above which a file is flagged as a match.",
		DefaultValueFactory = _ => AudioBumperMatcher.DefaultMinSimilarity,
		CustomParser = r => ParseInvariantFloat(r, AudioBumperMatcher.DefaultMinSimilarity),
	};

	internal static readonly Option<DetectionMode> Mode = new("--detection-mode") {
		Description = "Which signal(s) to run (visual|audio|both). 'both' runs visual as the " +
			"decision-maker and reports audio alongside as corroboration.",
		DefaultValueFactory = _ => DetectionMode.visual,
	};

	internal static readonly Option<DirectoryInfo> Library = new("--library") {
		Description = "Folder of video files to search. Subfolders are traversed by default — see --no-recurse.",
		Required = true,
	};

	internal static readonly Option<bool> NoRecurse = new("--no-recurse") {
		Description = "Search only the top level of --library instead of traversing its subfolders.",
	};

	internal static readonly Option<DirectoryInfo> DumpFrames = new("--dump-frames") {
		Description = "Diagnostic (visual only): write every sampled frame as a PNG under this " +
			"folder — the reference clip's frames under clip/, each candidate's search window " +
			"under a numbered subfolder — to inspect exactly what the matcher compared. Frame " +
			"fNNN sits at NNN × --sample-interval into its extracted clip/window.",
	};
}
