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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VBR.Core.Configuration;

/// <summary>
/// Discovers, parses, and validates <c>vbr.config.json</c>, then activates the result as
/// <see cref="VbrConfig.Current"/> (docs/iterativeplan.md, "File-path DB options" entry, Part 3).
/// Precedence: built-in default (<see cref="VbrConfig.Default"/>) &lt; config file &lt; explicit CLI
/// flag — this loader only ever produces the *middle* tier; a CLI option's own
/// <c>DefaultValueFactory</c> reading <see cref="VbrConfig.Current"/> is what lets an explicit flag
/// still win (System.CommandLine never calls a <c>DefaultValueFactory</c> for an option the user
/// actually supplied a value for).
/// </summary>
public static class VbrConfigLoader {
	public const string ConfigFileName = "vbr.config.json";

	/// <summary>Discovers a config file (current directory wins over VBR's state root; neither
	/// present means pure built-in defaults), parses and validates it, and sets
	/// <see cref="VbrConfig.Current"/> to the result. Call exactly once, at process startup, before
	/// any command parses its options — every <c>DefaultValueFactory</c> that reads
	/// <see cref="VbrConfig.Current"/> needs this to have already run.</summary>
	/// <returns>The path actually loaded from, or null if neither location had a config file (in
	/// which case <see cref="VbrConfig.Current"/> is left at <see cref="VbrConfig.Default"/>).</returns>
	/// <exception cref="InvalidOperationException">A config file was found but is invalid — malformed
	/// JSON, an unknown key, or a value outside its documented range. The message names every problem
	/// found, not just the first, since fixing them one at a time across repeated runs is needless
	/// friction for a file the user is meant to hand-edit.</exception>
	public static string? LoadAndActivate() {
		(VbrConfig config, string? path) = Load();
		VbrConfig.Current = config;
		return path;
	}

	/// <summary>Same discovery/parse/validate as <see cref="LoadAndActivate"/>, without touching
	/// <see cref="VbrConfig.Current"/> — the testable core; tests construct a config file, call this,
	/// and assert on the returned <see cref="VbrConfig"/> without a static's state leaking between
	/// tests.</summary>
	public static (VbrConfig Config, string? Path) Load() {
		string? path = FindConfigFile(Environment.CurrentDirectory, VbrPaths.GetStateRootFolder());
		if (path is null) return (VbrConfig.Default, null);
		return (LoadFrom(path), path);
	}

	/// <summary>Parses and validates the config file at an exact, caller-given path — skips discovery
	/// entirely, for tests that want to point at a specific temp file rather than manipulate the
	/// current directory or state root.</summary>
	/// <exception cref="InvalidOperationException">See <see cref="LoadAndActivate"/>.</exception>
	public static VbrConfig LoadFrom(string path) {
		string json;
		try {
			json = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new InvalidOperationException($"Could not read config file '{path}': {ex.Message}", ex);
		}

		var options = new JsonSerializerOptions {
			ReadCommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
			PropertyNameCaseInsensitive = true,
			// Unknown-key rejection (docs/iterativeplan.md: "a misspelled key that silently does
			// nothing is the classic config-file trap") -- applies recursively to every nested
			// section record, not just the top level, since JsonSerializer applies these options to
			// every type it (de)serializes through the same pipeline.
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		};

		VbrConfig? config;
		try {
			config = JsonSerializer.Deserialize<VbrConfig>(json, options);
		}
		catch (JsonException ex) {
			throw new InvalidOperationException($"Invalid config file '{path}': {ex.Message}", ex);
		}
		config ??= VbrConfig.Default;

		List<string> errors = Validate(config);
		if (errors.Count > 0)
			throw new InvalidOperationException(
				$"Invalid config file '{path}':\n" + string.Join("\n", errors.ConvertAll(e => "  - " + e)));

		return config;
	}

	/// <summary><paramref name="cwdFolder"/>'s own <c>vbr.config.json</c> (a project-local override)
	/// wins over one under <paramref name="stateRootFolder"/> (the same per-OS root both stores'
	/// dedicated subfolders live under). Neither checked location is created here — this is a
	/// read-only lookup; a missing file at either spot is not an error, just "use built-in
	/// defaults." Both folders are parameters (rather than reading <see cref="Environment.CurrentDirectory"/>/
	/// <see cref="VbrPaths.GetStateRootFolder"/> directly) purely so tests can point at temp
	/// directories instead of touching the real current directory or the real per-OS state root —
	/// <see cref="Load"/> is the only real caller, and always passes the genuine locations.</summary>
	internal static string? FindConfigFile(string cwdFolder, string stateRootFolder) {
		string cwdPath = Path.Combine(cwdFolder, ConfigFileName);
		if (File.Exists(cwdPath)) return cwdPath;
		string statePath = Path.Combine(stateRootFolder, ConfigFileName);
		return File.Exists(statePath) ? statePath : null;
	}

	/// <summary>Per-key range checks (docs/iterativeplan.md: "thresholds in (0,1], intervals &gt; 0,
	/// caps &gt;= 1, ..."). Collects every violation rather than stopping at the first, named by its
	/// full <c>section.key</c> path plus the value and the accepted range, so a config file with
	/// several mistakes can be fixed in one pass instead of one run-fix-rerun cycle per key.</summary>
	static List<string> Validate(VbrConfig config) {
		var errors = new List<string>();

		void Positive(double value, string name) {
			if (value <= 0) errors.Add($"{name}: must be > 0 (got {value}).");
		}
		void NonNegative(double value, string name) {
			if (value < 0) errors.Add($"{name}: must be >= 0 (got {value}).");
		}
		void Unit(float value, string name) {
			if (value <= 0 || value > 1) errors.Add($"{name}: must be in (0, 1] (got {value}).");
		}
		void AtLeast(int value, int min, string name) {
			if (value < min) errors.Add($"{name}: must be >= {min} (got {value}).");
		}
		void Range(int value, int min, int max, string name) {
			if (value < min || value > max) errors.Add($"{name}: must be between {min} and {max} (got {value}).");
		}
		void NotBlank(string value, string name) {
			if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name}: must not be blank.");
		}

		NonNegative(config.FrameQuality.MinDetail, "frameQuality.minDetail");
		NonNegative(config.FrameQuality.DarkOverrideDetail, "frameQuality.darkOverrideDetail");
		if (config.FrameQuality.DarkRejectPercent < 0 || config.FrameQuality.DarkRejectPercent > 100)
			errors.Add($"frameQuality.darkRejectPercent: must be between 0 and 100 (got {config.FrameQuality.DarkRejectPercent}).");

		Unit(config.Matching.PresenceThreshold, "matching.presenceThreshold");
		Unit(config.Matching.RigidHitThreshold, "matching.rigidHitThreshold");
		Unit(config.Matching.PHashPresenceThreshold, "matching.phashPresenceThreshold");
		Unit(config.Matching.AudioMinSimilarity, "matching.audioMinSimilarity");

		Positive(config.Sampling.MatchSampleIntervalSeconds, "sampling.matchSampleIntervalSeconds");
		Positive(config.Sampling.AddBumperSampleIntervalSeconds, "sampling.addBumperSampleIntervalSeconds");
		NonNegative(config.Sampling.ScanEdgeBoundarySeconds, "sampling.scanEdgeBoundarySeconds");
		Positive(config.Sampling.ScanDenseIntervalSeconds, "sampling.scanDenseIntervalSeconds");
		Positive(config.Sampling.ScanSparseIntervalSeconds, "sampling.scanSparseIntervalSeconds");
		NonNegative(config.Sampling.SearchLengthSlackSeconds, "sampling.searchLengthSlackSeconds");
		AtLeast(config.Sampling.MaxFramesPerZone, 1, "sampling.maxFramesPerZone");
		AtLeast(config.Sampling.SparseFrameCapMargin, 0, "sampling.sparseFrameCapMargin");

		NonNegative(config.Removal.EndCutOvershootSafetyMarginSeconds, "removal.endCutOvershootSafetyMarginSeconds");
		Positive(config.Removal.KeyframeSearchWindowSeconds, "removal.keyframeSearchWindowSeconds");
		NotBlank(config.Removal.ReEncodePreset, "removal.reEncodePreset");
		NotBlank(config.Removal.ReEncodeAudioCodec, "removal.reEncodeAudioCodec");
		NotBlank(config.Removal.ReEncodeAudioBitrate, "removal.reEncodeAudioBitrate");
		Range(config.Removal.H264Quality, 0, 51, "removal.h264Quality");
		Range(config.Removal.HevcQuality, 0, 51, "removal.hevcQuality");
		Range(config.Removal.Vp9Crf, 0, 63, "removal.vp9Crf");
		NotBlank(config.Removal.GpuNvencPreset, "removal.gpuNvencPreset");
		NotBlank(config.Removal.GpuQsvPreset, "removal.gpuQsvPreset");
		NonNegative(config.Removal.StreamCopyDurationToleranceSeconds, "removal.streamCopyDurationToleranceSeconds");
		NonNegative(config.Removal.ValidateFilesDurationToleranceSeconds, "removal.validateFilesDurationToleranceSeconds");

		Positive(config.Scan.CheckpointIntervalSeconds, "scan.checkpointIntervalSeconds");

		AtLeast(config.Storage.SaveRetryAttempts, 1, "storage.saveRetryAttempts");
		NonNegative(config.Storage.SaveRetryDelayMilliseconds, "storage.saveRetryDelayMilliseconds");

		AtLeast(config.Limits.MaxLabelLength, 1, "limits.maxLabelLength");
		AtLeast(config.Limits.MaxDescriptionLength, 1, "limits.maxDescriptionLength");
		AtLeast(config.Limits.MaxDirectMlDeviceIdToTry, 0, "limits.maxDirectMlDeviceIdToTry");

		return errors;
	}
}
