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

// Pure filesystem logic (mark/promote/delete/recovery) needs no video content at all, so most of
// these run as ordinary, always-on unit tests against temp directories with plain dummy files —
// unlike ClipRemoverTests, nothing here is env-var-gated. The two --validate-files tests are the
// exception: they shell out to ffmpeg/ffprobe on PATH (a hard dependency of this whole project,
// same as ClipRemoverTests.ProbeDuration below), not a curated real-media library.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VBR.Core.Cleanup;
using VBR.Core.Removal;

namespace VBR.Tests.Cleanup;

public class LibraryCleanerTests {
	[Theory]
	[InlineData(@"D:\Media\S01E01.vbr.mkv", @"D:\Media\S01E01.mkv")]
	[InlineData(@"D:\Media\Some.Show.2020.vbr.mp4", @"D:\Media\Some.Show.2020.mp4")]
	[InlineData(@"D:\Media\no-extension.vbr", @"D:\Media\no-extension")]
	public void OriginalPathFor_InvertsBuildOutputPath(string vbrPath, string expectedOriginal) {
		Assert.Equal(expectedOriginal, LibraryCleaner.OriginalPathFor(vbrPath));
	}

	[Theory]
	[InlineData(@"D:\Media\S01E01.mkv")]           // plain original, no .vbr. infix at all
	[InlineData(@"D:\Media\Some.Show.2020.mp4")]    // same
	[InlineData(@"D:\Media\S01E01.vbr.json")]       // the manifest sitting next to a real output —
													 // must NOT be mistaken for one itself
	public void OriginalPathFor_ReturnsNullForNonVbrPaths(string path) {
		Assert.Null(LibraryCleaner.OriginalPathFor(path));
	}

	[Theory]
	[InlineData(@"D:\Media\S01E01.mkv")]
	[InlineData(@"D:\Media\Some.Show.2020.mp4")]
	[InlineData(@"D:\Media\no-extension")]
	public void OriginalPathFor_RoundTripsWithBuildOutputPath(string original) {
		Assert.Equal(original, LibraryCleaner.OriginalPathFor(ClipRemover.BuildOutputPath(original)));
	}

	[Fact]
	public void CleanDirectory_PromotesOutputAndDeletesOriginalAndManifest() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			string manifest = Path.ChangeExtension(original, ".json"); // named after the original, not the .vbr. output
			File.WriteAllText(original, "OLD");
			File.WriteAllText(vbr, "NEW");
			File.WriteAllText(manifest, "placeholder manifest");

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			Assert.Empty(result.RecoveryActions);
			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Cleaned, row.Outcome);
			Assert.Null(row.Detail);

			Assert.True(File.Exists(original));
			Assert.Equal("NEW", File.ReadAllText(original));
			Assert.False(File.Exists(vbr));
			Assert.False(File.Exists(manifest));
			Assert.False(File.Exists(original + LibraryCleaner.DeletionMarkerSuffix));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_LeavesFilesWithNoVbrCounterpartUntouched() {
		string dir = CreateTempDir();
		try {
			string solo = Path.Combine(dir, "solo.mkv");
			File.WriteAllText(solo, "SOLO");

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			Assert.Empty(result.Results);
			Assert.Empty(result.RecoveryActions);
			Assert.Equal("SOLO", File.ReadAllText(solo));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_ReportsBroken_WhenNoMatchingOriginalExists() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "orphan.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			File.WriteAllText(vbr, "NEW");
			// original deliberately never created.

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Broken, row.Outcome);
			Assert.Contains("no matching original", row.Detail, StringComparison.OrdinalIgnoreCase);
			Assert.True(File.Exists(vbr), "The .vbr. file must be left alone when there's nothing safe to promote it onto.");
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_RollsBackMark_WhenPromoteFails() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			string marked = original + LibraryCleaner.DeletionMarkerSuffix;
			File.WriteAllText(original, "OLD");
			File.WriteAllText(vbr, "NEW");

			CleanupRunResult result;
			// Holding the .vbr. file open (without FileShare.Delete) makes the promote rename
			// fail with a genuine Windows sharing violation — the same real-world failure mode
			// (a player/indexer/AV scanner has the file open) ADR 0008 designed rollback around,
			// not a mock.
			using (new FileStream(vbr, FileMode.Open, FileAccess.Read, FileShare.Read))
				result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Broken, row.Outcome);
			Assert.Contains("rolled back", row.Detail, StringComparison.OrdinalIgnoreCase);

			Assert.True(File.Exists(original), "Rollback must restore the original.");
			Assert.Equal("OLD", File.ReadAllText(original));
			Assert.False(File.Exists(marked), "The marker must not linger after a successful rollback.");
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_RecoverySweep_RetriesDelete_WhenPromotionAlreadyCompleted() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string marked = original + LibraryCleaner.DeletionMarkerSuffix;
			// Simulates a run interrupted after promote (step c) but before the old original's
			// delete (step e): the .vbr. file is already gone (promoted), only the marker remains.
			File.WriteAllText(original, "NEW");
			File.WriteAllText(marked, "OLD");

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			RecoveryAction recovery = Assert.Single(result.RecoveryActions);
			Assert.Contains("already completed", recovery.Detail, StringComparison.OrdinalIgnoreCase);
			Assert.Empty(result.Results); // nothing left for the main pass — no .vbr. file exists anymore.

			Assert.True(File.Exists(original));
			Assert.Equal("NEW", File.ReadAllText(original));
			Assert.False(File.Exists(marked));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_RecoverySweep_RestoresAndReprocesses_WhenInterruptedBeforePromotion() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			string marked = original + LibraryCleaner.DeletionMarkerSuffix;
			// Simulates a run interrupted between mark (step b) and promote (step c): the
			// original is gone (marked), the .vbr. file was never touched.
			File.WriteAllText(marked, "OLD");
			File.WriteAllText(vbr, "NEW");

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: false, verbose: false);

			RecoveryAction recovery = Assert.Single(result.RecoveryActions);
			Assert.Contains("restored", recovery.Detail, StringComparison.OrdinalIgnoreCase);

			// The restored pair must be picked up as an ordinary candidate by the same run's
			// main pass — not left half-recovered until a third invocation.
			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Cleaned, row.Outcome);

			Assert.True(File.Exists(original));
			Assert.Equal("NEW", File.ReadAllText(original));
			Assert.False(File.Exists(vbr));
			Assert.False(File.Exists(marked));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanSingleFile_OnlyTouchesTargetFile_IgnoresSiblingVbrFiles() {
		string dir = CreateTempDir();
		try {
			string targetOriginal = Path.Combine(dir, "target.mkv");
			string targetVbr = ClipRemover.BuildOutputPath(targetOriginal);
			string siblingOriginal = Path.Combine(dir, "sibling.mkv");
			string siblingVbr = ClipRemover.BuildOutputPath(siblingOriginal);
			File.WriteAllText(targetOriginal, "TARGET_OLD");
			File.WriteAllText(targetVbr, "TARGET_NEW");
			File.WriteAllText(siblingOriginal, "SIBLING_OLD");
			File.WriteAllText(siblingVbr, "SIBLING_NEW");

			(CleanupFileResult result, RecoveryAction? recovery) = LibraryCleaner.CleanSingleFile(targetOriginal, validateFiles: false, verbose: false);

			Assert.Null(recovery);
			Assert.Equal(CleanupOutcome.Cleaned, result.Outcome);
			Assert.Equal("TARGET_NEW", File.ReadAllText(targetOriginal));
			Assert.False(File.Exists(targetVbr));

			// The sibling pair must be completely untouched — this is the whole point of
			// Decision 10 (--file never sweeps the rest of its directory).
			Assert.Equal("SIBLING_OLD", File.ReadAllText(siblingOriginal));
			Assert.Equal("SIBLING_NEW", File.ReadAllText(siblingVbr));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_WithValidateFiles_RejectsUnprobableOutput_WithoutTouchingOriginal() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			File.WriteAllText(original, "OLD");
			File.WriteAllText(vbr, "this is not a real video file");

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: true, verbose: false);

			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Broken, row.Outcome);
			Assert.Contains("probe", row.Detail, StringComparison.OrdinalIgnoreCase);

			Assert.Equal("OLD", File.ReadAllText(original));
			Assert.True(File.Exists(vbr));
			Assert.False(File.Exists(original + LibraryCleaner.DeletionMarkerSuffix));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void CleanDirectory_WithValidateFiles_AcceptsAndCleans_WhenManifestDurationMatches() {
		string dir = CreateTempDir();
		try {
			string original = Path.Combine(dir, "show.mkv");
			string vbr = ClipRemover.BuildOutputPath(original);
			string manifest = Path.ChangeExtension(original, ".json"); // named after the original, not the .vbr. output
			File.WriteAllText(original, "OLD"); // never probed when a manifest is present — content doesn't matter.
			GenerateSyntheticVideo(vbr, durationSeconds: 2.0);

			var entry = new RemovalManifestEntry(
				Tool: "vbr remove",
				TimestampUtc: DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				Source: original,
				Output: vbr,
				Region: "end",
				BumperLengthSeconds: 2.0,
				SourceDurationSeconds: 4.0,
				CutPointSeconds: 2.0, // end region: expected kept duration == cut point == ~2s, matching the synthetic clip.
				Mode: "StreamCopy",
				MatchDetail: null);
			File.WriteAllText(manifest, JsonSerializer.Serialize(entry, RemovalJsonContext.Default.RemovalManifestEntry));

			CleanupRunResult result = LibraryCleaner.CleanDirectory(dir, validateFiles: true, verbose: false);

			CleanupFileResult row = Assert.Single(result.Results);
			Assert.Equal(CleanupOutcome.Cleaned, row.Outcome);
			Assert.True(File.Exists(original));
			Assert.False(File.Exists(vbr));
			Assert.False(File.Exists(manifest));
		}
		finally { DeleteTempDir(dir); }
	}

	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_cleanup_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	// Same rationale as ClipRemoverTests.ProbeDuration: VBR.Tests has no InternalsVisibleTo from
	// VDF.Core, so this shells out to ffmpeg on PATH directly rather than VDF.Core.FFTools.
	static void GenerateSyntheticVideo(string path, double durationSeconds) {
		var psi = new ProcessStartInfo {
			FileName = "ffmpeg",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
		psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(FormattableString.Invariant($"color=c=black:s=64x64:d={durationSeconds}:r=10"));
		psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
		psi.ArgumentList.Add(path);
		using var process = Process.Start(psi)!;
		// Must drain both streams concurrently, not sequentially: ffmpeg writes its progress/log
		// output to stderr, which is large enough to fill the OS pipe buffer — reading stdout to
		// completion first (which never closes until the process exits) while nothing drains the
		// now-full stderr pipe deadlocks ffmpeg against this process. Started before WaitForExit.
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		bool exited = process.WaitForExit(30_000);
		if (!exited) {
			try { process.Kill(); } catch { }
			throw new InvalidOperationException("ffmpeg timed out generating a synthetic test video.");
		}
		Task.WaitAll(stdoutTask, stderrTask);
		if (process.ExitCode != 0 || !File.Exists(path))
			throw new InvalidOperationException($"Failed to generate a synthetic test video via ffmpeg — is it on PATH? stderr: {stderrTask.Result}");
	}
}
