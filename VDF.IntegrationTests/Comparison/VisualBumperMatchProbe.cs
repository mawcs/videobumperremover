// Bumper Remover — visual (DINOv2) diagnostic probe (not an upstream VDF test).
//
// The audio matcher collapses for short bumpers (see BumperMatchProbe / vdf-evaluation.md).
// This probe tests the VISUAL signal: VDF's dense keyframe-embedding matcher
// (ScanEngine.TryMatchDenseFrames), which matches a *sequence* of DINOv2 frame embeddings
// at a consistent time offset — robust to resolution/letterbox, and well suited to motion
// bumpers. It bypasses the AI-partial pass's gates (min-hits grouping, already-grouped
// exclusion) and reports similarity + offset per episode.
//
// It does NOT compute embeddings itself — it READS the ones a scan already cached. So first:
//   1. In the GUI, enable Settings → Partial clips → "Detect partial duplicates visually (AI)"
//      and scan the folder that holds the clip + episodes (this fills DenseEmbeddings.db).
//   2. Locate that DenseEmbeddings.db (next to ScannedFiles.db):
//        Get-ChildItem D:\Data\dev\git\videobumperremover -Recurse -Filter DenseEmbeddings.db
//   3. Run:
//        $env:BUMPER_DENSE_DB="...\DenseEmbeddings.db"
//        $env:BUMPER_CLIP="...\introclip.mkv"          # use the 40s clip first (see note)
//        $env:BUMPER_EPISODES_DIR="...\Season 01"
//        dotnet test VDF.IntegrationTests --filter "FullyQualifiedName~VisualBumperMatchProbe" -l "console;verbosity=detailed"
//
// NOTE on short clips: VDF samples dense frames every 5–15s, so a 3–5s clip yields ~1 frame —
// far below the ≥4 consistent hits the matcher needs. Validate the matcher with the ~40s clip
// first; testing genuinely short bumpers visually needs finer sampling (a follow-up: lower the
// AI-partial interval, or the standalone-extraction probe).

using System;
using System.IO;
using System.Linq;
using System.Text;
using VDF.Core;
using VDF.Core.AI;
using Xunit;
using Xunit.Abstractions;

namespace VDF.IntegrationTests.Comparison;

public class VisualBumperMatchProbe {
	readonly ITestOutputHelper _out;
	public VisualBumperMatchProbe(ITestOutputHelper output) => _out = output;

	// VDF's default "AI frame hit threshold" (per-frame cosine to count as a hit).
	const float HitThreshold = 0.89f;
	static readonly string[] VideoExts = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".webm", ".wmv" };

	[SkippableFact]
	public void Probe_VisualClipAgainstEpisodes() {
		string? denseDb = Environment.GetEnvironmentVariable("BUMPER_DENSE_DB");
		string? clipPath = Environment.GetEnvironmentVariable("BUMPER_CLIP");
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		Skip.If(string.IsNullOrWhiteSpace(denseDb) || string.IsNullOrWhiteSpace(clipPath) || string.IsNullOrWhiteSpace(episodesDir),
			"Set BUMPER_DENSE_DB, BUMPER_CLIP and BUMPER_EPISODES_DIR (run an AI visual-partial scan first).");
		Skip.If(!File.Exists(denseDb), $"DenseEmbeddings.db not found: {denseDb}");
		Skip.If(!File.Exists(clipPath), $"Clip not found: {clipPath}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");

		var log = new StringBuilder();
		void Line(string s) { _out.WriteLine(s); log.AppendLine(s); }
		string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
		Line($"Visual probe run {stamp}  (hit threshold {HitThreshold:P0})");

		DenseEmbeddingStore.DenseRecord? Lookup(DenseEmbeddingStore store, string path) {
			var info = new FileInfo(path);
			return store.TryGet(path, info.Length, info.LastWriteTimeUtc.Ticks, out var rec) ? rec : null;
		}

		string? prevOverride = DenseEmbeddingStore.TestOverrideStorePath;
		try {
			DenseEmbeddingStore.TestOverrideStorePath = denseDb;
			var store = DenseEmbeddingStore.Load();

			var clipRec = Lookup(store, clipPath!);
			Skip.If(clipRec is null,
				"Clip has no cached dense embeddings — did the AI visual-partial scan include this exact file (same size/mtime)?");
			Line($"CLIP: {Path.GetFileName(clipPath)}  ({clipRec!.Frames.Count(f => f.Length > 0)} usable frames @ {clipRec.IntervalSeconds}s interval)");
			if (clipRec.Frames.Count(f => f.Length > 0) < 4)
				Line("  WARNING: fewer than 4 usable clip frames — the matcher needs ≥4 consistent hits, so short clips will not match here (sampling too coarse).");
			Line(new string('-', 78));

			var episodes = Directory.EnumerateFiles(episodesDir!)
				.Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
				.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipPath!), StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();
			Skip.If(episodes.Count == 0, "No episode files found in BUMPER_EPISODES_DIR.");

			int matched = 0;
			foreach (var ep in episodes) {
				var epRec = Lookup(store, ep);
				if (epRec is null) {
					Line($"{Path.GetFileName(ep),-58}  (no cached embeddings — not in the scan?)");
					continue;
				}
				bool hit = ScanEngine.TryMatchDenseFrames(epRec, clipRec, HitThreshold, out float sim, out int offsetSec);
				if (hit) {
					matched++;
					Line($"{Path.GetFileName(ep),-58}  MATCH  sim={sim,7:P1}  offset≈{offsetSec,5}s");
				}
				else {
					Line($"{Path.GetFileName(ep),-58}  no match (< 4 consistent hits)");
				}
			}

			Line(new string('-', 78));
			Line($"{matched}/{episodes.Count} episodes matched the clip visually (≥4 hits agreeing on one offset).");

			string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clipPath!))!, $"visual-probe-results-{stamp}.txt");
			try { File.WriteAllText(outPath, log.ToString()); _out.WriteLine($"\nWrote results to: {outPath}"); }
			catch (Exception e) { _out.WriteLine($"(could not write results file: {e.Message})"); }
		}
		finally {
			DenseEmbeddingStore.TestOverrideStorePath = prevOverride;
		}
	}
}
