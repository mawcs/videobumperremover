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
using System.Linq;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;
using VBR.Core.Matching;
using VDF.Core.Utils;

namespace VBR.CLI.Commands;

/// <summary>Which signal(s) a command runs. Lowercase members — see VBR.Core.Extraction.ClipEdge
/// for why. <c>both</c> (visual+audio) predates pHash and keeps its original meaning for backward
/// compatibility; <c>all</c> adds pHash alongside both. <c>phash</c> alone makes pHash the sole
/// decision-maker — genuinely alternate, not just corroboration, per the maintainer's direction —
/// but be aware it has so far underperformed badly as a standalone signal in real testing (see
/// --phash-presence-threshold's help text).</summary>
internal enum DetectionMode { visual, audio, phash, both, all }

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

	// Shared by --library and --exclude-folders: one flag, semicolon-delimited (decided over a
	// repeatable flag — docs/iterativeplan.md's "CLI terminology & multi-folder libraries" entry).
	// requireExists is false for --exclude-folders: excluding a folder that's currently offline
	// (a dismounted network share, say) should still work by path, not error.
	internal static DirectoryInfo[] ParseFolderListArg(ArgumentResult result, bool requireExists) {
		if (result.Tokens.Count == 0) return Array.Empty<DirectoryInfo>();
		var folders = new List<DirectoryInfo>();
		foreach (string piece in result.Tokens[0].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			var dir = new DirectoryInfo(piece);
			if (requireExists && !dir.Exists) {
				result.AddError($"Folder not found: {dir.FullName}");
				continue;
			}
			folders.Add(dir);
		}
		return folders.ToArray();
	}

	/// <summary>True if <paramref name="path"/> (a file or directory) falls under any of
	/// <paramref name="excludeFolders"/> — a path/folder rule, distinct from and independent of the
	/// '.vbr.'-output filename filter each command applies separately.</summary>
	internal static bool IsUnderAny(string path, IReadOnlyList<DirectoryInfo> excludeFolders) {
		if (excludeFolders.Count == 0) return false;
		string fullPath = Path.GetFullPath(path);
		foreach (DirectoryInfo exclude in excludeFolders) {
			string root = Path.GetFullPath(exclude.FullName)
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

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
		Description = "Visual/pHash: the dense interval — seconds between sampled frames nearest " +
			"the true edge (see --edge-boundary) — smaller is denser. Default 1s; short clips " +
			"(under ~8s) need it as low as ~0.2s to have enough frames to match on — no floor is " +
			"enforced, go as dense as needed.",
		DefaultValueFactory = _ => TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds),
		CustomParser = r => ParseDurationArg(r, TimeSpan.FromSeconds(VisualBumperMatcher.DefaultSampleIntervalSeconds)),
	};

	// No DefaultValueFactory for the same reason as SearchLength: the real default ("cover the
	// whole clip/search window", i.e. always dense, today's exact single-density behavior) depends
	// on --clip-length/--search-length, which aren't known yet when this Option is declared.
	// TimeSpan.Zero means "unset"; resolved to TimeSpan.MaxValue in each command's action, which
	// MixedDensitySampler.GatherFrames's own clamp (edgeBoundary > totalLength => totalLength)
	// turns into "the entire window is dense" for whatever totalLength that particular call uses
	// (clip-length for the reference clip, search-length for each candidate) — no separate
	// single-density code path needed.
	internal static readonly Option<TimeSpan> EdgeBoundary = new("--edge-boundary") {
		Description = "Visual/pHash: how far from the true edge the dense zone extends; sampled " +
			"sparser beyond it (see --sparse-interval). Default: the whole clip/search window is " +
			"dense (today's single-density behavior) — set this smaller than --clip-length for a " +
			"bumper long enough to need mixed-density sampling (e.g. a 47s intro with a 20s " +
			"edge-boundary).",
		CustomParser = r => r.Tokens.Count == 0 ? TimeSpan.Zero : ParseDurationArg(r, TimeSpan.Zero),
	};

	internal static readonly Option<TimeSpan> SparseInterval = new("--sparse-interval") {
		Description = "Visual/pHash: sampling interval beyond --edge-boundary. Default: same as " +
			"--sample-interval (irrelevant unless --edge-boundary is set smaller than " +
			"--clip-length/--search-length, since the sparse zone never activates otherwise).",
		CustomParser = r => r.Tokens.Count == 0 ? TimeSpan.Zero : ParseDurationArg(r, TimeSpan.Zero),
	};

	internal static readonly Option<float> PresenceThreshold = new("--presence-threshold") {
		Description = "Visual: cosine similarity (0-1) at or above which a clip frame counts as present in a candidate.",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultPresenceThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultPresenceThreshold),
	};

	internal static readonly Option<float> PHashPresenceThreshold = new("--phash-presence-threshold") {
		Description = "pHash: Hamming similarity (0-1) at or above which a clip frame counts as " +
			"present in a candidate. Default matches VDF's own pHash duplicate gate (96%). Note: " +
			"on real testing so far, pHash alone has a much narrower true/false-positive margin " +
			"than visual and has missed real matches visual caught — treat --detection-mode phash " +
			"as experimental, not a drop-in replacement for visual.",
		DefaultValueFactory = _ => VisualBumperMatcher.DefaultPHashPresenceThreshold,
		CustomParser = r => ParseInvariantFloat(r, VisualBumperMatcher.DefaultPHashPresenceThreshold),
	};

	internal static readonly Option<float> MinSimilarity = new("--min-similarity") {
		Description = "Audio: similarity (0-1) at or above which a file is flagged as a match.",
		DefaultValueFactory = _ => AudioBumperMatcher.DefaultMinSimilarity,
		CustomParser = r => ParseInvariantFloat(r, AudioBumperMatcher.DefaultMinSimilarity),
	};

	internal static readonly Option<DetectionMode> Mode = new("--detection-mode") {
		Description = "Which signal(s) to run (visual|audio|phash|both|all). 'both' runs visual " +
			"and audio (visual decides, audio corroborates) — the original two-signal meaning, " +
			"kept for compatibility. 'all' adds pHash alongside both (visual still decides when " +
			"it ran). 'phash' runs pHash alone as the sole decision-maker — see " +
			"--phash-presence-threshold's note before relying on it.",
		DefaultValueFactory = _ => DetectionMode.visual,
	};

	// Not Required: exactly one of Library/TargetFile must be given, validated in each command's
	// action (System.CommandLine has no built-in "exactly one of" constraint) via ResolveCandidates.
	internal static readonly Option<DirectoryInfo[]> Library = new("--library") {
		Description = "Semicolon-delimited folder(s) of video files to search (e.g. " +
			"\"D:\\Show;D:\\Extras\"). Subfolders are traversed by default — see --no-recurse. The " +
			"same folder may appear under more than one library (e.g. across separate " +
			"--library-db-folder/--library-name runs) — nothing here checks for or prevents that " +
			"overlap. Exactly one of --library or --file is required.",
		CustomParser = r => ParseFolderListArg(r, requireExists: true),
	};

	internal static readonly Option<DirectoryInfo[]> ExcludeFolders = new("--exclude-folders") {
		Description = "Semicolon-delimited folder(s) to exclude from --library's candidates (e.g. " +
			"\"D:\\Show\\Extras;D:\\Show\\Deleted Scenes\"). A file is excluded if its path falls " +
			"under any of these, regardless of which --library folder it was found under — a " +
			"path/folder rule, independent of the '.vbr.'-output filename filter each command " +
			"already applies on its own. No effect with --file.",
		CustomParser = r => ParseFolderListArg(r, requireExists: false),
	};

	internal static readonly Option<FileInfo> TargetFile = new("--file") {
		Description = "A single video file to search, instead of a folder. Exactly one of " +
			"--library or --file is required.",
	};

	internal static readonly Option<bool> NoRecurse = new("--no-recurse") {
		Description = "With --library: search only its top level instead of traversing subfolders. No effect with --file.",
	};

	// Shared by `scan` and `add-bumper` — both name a per-library file (a database, a catalog) after
	// this, defaulting from --library's own folder name. Promoted here (was scan-only originally)
	// once add-bumper needed the identical option, per this file's own stated purpose: one shared
	// definition rather than two copies drifting apart.
	internal static readonly Option<string> LibraryName = new("--library-name") {
		Description = "Label for this library — also names its default per-library file (database, " +
			"catalog, etc.). Default: --library's own folder name (the first folder, if --library " +
			"names more than one).",
	};

	internal static readonly Option<DirectoryInfo> DumpFrames = new("--dump-frames") {
		Description = "Diagnostic (visual only): write every sampled frame as a PNG under this " +
			"folder — the reference clip's frames under clip/, each candidate's search window " +
			"under a numbered subfolder — to inspect exactly what the matcher compared. Frame " +
			"fNNN sits at NNN × --sample-interval into its extracted clip/window.",
	};

	internal static readonly Option<bool> Verbose = new("--verbose") {
		Description = "Log detailed diagnostic info (model path, per-file frame/embedding " +
			"counts, exact ffmpeg commands run) to the console and to VDF's log.txt — proof of " +
			"what's actually happening on a given run, not just the summary line.",
	};

	// commit-only, but defined here alongside Verbose/TargetFile/Library rather than in
	// CommitCommand — SharedOptions is this project's one place option definitions live,
	// per its own doc comment above, even though this one option isn't shared with match/remove.
	internal static readonly Option<bool> ValidateFiles = new("--validate-files") {
		Description = "Before promoting each '.vbr.' output, ffprobe it and sanity-check its " +
			"duration (against the manifest when present, or against the original's own probed " +
			"duration otherwise). A file that fails is left alone and reported broken — the " +
			"original is never touched. Off by default: the CLI can't enforce that a human " +
			"reviewed the output, so this is an assist, not a substitute (ADR 0008).",
	};

	/// <summary>Resolved set of candidate files plus the root(s) to print paths relative to (empty
	/// for a single-file target, where the display name is just the file name).</summary>
	internal readonly record struct CandidateSet(IReadOnlyList<string> Files, IReadOnlyList<string> LibraryRoots);

	/// <summary>
	/// Validates exactly one of <paramref name="file"/>/<paramref name="libraries"/> was given and
	/// resolves it to a candidate list — a single file as-is (no extension filtering: the user
	/// named it explicitly), or every recognized video file under every library folder, minus
	/// anything under <paramref name="excludeFolders"/>, deduplicated (the same physical file can
	/// otherwise appear twice if two given library folders overlap, e.g. one nested in the other).
	/// Returns null and sets <paramref name="error"/> on any validation failure; callers print the
	/// error and exit nonzero. Each library folder's own existence was already validated by
	/// <see cref="Library"/>'s own parser (a nonexistent folder can't make it into this list at
	/// all), so there's no existence re-check here.
	/// </summary>
	internal static CandidateSet? ResolveCandidates(FileInfo? file, IReadOnlyList<DirectoryInfo> libraries,
			IReadOnlyList<DirectoryInfo> excludeFolders, bool recurse, out string? error) {
		if (file is null && libraries.Count == 0) {
			error = "Error: one of --library or --file is required.";
			return null;
		}
		if (file is not null && libraries.Count > 0) {
			error = "Error: specify only one of --library or --file, not both.";
			return null;
		}
		if (file is not null) {
			if (!file.Exists) {
				error = $"Error: File not found: {file.FullName}";
				return null;
			}
			error = null;
			return new CandidateSet(new[] { file.FullName }, LibraryRoots: Array.Empty<string>());
		}
		error = null;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new List<string>();
		foreach (DirectoryInfo library in libraries) {
			foreach (string f in Directory.EnumerateFiles(library.FullName, "*",
					new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true })) {
				if (!ClipExtractor.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
				if (IsUnderAny(f, excludeFolders)) continue;
				if (seen.Add(Path.GetFullPath(f)))
					files.Add(f);
			}
		}
		files.Sort(StringComparer.OrdinalIgnoreCase);
		return new CandidateSet(files, libraries.Select(l => l.FullName).ToList());
	}

	/// <summary>Library-relative path for a resolved candidate — relative to whichever of
	/// <paramref name="libraryRoots"/> actually contains it — or just the file name for a
	/// single-file target (empty <paramref name="libraryRoots"/>, no root to be relative to).</summary>
	internal static string DisplayName(string file, IReadOnlyList<string> libraryRoots) {
		if (libraryRoots.Count == 0) return Path.GetFileName(file);
		string fullFile = Path.GetFullPath(file);
		foreach (string root in libraryRoots) {
			string normalizedRoot = Path.GetFullPath(root);
			if (fullFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
				return Path.GetRelativePath(normalizedRoot, file);
		}
		// Every candidate came from one of these roots, so this shouldn't happen -- fall back to
		// the full path rather than throw, since this is display-only.
		return file;
	}

	/// <summary>
	/// When <paramref name="verbose"/>, echoes every log entry VBR.Core/VDF.Core raise via
	/// <see cref="Logger"/> to stderr as they happen — Info/Warn/Error all always get written to
	/// VDF's own log.txt regardless of this flag (VBR.Core logs unconditionally; there's nothing
	/// to gate there), so --verbose only controls whether the CLI *also* echoes them live. Returns
	/// an <see cref="IDisposable"/> that unsubscribes; dispose it when the command finishes so a
	/// stale handler doesn't outlive the process (matters most for the CLI's own test runs).
	/// </summary>
	internal static IDisposable? SubscribeVerboseLogging(bool verbose) =>
		SubscribeLogging(verbose, line => Console.Error.WriteLine(line));

	/// <summary>Same mechanism as <see cref="SubscribeVerboseLogging"/>, generalized to any
	/// destination (e.g. <c>vbr scan</c>'s <c>--log-file</c>, which needs its own independently
	/// leveled echo of the same <see cref="Logger"/> events, separate from the console's).</summary>
	internal static IDisposable? SubscribeLogging(bool active, Action<string> writeLine) {
		if (!active) return null;
		Logger.LogEventHandler handler = entry => {
			if (entry.IsSessionStart) return;
			string tag = entry.Severity switch {
				LogSeverity.Warning => "WARN ",
				LogSeverity.Error => "ERROR",
				_ => "INFO ",
			};
			writeLine($"[{entry.Timestamp:HH:mm:ss} {tag}] {entry.Message}");
		};
		Logger.Instance.LogEntryAdded += handler;
		return new Unsubscriber(() => Logger.Instance.LogEntryAdded -= handler);
	}

	sealed class Unsubscriber(Action unsubscribe) : IDisposable {
		public void Dispose() => unsubscribe();
	}
}
