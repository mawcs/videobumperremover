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

namespace VBR.Core.Configuration;

/// <summary>
/// Every value in this file was, until 2026-08-12, a hardcoded <c>const</c> somewhere in VBR.Core/
/// VBR.CLI (docs/iterativeplan.md, "File-path DB options" entry, Part 3). Each record property's own
/// default is that original constant's exact value — loading with no config file present reproduces
/// today's behavior exactly. <see cref="VbrConfigLoader"/> owns discovery/precedence/validation;
/// this file only owns the shape and the defaults.
///
/// <b>Not everything numeric moved here</b> — deliberately excluded, with reasons: format magics and
/// <c>CurrentFormatVersion</c>s (file identity, not behavior); <c>OnnxEmbedder.InputSide</c> (the
/// DINOv2 model's own fixed input size) and <c>EmbeddingMath.QuantScale</c> (stored-embedding
/// comparability — both are upstream VDF.Core code shared with VDF's own dedup scan); pHash's
/// <c>HashSide</c>/<c>Block</c> geometry (stored hashes stop being comparable if changed); CLI parse
/// conventions (invariant decimal, duration suffixes). Changing any of these doesn't tune behavior,
/// it silently breaks correctness or comparability with already-persisted data.
///
/// <b><see cref="FrameQuality"/> and <see cref="Audio"/> are the sections stamped into saved
/// catalogs/databases and checked for staleness on load</b> (see <c>BumperCatalog.FrameQualitySnapshot</c>/
/// <c>LibraryDatabase.FrameQualitySnapshot</c>/<c>BumperCatalogEntry.FrameQualitySnapshot</c>, which
/// despite the name now covers both — see <see cref="Configuration.FrameQualitySnapshot"/>'s own doc
/// comment) — both are binary usable/not-usable *recipes* baked directly into the stored bytes, not
/// just a judgment applied afterward: <see cref="FrameQuality"/> gates which frames even get
/// embedded (drift between two stores means one side is being compared against content the other
/// never saw — the exact 2026-08-07 incident); <see cref="Audio"/>'s <c>BucketSeconds</c> changes
/// what each stored fingerprint element *encodes* (two fingerprints built at different bucket sizes
/// aren't directly comparable at all — docs/iterativeplan.md, "Audio bucket phase-alignment" entry,
/// 2026-08-14). <see cref="Sampling"/> is deliberately NOT stamped: presence matching
/// (<c>VisualBumperMatcher.ComparePresence</c>) is interval-agnostic by design (see the "does the
/// sample interval have to match" design note in iterativeplan.md, 2026-08-11) — a coarser interval
/// can only cost recall, never turn an already-found match into a wrong one, so treating it as a
/// staleness signal would false-alarm on the normal, correct state.
/// </summary>
public sealed record VbrConfig {
	public FrameQualityConfig FrameQuality { get; init; } = new();
	public AudioConfig Audio { get; init; } = new();
	public MatchingConfig Matching { get; init; } = new();
	public SamplingConfig Sampling { get; init; } = new();
	public RemovalConfig Removal { get; init; } = new();
	public ScanConfig Scan { get; init; } = new();
	public StorageConfig Storage { get; init; } = new();
	public LimitsConfig Limits { get; init; } = new();

	/// <summary>Every field at its original hardcoded value — what any command gets with no config
	/// file present anywhere.</summary>
	public static readonly VbrConfig Default = new();

	/// <summary>The active config for this process — <see cref="Default"/> until
	/// <see cref="VbrConfigLoader.LoadAndActivate"/> runs (once, at CLI startup, before any command
	/// parses its options). Every call site in this codebase reads this static rather than taking a
	/// config instance as a constructor/method parameter, the same convention
	/// <c>HardwareAcceleration.Mode</c> already established for other process-wide, set-once-at-
	/// startup state.</summary>
	public static VbrConfig Current { get; internal set; } = Default;
}

/// <summary>Low-information frame filtering (<see cref="Fingerprinting.FrameQuality"/>) — the one
/// section that is a real fingerprint-recipe (see <see cref="VbrConfig"/>'s own doc comment).</summary>
public sealed record FrameQualityConfig {
	/// <summary>Original: <c>Fingerprinting.FrameQuality.MinDetail</c>.</summary>
	public double MinDetail { get; init; } = 1.0;

	/// <summary>Original: <c>Fingerprinting.FrameQuality.DarkOverrideDetail</c>.</summary>
	public double DarkOverrideDetail { get; init; } = 2.0;

	/// <summary>Original: <c>Fingerprinting.FrameQuality.DarkRejectPercent</c>.</summary>
	public double DarkRejectPercent { get; init; } = 80.0;
}

/// <summary>Audio-fingerprint recipe (<see cref="Chromaprint.ChromaContext"/>, via VDF.Core) — a
/// fingerprint recipe like <see cref="FrameQuality"/> (see <see cref="VbrConfig"/>'s own doc
/// comment), not a live comparison parameter: it changes what gets encoded into
/// <c>BumperCatalogEntry.AudioFingerprint</c>/<c>LibraryDatabaseEntry.AudioFingerprint</c> at
/// extraction time, so a mismatch between two stores (or between a store and the currently active
/// config) means the two fingerprint arrays aren't meaningfully comparable at all — not just
/// somewhat less precise, the way a coarser <see cref="Sampling"/> interval is.</summary>
public sealed record AudioConfig {
	/// <summary>How much audio each element of a Chromaprint fingerprint array represents, in
	/// seconds — originally a hardcoded 1.0 (<c>ChromaContext</c>'s majority-vote aggregation bucket
	/// boundary). Real testing (docs/iterativeplan.md, "Audio bucket phase-alignment" entry,
	/// 2026-08-14) found that a reference clip extracted independently from a source file is
	/// essentially never phase-aligned with that source's own continuous decode-from-BOF bucket grid
	/// — even byte-identical audio can score well below 100% purely from a sub-bucket timing offset,
	/// because <c>ScanEngine.SlidingWindowCompare</c> only searches whole-bucket offsets. Lowering
	/// this shrinks the worst-case phase error proportionally (bucket size directly bounds it) at the
	/// cost of a correspondingly larger stored fingerprint array per file. Range: > 0.</summary>
	public double BucketSeconds { get; init; } = 1.0;
}

/// <summary>Presence-matching thresholds (<see cref="Matching.VisualBumperMatcher"/>/
/// <see cref="Matching.AudioBumperMatcher"/>). Live comparison parameters, not baked into any stored
/// file — changing these never invalidates an existing catalog/database (see <see cref="VbrConfig"/>'s
/// doc comment on what IS a recipe).</summary>
public sealed record MatchingConfig {
	/// <summary>Original: <c>VisualBumperMatcher.DefaultPresenceThreshold</c>. Also
	/// <c>--presence-threshold</c>'s CLI default.</summary>
	public float PresenceThreshold { get; init; } = 0.90f;

	/// <summary>Original: <c>VisualBumperMatcher.DefaultRigidHitThreshold</c>. No CLI flag exists for
	/// this today (per the automatic-over-manual principle, this config key is the intended way to
	/// adjust it rather than adding one).</summary>
	public float RigidHitThreshold { get; init; } = 0.89f;

	/// <summary>Original: <c>VisualBumperMatcher.DefaultPHashPresenceThreshold</c>. Also
	/// <c>--phash-presence-threshold</c>'s CLI default.</summary>
	public float PHashPresenceThreshold { get; init; } = 0.96f;

	/// <summary>Original: <c>AudioBumperMatcher.DefaultMinSimilarity</c>. Also
	/// <c>--min-similarity</c>'s CLI default.</summary>
	public float AudioMinSimilarity { get; init; } = 0.80f;
}

/// <summary>Sampling density/coverage knobs — deliberately NOT a fingerprint recipe (see
/// <see cref="VbrConfig"/>'s doc comment): mismatches here cost thoroughness/recall, never
/// correctness.</summary>
public sealed record SamplingConfig {
	/// <summary>Original: <c>Matching.VisualBumperMatcher.DefaultSampleIntervalSeconds</c>. Also
	/// <c>match</c>/<c>remove</c>'s shared <c>--sample-interval</c> CLI default.</summary>
	public double MatchSampleIntervalSeconds { get; init; } = 1.0;

	/// <summary>Original: <c>Catalog.BumperCatalogBuilder.DefaultSampleInterval</c>. Also
	/// <c>add-bumper</c>'s own local <c>--sample-interval</c> CLI default (deliberately denser than
	/// match/remove's — see that option's own description).</summary>
	public double AddBumperSampleIntervalSeconds { get; init; } = 0.2;

	/// <summary>Original: <c>ScanCommand</c>'s local <c>--edge-boundary</c> CLI default.</summary>
	public double ScanEdgeBoundarySeconds { get; init; } = 20.0;

	/// <summary>Original: <c>ScanCommand</c>'s local <c>--sample-interval</c> CLI default (the dense
	/// interval within the edge boundary).</summary>
	public double ScanDenseIntervalSeconds { get; init; } = 0.2;

	/// <summary>Original: <c>ScanCommand</c>'s local <c>--sparse-interval</c> CLI default.</summary>
	public double ScanSparseIntervalSeconds { get; init; } = 4.0;

	/// <summary>Original: the hardcoded <c>+ TimeSpan.FromSeconds(20)</c> both <c>match</c> and
	/// <c>remove</c> fall back to when <c>--search-length</c> is omitted.</summary>
	public double SearchLengthSlackSeconds { get; init; } = 20.0;

	/// <summary>Original: <c>WholeFileSampler.MaxDenseFramesPerZone</c>,
	/// <c>Fingerprinting.MixedDensitySampler.MaxFramesPerZone</c>, and
	/// <c>Matching.VisualBumperMatcher.MaxFramesPerFile</c> — three independently-declared constants
	/// that were always the same value (400) for the same reason (a safety ceiling on one sampled
	/// zone/region, generous relative to any interval/window this project targets); unified into one
	/// config key rather than kept as three copies that could silently drift apart.</summary>
	public int MaxFramesPerZone { get; init; } = 400;

	/// <summary>Original: <c>WholeFileSampler.SparseFrameCapMargin</c>.</summary>
	public int SparseFrameCapMargin { get; init; } = 20;
}

/// <summary>Removal-cut mechanics (<see cref="Removal.ClipRemover"/>) and its two duration-tolerance
/// sanity checks elsewhere. None of this affects matching/fingerprints at all — purely about how a
/// cut is produced and re-verified.</summary>
public sealed record RemovalConfig {
	/// <summary>Original: <c>ClipRemover.EndCutOvershootSafetyMarginSeconds</c>.</summary>
	public double EndCutOvershootSafetyMarginSeconds { get; init; } = 1.0;

	/// <summary>Original: <c>ClipRemover</c>'s local <c>KeyframeSearchWindowSeconds</c>.</summary>
	public double KeyframeSearchWindowSeconds { get; init; } = 30.0;

	/// <summary>Original: <c>ClipRemover</c>'s local <c>ReEncodePreset</c> (CPU x264/x265 preset).</summary>
	public string ReEncodePreset { get; init; } = "medium";

	/// <summary>Original: <c>ClipRemover</c>'s local <c>ReEncodeAudioCodec</c>.</summary>
	public string ReEncodeAudioCodec { get; init; } = "aac";

	/// <summary>Original: <c>ClipRemover</c>'s local <c>ReEncodeAudioBitrate</c>.</summary>
	public string ReEncodeAudioBitrate { get; init; } = "192k";

	/// <summary>Original: the <c>"22"</c> quality target <c>SelectVideoEncoder</c> used for H.264,
	/// shared verbatim between the CPU row's <c>-crf</c> and every GPU vendor's own quality flag
	/// (<c>-cq</c>/<c>-global_quality</c>/<c>-qp_i</c>/<c>-qp_p</c>) — one target per codec family,
	/// not per vendor, matching how the original code actually computed it.</summary>
	public int H264Quality { get; init; } = 22;

	/// <summary>Original: the <c>"24"</c> quality target <c>SelectVideoEncoder</c> used for
	/// HEVC/H.265, same sharing as <see cref="H264Quality"/>.</summary>
	public int HevcQuality { get; init; } = 24;

	/// <summary>Original: <c>SelectVideoEncoder</c>'s VP9 <c>-crf</c> (CPU-only — VP9 has no GPU row).</summary>
	public int Vp9Crf { get; init; } = 31;

	/// <summary>Original: the <c>"p5"</c> NVENC <c>-preset</c>.</summary>
	public string GpuNvencPreset { get; init; } = "p5";

	/// <summary>Original: the <c>"slow"</c> QSV <c>-preset</c>.</summary>
	public string GpuQsvPreset { get; init; } = "slow";

	/// <summary>Original: <c>Extraction.ClipExtractor</c>'s local <c>StreamCopyDurationToleranceSeconds</c>.</summary>
	public double StreamCopyDurationToleranceSeconds { get; init; } = 2.0;

	/// <summary>Original: <c>Cleanup.LibraryCleaner.ValidateFilesDurationToleranceSeconds</c>.</summary>
	public double ValidateFilesDurationToleranceSeconds { get; init; } = 2.0;
}

/// <summary>Scan-run operational behavior, independent of what gets sampled.</summary>
public sealed record ScanConfig {
	/// <summary>Original: <c>Database.LibraryScanner</c>'s local <c>DefaultCheckpointInterval</c>
	/// (was already a constructor-overridable <c>TimeSpan?</c> for tests; this is just its default).</summary>
	public double CheckpointIntervalSeconds { get; init; } = 30.0;
}

/// <summary>Persisted-file save retry behavior, shared by both stores.</summary>
public sealed record StorageConfig {
	/// <summary>Original: <c>BumperCatalogStore</c>/<c>LibraryDatabaseStore</c>'s identical local
	/// <c>MoveRetryAttempts</c> — one shared setting, not two copies.</summary>
	public int SaveRetryAttempts { get; init; } = 4;

	/// <summary>Original: <c>BumperCatalogStore</c>/<c>LibraryDatabaseStore</c>'s identical local
	/// <c>MoveRetryDelay</c> (150ms).</summary>
	public double SaveRetryDelayMilliseconds { get; init; } = 150.0;
}

/// <summary>Plain input-validation ceilings, unrelated to matching/sampling/removal.</summary>
public sealed record LimitsConfig {
	/// <summary>Original: <c>AddBumperCommand.MaxLabelLength</c> (was 30 -- raised to 80, 2026-08-13,
	/// per real dogfooding: 30 characters proved too tight for real bumper labels in practice).</summary>
	public int MaxLabelLength { get; init; } = 80;

	/// <summary>Original: <c>AddBumperCommand.MaxDescriptionLength</c>.</summary>
	public int MaxDescriptionLength { get; init; } = 255;

	/// <summary>Original: <c>Extraction.HardwareAcceleration</c>'s local <c>MaxDirectMlDeviceIdToTry</c>
	/// — only consulted when DXGI adapter enumeration itself found nothing to iterate.</summary>
	public int MaxDirectMlDeviceIdToTry { get; init; } = 4;
}
