// Bumper Remover — fine-grained VISUAL tail probe (not an upstream VDF test).
//
// For short/silent bumpers (e.g. Caprica's ~5s end-card: visually identical, audio varies or is
// silent), audio can't match and VDF's 5–15s dense sampling is too coarse. This probe embeds the
// clip AND only each episode's *tail* at a fine interval, then matches with VDF's dense-embedding
// matcher — validating the "fine-grained visual + positional window" approach cheaply (edges only).
//
// It computes embeddings itself (OnnxEmbedder) — no dependency on the scan embedding the clip.
// Model + ONNX runtime + (unused here) store are resolved by pointing the DB folder at the GUI's
// output dir, where the GUI downloaded them.
//
// Run (PowerShell) — the clip must be a VIDEO clip of the bumper (frames matter, not audio):
//   ffmpeg -sseof -5 -i "<a Caprica episode>" -c:v libx264 -an "D:\...\caprica-end5.mkv"
//   $env:BUMPER_DB_FOLDER="D:\Data\dev\git\videobumperremover\VDF.GUI\bin\Debug\net10.0"
//   $env:BUMPER_CLIP="D:\...\caprica-end5.mkv"
//   $env:BUMPER_EPISODES_DIR="<folder of Caprica episodes>"
//   $env:BUMPER_TAIL_SECONDS="30"      # optional (default 30); how much tail to sample
//   $env:BUMPER_CLIP_INTERVAL="1"      # optional (default 1s); fine sampling interval
//   dotnet test VDF.IntegrationTests --filter "FullyQualifiedName~VisualTailProbe" -l "console;verbosity=detailed"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using VDF.Core;
using VDF.Core.AI;
using VDF.Core.FFTools;
using VDF.Core.Utils;
using Xunit;
using Xunit.Abstractions;

namespace VDF.IntegrationTests.Comparison;

public class VisualTailProbe {
	readonly ITestOutputHelper _out;
	public VisualTailProbe(ITestOutputHelper output) => _out = output;

	static readonly string[] VideoExts = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".webm", ".wmv" };

	[SkippableFact]
	public void Probe_VisualTail() {
		string? dbFolder = Environment.GetEnvironmentVariable("BUMPER_DB_FOLDER");
		string? clipPath = Environment.GetEnvironmentVariable("BUMPER_CLIP");
		string? clipEpisode = Environment.GetEnvironmentVariable("BUMPER_CLIP_EPISODE");
		int clipSeconds = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_CLIP_SECONDS"), out var cs) && cs > 0 ? cs : 8;
		string? episodesDir = Environment.GetEnvironmentVariable("BUMPER_EPISODES_DIR");
		bool haveClip = !string.IsNullOrWhiteSpace(clipPath) || !string.IsNullOrWhiteSpace(clipEpisode);
		Skip.If(string.IsNullOrWhiteSpace(dbFolder) || !haveClip || string.IsNullOrWhiteSpace(episodesDir),
			"Set BUMPER_DB_FOLDER, BUMPER_EPISODES_DIR, and either BUMPER_CLIP (a clip file) or BUMPER_CLIP_EPISODE (auto-cut last BUMPER_CLIP_SECONDS from it).");
		Skip.If(!Directory.Exists(dbFolder), $"DB folder not found: {dbFolder}");
		Skip.If(!string.IsNullOrWhiteSpace(clipPath) && !File.Exists(clipPath), $"Clip not found: {clipPath}");
		Skip.If(!string.IsNullOrWhiteSpace(clipEpisode) && !File.Exists(clipEpisode), $"Clip episode not found: {clipEpisode}");
		Skip.If(!Directory.Exists(episodesDir), $"Episodes dir not found: {episodesDir}");
		Skip.If(string.IsNullOrEmpty(FfmpegEngine.FFmpegPath) || !File.Exists(FfmpegEngine.FFmpegPath),
			"ffmpeg executable not found (needs it on PATH).");

		double interval = double.TryParse(Environment.GetEnvironmentVariable("BUMPER_CLIP_INTERVAL"), out var iv) && iv > 0 ? iv : 1.0;
		int tailSec = int.TryParse(Environment.GetEnvironmentVariable("BUMPER_TAIL_SECONDS"), out var ts) && ts > 0 ? ts : 30;
		float hit = float.TryParse(Environment.GetEnvironmentVariable("BUMPER_HIT_PERCENT"), out var hp) && hp > 1 ? hp / 100f : 0.89f;

		var log = new StringBuilder();
		void Line(string s) { _out.WriteLine(s); log.AppendLine(s); }
		string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
		Line($"Visual TAIL probe {stamp}  (clip interval {interval}s, tail {tailSec}s, hit {hit:P0})");

		// The DINOv2 model file the GUI downloaded (default under <dbFolder>/ai). The ONNX *runtime*
		// comes from this test project's own OnnxRuntime native package, so we only need the model:
		// AiComponents.TestOverrideModelPath points ModelPath at it and bypasses the download check.
		string modelPath = Environment.GetEnvironmentVariable("BUMPER_AI_MODEL")
			?? Path.Combine(dbFolder!, "ai", AiComponents.ModelFileName);
		Skip.If(!File.Exists(modelPath),
			$"DINOv2 model not found at '{modelPath}'. Set BUMPER_AI_MODEL to the {AiComponents.ModelFileName} the GUI downloaded (search the GUI's ai/ folder).");

		string? prevModel = AiComponents.TestOverrideModelPath;
		var temps = new List<string>();
		try {
			AiComponents.TestOverrideModelPath = modelPath;

			// If no clip file was given, cut the clip from a reference episode's last N seconds
			// (reliable — the same extraction as the tails; ends hand-editing precision problems).
			if (string.IsNullOrWhiteSpace(clipPath)) {
				string clipTemp = Path.Combine(Path.GetTempPath(), $"vbr_clip_{Guid.NewGuid():N}.mkv");
				temps.Add(clipTemp);
				Skip.If(!ExtractTail(clipEpisode!, clipSeconds, clipTemp), $"Failed to extract last {clipSeconds}s from {clipEpisode}.");
				clipPath = clipTemp;
				Line($"Clip = last {clipSeconds}s of {Path.GetFileName(clipEpisode)} (auto-extracted).");
			}

			OnnxEmbedder embedder;
			try { embedder = new OnnxEmbedder(AiComponents.ModelPath); }
			catch (Exception e) { Skip.If(true, $"OnnxEmbedder init failed (model '{modelPath}'; runtime from test package): {e.Message}"); return; }

			using (embedder) {
				DenseEmbeddingStore.DenseRecord Embed(string path) {
					byte[][]? frames = FfmpegEngine.GetDenseAiFrames(path, interval, 400, false, default);
					if (frames is null || frames.Length == 0)
						return new DenseEmbeddingStore.DenseRecord(0, 0, (float)interval, Array.Empty<byte[]>());
					var emb = new byte[frames.Length][];
					var batch = new List<byte[]>(OnnxEmbedder.MaxBatch);
					var slots = new List<int>(OnnxEmbedder.MaxBatch);
					void Flush() {
						if (batch.Count == 0) return;
						byte[][] v = embedder.EmbedBatchQuantized(batch);
						for (int k = 0; k < v.Length; k++) emb[slots[k]] = v[k];
						batch.Clear(); slots.Clear();
					}
					for (int f = 0; f < frames.Length; f++) {
						emb[f] = Array.Empty<byte>();
						if (frames[f] is null || frames[f].Length == 0) continue;
						batch.Add(frames[f]); slots.Add(f);
						if (batch.Count == OnnxEmbedder.MaxBatch) Flush();
					}
					Flush();
					return new DenseEmbeddingStore.DenseRecord(0, 0, (float)interval, emb);
				}

				var clipRec = Embed(clipPath!);
				int clipN = clipRec.Frames.Count(f => f.Length > 0);
				Line($"CLIP: {Path.GetFileName(clipPath)}  {clipN} usable / {clipRec.Frames.Length} sampled frames @ {interval}s (≈{clipRec.Frames.Length * interval:0.0}s span)");
				Skip.If(clipN < 1, "Clip produced no usable frames. Lower BUMPER_CLIP_INTERVAL or check the clip.");
				Line(new string('-', 78));

				var episodes = Directory.EnumerateFiles(episodesDir!)
					.Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
					.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipPath!), StringComparison.OrdinalIgnoreCase))
						.Where(f => string.IsNullOrWhiteSpace(clipEpisode) || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(clipEpisode), StringComparison.OrdinalIgnoreCase))
					.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
					.ToList();
				Skip.If(episodes.Count == 0, "No episode files found.");

				// Presence threshold: a clip frame is "present" if its best cosine to any tail frame
				// clears this. For short/static bumpers we don't need ≥4 temporally-consistent hits
				// (VDF's rigid rule) — one distinctive frame found at high cosine IS the detection.
				float presence = float.TryParse(Environment.GetEnvironmentVariable("BUMPER_PRESENCE"), out var pv) && pv > 0
					? (pv > 1 ? pv / 100f : pv) : 0.90f;
				Line($"Columns: bestCos@tailTime  present=<clip frames matched>/<clip frames>  [rigid ≥4-hit matcher]  (presence thr {presence:P0})");
				Line(new string('-', 78));

				int present = 0;
				foreach (var ep in episodes) {
					string temp = Path.Combine(Path.GetTempPath(), $"vbr_tail_{Guid.NewGuid():N}.mkv");
					temps.Add(temp);
					if (!ExtractTail(ep, tailSec, temp)) {
						Line($"{Path.GetFileName(ep),-46}  (tail extract failed)");
						continue;
					}
					var epRec = Embed(temp);

					// Presence matcher (ours): best per-frame cosine + how many clip frames appear.
					float best = 0f; int bestTf = -1, hits = 0;
					for (int c = 0; c < clipRec.Frames.Length; c++) {
						if (clipRec.Frames[c].Length == 0) continue;
						float cBest = 0f;
						for (int s = 0; s < epRec.Frames.Length; s++) {
							if (epRec.Frames[s].Length == 0) continue;
							float cos = EmbeddingMath.CosineSimilarity(clipRec.Frames[c], epRec.Frames[s]);
							if (cos > cBest) cBest = cos;
							if (cos > best) { best = cos; bestTf = s; }
						}
						if (cBest >= presence) hits++;
					}
					int epUsable = epRec.Frames.Count(f => f.Length > 0);
					double bestTime = bestTf < 0 ? -1 : bestTf * interval;
					if (hits >= 1) present++;

					// Rigid matcher (VDF) for comparison.
					bool ok = ScanEngine.TryMatchDenseFrames(epRec, clipRec, hit, out float sim, out int off);
					string rigid = ok ? $"rigid {sim:P0}@{off}s" : "rigid:no";
					Line($"{Path.GetFileName(ep),-46}  bestCos={best,6:P1}@{bestTime,5:0.0}s  present={hits}/{clipN}  tail={epUsable}/{epRec.Frames.Length}  [{rigid}]");
				}
				Line(new string('-', 78));
				Line($"{present}/{episodes.Count} episodes have ≥1 clip frame present (≥{presence:P0} cosine) in the last {tailSec}s.");

				string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clipPath!))!, $"visual-tail-results-{stamp}.txt");
				try { File.WriteAllText(outPath, log.ToString()); _out.WriteLine($"\nWrote results to: {outPath}"); }
				catch (Exception e) { _out.WriteLine($"(could not write results file: {e.Message})"); }
			}
		}
		finally {
			AiComponents.TestOverrideModelPath = prevModel;
			foreach (var t in temps) { try { if (File.Exists(t)) File.Delete(t); } catch { } }
		}
	}

	// Extract the last <tailSec> seconds to a temp file via the ffmpeg CLI (stream-copy).
	static bool ExtractTail(string ep, int tailSec, string temp) {
		var psi = new ProcessStartInfo {
			FileName = FfmpegEngine.FFmpegPath,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-y");
		psi.ArgumentList.Add("-sseof");
		psi.ArgumentList.Add($"-{tailSec}");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(ep);
		psi.ArgumentList.Add("-c");
		psi.ArgumentList.Add("copy");
		psi.ArgumentList.Add(temp);
		try {
			using var p = Process.Start(psi)!;
			p.StandardError.ReadToEnd();
			p.StandardOutput.ReadToEnd();
			p.WaitForExit(60000);
			return p.HasExited && p.ExitCode == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0;
		}
		catch { return false; }
	}
}
