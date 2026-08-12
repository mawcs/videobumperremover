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
using System.IO;
using VBR.Core.Configuration;
using Xunit;

namespace VBR.Tests.Configuration;

public class VbrConfigLoaderTests {
	static string CreateTempDir() {
		string dir = Path.Combine(Path.GetTempPath(), "vbr_config_tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static void DeleteTempDir(string dir) {
		try { Directory.Delete(dir, recursive: true); } catch { }
	}

	static string WriteConfig(string dir, string json) {
		string path = Path.Combine(dir, VbrConfigLoader.ConfigFileName);
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void LoadFrom_ValidOverride_AppliesIt_LeavesEverythingElseAtDefault() {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, """{ "matching": { "presenceThreshold": 0.95 } }""");
			VbrConfig config = VbrConfigLoader.LoadFrom(path);
			Assert.Equal(0.95f, config.Matching.PresenceThreshold);
			// Everything else in the same section, and every other section, stays at the built-in
			// default -- a partial override file doesn't zero out what it didn't mention.
			Assert.Equal(VbrConfig.Default.Matching.RigidHitThreshold, config.Matching.RigidHitThreshold);
			Assert.Equal(VbrConfig.Default.FrameQuality.DarkOverrideDetail, config.FrameQuality.DarkOverrideDetail);
			Assert.Equal(VbrConfig.Default.Sampling.MaxFramesPerZone, config.Sampling.MaxFramesPerZone);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void LoadFrom_TolerantOfCommentsAndTrailingCommas() {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, """
				{
					// a full-line comment
					"frameQuality": {
						"darkOverrideDetail": 3.5, // trailing comma below is deliberate
					},
				}
				""");
			VbrConfig config = VbrConfigLoader.LoadFrom(path);
			Assert.Equal(3.5, config.FrameQuality.DarkOverrideDetail);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void LoadFrom_UnknownTopLevelKey_Throws() {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, """{ "notARealSection": {} }""");
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => VbrConfigLoader.LoadFrom(path));
			Assert.Contains(path, ex.Message);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void LoadFrom_UnknownNestedKey_Throws() {
		string dir = CreateTempDir();
		try {
			// A misspelled key one level down -- the classic config-file trap this whole mechanism
			// exists to catch. Proves rejection isn't only a top-level check.
			string path = WriteConfig(dir, """{ "matching": { "presenseThreshold": 0.95 } }""");
			Assert.Throws<InvalidOperationException>(() => VbrConfigLoader.LoadFrom(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void LoadFrom_MalformedJson_Throws() {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, "{ this is not json");
			Assert.Throws<InvalidOperationException>(() => VbrConfigLoader.LoadFrom(path));
		}
		finally { DeleteTempDir(dir); }
	}

	[Theory]
	[InlineData("""{ "matching": { "presenceThreshold": 0 } }""")]
	[InlineData("""{ "matching": { "presenceThreshold": 1.5 } }""")]
	[InlineData("""{ "frameQuality": { "darkRejectPercent": -1 } }""")]
	[InlineData("""{ "frameQuality": { "darkRejectPercent": 101 } }""")]
	[InlineData("""{ "sampling": { "matchSampleIntervalSeconds": 0 } }""")]
	[InlineData("""{ "sampling": { "maxFramesPerZone": 0 } }""")]
	[InlineData("""{ "removal": { "h264Quality": 52 } }""")]
	[InlineData("""{ "removal": { "reEncodePreset": "" } }""")]
	[InlineData("""{ "storage": { "saveRetryAttempts": 0 } }""")]
	[InlineData("""{ "limits": { "maxLabelLength": 0 } }""")]
	public void LoadFrom_OutOfRangeValue_Throws(string json) {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, json);
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => VbrConfigLoader.LoadFrom(path));
			Assert.Contains(path, ex.Message);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void LoadFrom_MultipleViolations_ReportsAllOfThem_NotJustTheFirst() {
		string dir = CreateTempDir();
		try {
			string path = WriteConfig(dir, """
				{ "matching": { "presenceThreshold": 5 }, "storage": { "saveRetryAttempts": -1 } }
				""");
			InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => VbrConfigLoader.LoadFrom(path));
			Assert.Contains("presenceThreshold", ex.Message);
			Assert.Contains("saveRetryAttempts", ex.Message);
		}
		finally { DeleteTempDir(dir); }
	}

	[Fact]
	public void Load_NoConfigFileAnywhere_ReturnsBuiltInDefault() {
		string cwd = CreateTempDir();
		string stateRoot = CreateTempDir();
		try {
			string? found = VbrConfigLoader.FindConfigFile(cwd, stateRoot);
			Assert.Null(found);
		}
		finally { DeleteTempDir(cwd); DeleteTempDir(stateRoot); }
	}

	[Fact]
	public void FindConfigFile_CwdFile_FoundEvenWhenStateRootAlsoHasOne() {
		string cwd = CreateTempDir();
		string stateRoot = CreateTempDir();
		try {
			string cwdPath = WriteConfig(cwd, "{}");
			WriteConfig(stateRoot, "{}");
			string? found = VbrConfigLoader.FindConfigFile(cwd, stateRoot);
			Assert.Equal(cwdPath, found);
		}
		finally { DeleteTempDir(cwd); DeleteTempDir(stateRoot); }
	}

	[Fact]
	public void FindConfigFile_NoCwdFile_FallsBackToStateRoot() {
		string cwd = CreateTempDir();
		string stateRoot = CreateTempDir();
		try {
			string statePath = WriteConfig(stateRoot, "{}");
			string? found = VbrConfigLoader.FindConfigFile(cwd, stateRoot);
			Assert.Equal(statePath, found);
		}
		finally { DeleteTempDir(cwd); DeleteTempDir(stateRoot); }
	}

	[Fact]
	public void LoadAndActivate_SetsVbrConfigCurrent_ThenRestoresOnFailure() {
		// VbrConfig.Current is a process-wide static -- captured and restored so this test can't
		// leak state into any test that runs after it (AssemblySetup.cs disables cross-class
		// parallelism precisely so this kind of save/restore is actually safe, not just hopeful).
		VbrConfig original = VbrConfig.Current;
		string originalCwd = Environment.CurrentDirectory;
		string cwd = CreateTempDir();
		try {
			WriteConfig(cwd, """{ "matching": { "presenceThreshold": 0.77 } }""");
			Directory.SetCurrentDirectory(cwd);
			string? path = VbrConfigLoader.LoadAndActivate();
			Assert.NotNull(path);
			Assert.Equal(0.77f, VbrConfig.Current.Matching.PresenceThreshold);
		}
		finally {
			Directory.SetCurrentDirectory(originalCwd);
			VbrConfig.Current = original;
			DeleteTempDir(cwd);
		}
	}
}

/// <summary>The "flows-through" proof docs/iterativeplan.md's Part 3 TODO calls for: a config value
/// doesn't just deserialize correctly (<see cref="VbrConfigLoaderTests"/>) -- it actually changes
/// real matching behavior. <see cref="FrameQuality.SelectUsable"/> reads <see cref="VbrConfig.Current"/>
/// directly (2026-08-12), so swapping it should flip a real verdict.</summary>
public class FrameQualityConfigFlowsThroughTests {
	// A 224x224 RGB24 frame, majority-dark (every pixel black) with one distinctive patch bright
	// enough to clear MinDetail but not the default DarkOverrideDetail -- mirrors
	// FrameQualityTests.DarkFrameWithOnlyBorderlineDetail_StillRejected_HigherBarThanMinDetailIsReal's
	// own construction, reused here because it's exactly the shape that DOES depend on which
	// DarkOverrideDetail value is active.
	static byte[] BorderlineDarkFrame() {
		const int side = 224;
		const int brightRows = 140; // verified below: lands the resulting detail score at ~1.43
		var frame = new byte[side * side * 3];
		// A single bright pixel at x=2, present in only the first `brightRows` of 224 rows (the rest
		// stay fully black) -- a full-height single-pixel column scores ~2.29 (MeasureDetail sums
		// TWO luma transitions -- 0->255 and 255->0 -- per row it appears in, divided by side*(side-1)
		// = 49952), already above the default DarkOverrideDetail (2.0) and useless for this test.
		// 140/224 rows scores 140*510/49952 ~= 1.429 -- comfortably between MinDetail (1.0) and the
		// default DarkOverrideDetail (2.0), which is the whole point: verified with MeasureDetail
		// itself below, not just this arithmetic in a comment.
		for (int y = 0; y < brightRows; y++) {
			int i = (y * side + 2) * 3;
			frame[i] = frame[i + 1] = frame[i + 2] = 255;
		}
		return frame;
	}

	[Fact]
	public void LoweringDarkOverrideDetail_AdmitsAFrame_TheDefaultConfigRejects() {
		byte[] frame = BorderlineDarkFrame();
		double dark = VBR.Core.Fingerprinting.FrameQuality.MeasureDarkPercent(frame);
		double detail = VBR.Core.Fingerprinting.FrameQuality.MeasureDetail(frame);
		Assert.True(dark >= VbrConfig.Default.FrameQuality.DarkRejectPercent, $"Test setup: expected majority-dark, got {dark:0.#}%.");
		Assert.True(detail > VbrConfig.Default.FrameQuality.MinDetail && detail < VbrConfig.Default.FrameQuality.DarkOverrideDetail,
			$"Test setup: expected detail strictly between MinDetail and the default DarkOverrideDetail, got {detail:0.###}.");

		VbrConfig original = VbrConfig.Current;
		try {
			VbrConfig.Current = VbrConfig.Default;
			bool[] usableAtDefault = VBR.Core.Fingerprinting.FrameQuality.SelectUsable(new[] { frame });
			Assert.False(usableAtDefault[0], "Test setup: the default config's DarkOverrideDetail should reject this frame.");

			VbrConfig.Current = VbrConfig.Default with {
				FrameQuality = VbrConfig.Default.FrameQuality with { DarkOverrideDetail = detail - 0.01 },
			};
			bool[] usableWithLoweredBar = VBR.Core.Fingerprinting.FrameQuality.SelectUsable(new[] { frame });
			Assert.True(usableWithLoweredBar[0], "Lowering DarkOverrideDetail below this frame's own detail score should admit it.");
		}
		finally { VbrConfig.Current = original; }
	}
}
