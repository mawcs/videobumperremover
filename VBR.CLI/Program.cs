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
using VBR.CLI.Commands;
using VBR.Core.Configuration;
using VBR.Core.Extraction;

// Hidden, undocumented entry point: a child-process probe target for
// HardwareAcceleration.ProbeDirectMlInSubprocess (docs/decisions/0013-gpu-acceleration.md) --
// checked before any normal CLI parsing so a crash here (the exact failure mode this probe exists
// to catch: a native access violation that no managed try/catch can contain) never touches
// System.CommandLine or a real command's state, only this one throwaway invocation.
if (args.Length >= 3 && args[0] == HardwareAcceleration.DirectMlProbeArgument)
	return HardwareAcceleration.RunDirectMlProbe(args[1], int.Parse(args[2]));

// Must run before any Option's DefaultValueFactory can fire (docs/iterativeplan.md, "File-path DB
// options" entry, Part 3): those factories read VbrConfig.Current, and System.CommandLine only
// invokes one lazily, at parse time -- but "before Parse is called at all" is the simplest correct
// place to guarantee this runs first, rather than relying on that laziness. A bad config file fails
// every command identically and immediately, same as a bad CLI argument would.
try {
	VbrConfigLoader.LoadAndActivate();
}
catch (System.InvalidOperationException ex) {
	Console.Error.WriteLine($"Error: {ex.Message}");
	return 1;
}

var root = new RootCommand("vbr-cli — Video Bumper Remover command-line interface");
root.Subcommands.Add(MatchCommand.Build());
root.Subcommands.Add(RemoveCommand.Build());
root.Subcommands.Add(TrimCommand.Build());
root.Subcommands.Add(CommitCommand.Build());
root.Subcommands.Add(ScanCommand.Build());
root.Subcommands.Add(AddBumperCommand.Build());
root.Subcommands.Add(ListBumpersCommand.Build());
return await root.Parse(args).InvokeAsync();
