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
using MemoryPack;
using VBR.Core.Extraction;
using VBR.Core.Fingerprinting;

namespace VBR.Core.Catalog;

/// <summary>
/// One known bumper, independent of any particular video (docs/iterativeplan.md, "Bumper catalog").
/// The persisted counterpart to a single <c>vbr add-bumper</c> call — mirrors
/// <see cref="Database.LibraryDatabaseEntry"/>'s shape (same <see cref="TimedFingerprint"/> type, same
/// change-detection-free simplicity: a catalog entry is curated once, not periodically re-verified
/// against a source file the way a library entry is).
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public sealed partial class BumperCatalogEntry {
	/// <summary>Stable, internal identifier (a GUID) — never meant to be typed by a human; names
	/// the reference clip file and is the dictionary key in <see cref="BumperCatalog.Entries"/>.
	/// <see cref="Label"/> is the human-facing handle a future lookup command would key off.</summary>
	[MemoryPackOrder(0)]
	public string Id { get; set; } = string.Empty;

	/// <summary>Required at add time; the human-facing lookup key. Length is enforced by the CLI,
	/// not here (plan decision: keep this model plain/unconstrained, validate at the boundary).</summary>
	[MemoryPackOrder(1)]
	public string Label { get; set; } = string.Empty;

	/// <summary>Optional, longer free text for curation context. Same length-enforcement note as
	/// <see cref="Label"/>.</summary>
	[MemoryPackOrder(2)]
	public string? Description { get; set; }

	[MemoryPackOrder(3)]
	public string[] Tags { get; set; } = Array.Empty<string>();

	/// <summary>Which edge this bumper lives at — the stored value *is* whatever <c>--region</c>
	/// was passed at add time, no separate category/inference step. No interstitial value exists
	/// yet because <see cref="ClipEdge"/> itself doesn't have one (deferred — see
	/// docs/iterativeplan.md's "Bumper catalog" entry for why deferring this doesn't cost a future
	/// library rebuild).</summary>
	[MemoryPackOrder(4)]
	public ClipEdge Region { get; set; }

	/// <summary>"active" | "retired". No curation UI exists yet to ever set this to "retired" —
	/// present for forward compatibility, always "active" for everything <c>add-bumper</c> writes.</summary>
	[MemoryPackOrder(5)]
	public string Status { get; set; } = "active";

	/// <summary>The bumper's precisely-measured length — probed from the *extracted reference
	/// clip*, not trusted from the requested <c>--clip-length</c> verbatim, since stream-copy
	/// extraction is keyframe-bound and the actual result can differ slightly. This is the value a
	/// future removal pass would use for ADR 0007's arithmetic cut (<c>fileDuration − Duration</c> /
	/// <c>Duration</c>).</summary>
	[MemoryPackOrder(6)]
	public TimeSpan Duration { get; set; }

	/// <summary>Same type <see cref="Database.LibraryDatabaseEntry.Fingerprints"/> uses — sampled
	/// directly from the source video (never from the extracted reference clip), matching
	/// <c>match</c>/<c>remove</c>'s established direct-source-decode path.</summary>
	[MemoryPackOrder(7)]
	public TimedFingerprint[] Fingerprints { get; set; } = Array.Empty<TimedFingerprint>();

	/// <summary>Chromaprint fingerprint of the extracted reference clip's audio (null when the
	/// bumper has no usable audio track) — the "audio accelerator" signal for audible bumpers.</summary>
	[MemoryPackOrder(8)]
	public uint[]? AudioFingerprint { get; set; }

	/// <summary>Relative to the catalog's own folder (see <see cref="BumperCatalogStore"/>) — a
	/// real, playable file so a human can preview/verify the entry without hunting down the
	/// original source.</summary>
	[MemoryPackOrder(9)]
	public string ReferenceClipPath { get; set; } = string.Empty;

	/// <summary>A single representative still frame, embedded directly (unlike
	/// <see cref="ReferenceClipPath"/>, which stays a sibling file) — a still is small enough that
	/// embedding costs little and avoids orphan-file bookkeeping. Stored at whatever resolution it
	/// was decoded at; no resize (maintainer decision, deferred not rejected). Empty when capture
	/// failed — best-effort, never blocks adding the bumper.</summary>
	[MemoryPackOrder(10)]
	public byte[] Thumbnail { get; set; } = Array.Empty<byte>();

	/// <summary>Provenance: the source video this entry was added from.</summary>
	[MemoryPackOrder(11)]
	public string SourceVideoPath { get; set; } = string.Empty;

	[MemoryPackOrder(12)]
	public DateTime DateAdded { get; set; }

	/// <summary>Zero until a future "apply" pass (catalog-aware matching, not yet built) updates
	/// it — adding a bumper doesn't remove anything itself.</summary>
	[MemoryPackOrder(13)]
	public int OccurrenceCount { get; set; }

	/// <summary>Which signal(s) must agree for this bumper to count as present — default
	/// <see cref="BumperMatchingStrategy.Corroborated"/> preserves today's exact behavior for every
	/// entry that predates this field (docs/iterativeplan.md, "Per-bumper matching strategy" entry,
	/// 2026-08-13).</summary>
	[MemoryPackOrder(14)]
	public BumperMatchingStrategy MatchingStrategy { get; set; } = BumperMatchingStrategy.Corroborated;

	/// <summary>How much to cut on an actual <c>remove</c>, when it differs from <see cref="Duration"/>
	/// (the region used to *identify* this bumper) — e.g. a cross-fade that needs a few extra seconds
	/// stripped beyond what's needed to match reliably. Null (the default) falls back to
	/// <see cref="Duration"/>, i.e. today's exact single-length behavior for every entry that
	/// predates this field.</summary>
	[MemoryPackOrder(15)]
	public TimeSpan? RemovalLength { get; set; }

	/// <summary>Per-bumper override of <c>VbrConfig.Current.Matching.PresenceThreshold</c> — null uses
	/// the global config value. This and the other three "matching" overrides below ("Group A") are
	/// genuine, live, comparison-time overrides (they judge an already-computed similarity score,
	/// never what got sampled) — contrast <see cref="FrameQualitySnapshot"/> below ("Group B"), which
	/// is provenance/metadata only, never read back to influence anything. See docs/iterativeplan.md's
	/// "Per-bumper matching strategy" entry.</summary>
	[MemoryPackOrder(19)]
	public float? PresenceThreshold { get; set; }

	/// <summary>Per-bumper override of <c>VbrConfig.Current.Matching.RigidHitThreshold</c>.</summary>
	[MemoryPackOrder(20)]
	public float? RigidHitThreshold { get; set; }

	/// <summary>Per-bumper override of <c>VbrConfig.Current.Matching.PHashPresenceThreshold</c>.</summary>
	[MemoryPackOrder(21)]
	public float? PHashPresenceThreshold { get; set; }

	/// <summary>Per-bumper override of <c>VbrConfig.Current.Matching.AudioMinSimilarity</c>.</summary>
	[MemoryPackOrder(22)]
	public float? AudioMinSimilarity { get; set; }

	/// <summary>The <c>frameQuality</c> config values active when this specific entry was added —
	/// pure provenance/metadata (docs/iterativeplan.md's "Per-bumper matching strategy" entry,
	/// "Group B" — deliberately never read back to influence matching itself, only to explain how
	/// this entry's own <see cref="Fingerprints"/> came to be, and to let the staleness warning
	/// compare against *this entry's own* recipe rather than the whole catalog's). Null for any
	/// entry added before this field existed, or before the per-entry stamp was wired in — treated
	/// as "unknown, not provably stale," same convention as <see cref="Catalog.BumperCatalog"/>'s own
	/// whole-file <c>FrameQualitySnapshot</c>.</summary>
	[MemoryPackOrder(23)]
	public Configuration.FrameQualitySnapshot? FrameQualitySnapshot { get; set; }
}
