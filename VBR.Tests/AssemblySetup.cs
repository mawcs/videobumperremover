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

// VBR.Core.Configuration.VbrConfig.Current is a process-wide mutable static (docs/iterativeplan.md,
// "File-path DB options" entry, Part 3) that FrameQuality/ClipRemover/etc. now read directly --
// xUnit's default per-class-collection parallelism would let a test that swaps VbrConfig.Current
// (e.g. a config-flows-through test) race against any other concurrently-running test that reads it
// (e.g. FrameQualityTests, which assumes default values). Disabled assembly-wide rather than only for
// the new config tests: any future test touching this static (or HardwareAcceleration's similar
// globals) inherits the same protection without needing to remember to opt in per class. The suite is
// small (well under a second today), so the parallelism this gives up costs nothing real.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
