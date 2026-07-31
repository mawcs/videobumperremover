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
using SharpGen.Runtime;
using Vortice.DXGI;

namespace VBR.Core.Extraction;

/// <summary>
/// Enumerates real GPU DXGI adapters for DirectML device-index selection
/// (docs/decisions/0013-gpu-acceleration.md), filtering out virtual display adapters that DXGI
/// happily enumerates alongside real hardware but that have no compute behind them at all.
///
/// Live-verified on two completely unrelated machines: an RDP session's "Microsoft Remote Display
/// Adapter," and (on a different, local, non-RDP machine) a persistent "Meta Virtual Monitor" left
/// behind by Meta Quest Link software even with no headset physically attached. Both are Windows
/// Indirect Display Driver (IDD) adapters — a general Windows platform mechanism also used by most
/// screen-streaming/remote-desktop/VR software, not unique to either vendor — and both falsely
/// advertise full D3D12 feature-level support despite having <c>Chip type: Unknown</c> and no real
/// compute hardware. <see cref="HardwareAcceleration.ProbeDirectMlInSubprocess"/> previously tried
/// device indices 0 through 4 blindly, which is fragile in the presence of *any* such software —
/// this enumerator lets it try only indices that are actually real GPUs instead.
/// </summary>
public static class DirectMlAdapterEnumerator {
	// Known real discrete/integrated GPU vendor PCI IDs. A virtual/indirect-display adapter
	// reports something else -- Microsoft's own vendor ID for RDP's adapter, or no recognizable
	// GPU vendor at all for Meta's -- so restricting to this known set is a stronger, more direct
	// signal than DXGI_ADAPTER_FLAG_SOFTWARE alone, which only catches WARP/Microsoft Basic
	// Render Driver: live-verified that neither phantom adapter observed so far was flagged
	// AdapterFlags.Software, since IDD adapters are a different mechanism from the software
	// rasterizer that flag exists for.
	const uint VendorNvidia = 0x10DE;
	const uint VendorAmd1 = 0x1002;
	const uint VendorAmd2 = 0x1022;
	const uint VendorIntel = 0x8086;
	const uint VendorQualcomm = 0x5143;

	public readonly record struct AdapterInfo(int Index, string Description, uint VendorId, long DedicatedVideoMemory);

	/// <summary>Real GPU adapters only, in DXGI enumeration order — the same order/index space
	/// <c>AppendExecutionProvider_DML(deviceId)</c> uses, per ONNX Runtime's own documentation.
	/// Virtual/indirect-display and software-rasterizer adapters are excluded. Returns an empty
	/// list (never throws) if enumeration itself fails for any reason — a machine with no D3D12
	/// support at all, a driver issue, whatever — since "enumeration failed" and "no real GPU
	/// exists" aren't the same thing; callers should fall back to their own prior behavior (e.g.
	/// trying a small range of indices blindly) rather than give up outright.</summary>
	public static IReadOnlyList<AdapterInfo> GetRealGpuAdapters() {
		var result = new List<AdapterInfo>();
		try {
			using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
			for (uint i = 0; ; i++) {
				Result hr = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
				if (hr.Failure || adapter is null)
					break;
				using (adapter) {
					AdapterDescription1 desc = adapter.Description1;
					bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
					bool isKnownGpuVendor = desc.VendorId is VendorNvidia or VendorAmd1 or VendorAmd2 or VendorIntel or VendorQualcomm;
					if (!isSoftware && isKnownGpuVendor)
						result.Add(new AdapterInfo((int)i, desc.Description, desc.VendorId, desc.DedicatedVideoMemory));
				}
			}
		}
		catch {
			return Array.Empty<AdapterInfo>();
		}
		return result;
	}
}
