// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//
// Modifications Copyright (C) 2026 mawcs — DirectML execution-provider acquisition
// (docs/decisions/0013-gpu-acceleration.md): a second, separately-versioned native runtime
// download path (Microsoft.ML.OnnxRuntime.DirectML + Microsoft.AI.DirectML NuGet packages,
// extracted the same way the existing GitHub-release archive is), kept in its own subfolder so it
// never collides with the CPU runtime this file already downloads.

using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using VDF.Core.Utils;

namespace VDF.Core.AI {
	public enum AiComponentsState { Missing, RuntimeMissing, ModelMissing, Ready }

	public readonly record struct AiDownloadProgress(string Step, long BytesDone, long? BytesTotal);

	/// <summary>
	/// Locates and downloads the two native components AI matching needs: the ONNX Runtime
	/// library (per-RID archive from the official microsoft/onnxruntime GitHub release,
	/// pinned version) and the DINOv2-small embedding model (SHA256-pinned). Nothing is
	/// bundled with VDF releases — the FFmpeg pattern: opt-in download on first use into
	/// <c>{StateFolder}/ai</c>. Under Native AOT the OnnxRuntime *Managed* wrapper is pure
	/// P/Invoke; a DllImport resolver points its "onnxruntime" import at the downloaded
	/// library, so no native lib has to sit next to the executable.
	/// </summary>
	public static class AiComponents {
		/// <summary>
		/// Pinned ONNX Runtime version. Must match the Microsoft.ML.OnnxRuntime.Managed
		/// PackageReference. Pinned to 1.23.0, not the newer 1.23.2 (Modifications Copyright (C)
		/// 2026 mawcs, docs/decisions/0013-gpu-acceleration.md): <see cref="DirectMlRuntimeVersion"/>
		/// has no 1.23.2 release at all (that NuGet package skips 1.23.0 → 1.24.1), and a
		/// managed/native version mismatch between the two caused a real, reproducible native
		/// crash (0xC0000005) in <c>InferenceSession.Init</c> on real GPU hardware — pinning both
		/// runtime flavors (and the managed wrapper) to the exact same version removes that
		/// mismatch as a variable. 1.23.0 still ships osx-x86_64 (Intel Mac) binaries, same as
		/// 1.23.2 did — confirmed against the actual GitHub release asset listing — so this
		/// doesn't reopen the platform-support reason 1.23.2 was originally chosen (1.24+ dropped
		/// Intel Macs, which VDF releases still target).
		/// </summary>
		public const string RuntimeVersion = "1.23.0";

		/// <summary>Pinned <c>Microsoft.ML.OnnxRuntime.DirectML</c> version — the exact same as
		/// <see cref="RuntimeVersion"/>, deliberately: see that constant's own doc comment for why
		/// keeping the two versions identical (rather than the nearest-available mismatch this
		/// project shipped with initially) was necessary, not just tidier.</summary>
		public const string DirectMlRuntimeVersion = RuntimeVersion;
		/// <summary>Pinned <c>Microsoft.AI.DirectML</c> version — the exact minimum
		/// <c>Microsoft.ML.OnnxRuntime.DirectML 1.23.0</c> depends on (confirmed against its
		/// published NuGet dependency group), also its latest published release.</summary>
		public const string DirectMlRedistVersion = "1.15.4";

		public const string ModelFileName = "dinov2-small-int8.onnx";
		/// <summary>SHA256 of the model file (Xenova/dinov2-small ONNX export, quantized, Apache-2.0).</summary>
		public const string ModelSha256 = "3afdc8bc63b50558d6e5770f5b799bb82455c2311183a2de43803f343a29d917";
		// Primary: mirrored release asset on the VDF repo (stable bytes, hash-pinned).
		// Fallback: the upstream HuggingFace export the mirror was taken from.
		const string ModelPrimaryUrl = "https://github.com/0x90d/videoduplicatefinder/releases/download/ai-models-v1/dinov2-small-int8.onnx";
		const string ModelFallbackUrl = "https://huggingface.co/Xenova/dinov2-small/resolve/main/onnx/model_quantized.onnx";
		const string VersionMarkerFileName = "runtime.version";

		public static string AiFolder => Path.Combine(CoreUtils.StateFolder, "ai");
		public static string ModelPath => TestOverrideModelPath ?? Path.Combine(AiFolder, ModelFileName);

		/// <summary>Kept separate from <see cref="AiFolder"/>, not swapped in-place over it: both the
		/// CPU and DirectML native builds are files literally named <c>onnxruntime.dll</c> on
		/// Windows, so keeping them in different folders means switching <c>--hardware-accel</c>
		/// between runs never has to re-download/overwrite the other flavor.</summary>
		public static string DirectMlFolder => Path.Combine(AiFolder, "directml");

		/// <summary>
		/// Test hook: points ModelPath at the checked-in tiny embedder and makes
		/// EnsureReady succeed, so suites exercise the AI pipeline without downloads
		/// (the native runtime comes from the test projects' full OnnxRuntime package).
		/// </summary>
		internal static string? TestOverrideModelPath;

		static bool resolverInstalled;
		static readonly object resolverLock = new();

		/// <param name="preferDirectML">When true, checks <see cref="DirectMlFolder"/> against
		/// <see cref="DirectMlRuntimeVersion"/> instead of <see cref="AiFolder"/>/<see cref="RuntimeVersion"/>
		/// — the two runtime flavors are entirely independent on disk (docs/decisions/0013-gpu-acceleration.md).</param>
		public static AiComponentsState GetState(bool preferDirectML = false) {
			string folder = preferDirectML ? DirectMlFolder : AiFolder;
			string version = preferDirectML ? DirectMlRuntimeVersion : RuntimeVersion;
			bool runtime = FindRuntimeLibrary(folder) != null && HasCurrentRuntimeVersion(folder, version);
			bool model = File.Exists(ModelPath);
			if (runtime && model) return AiComponentsState.Ready;
			if (runtime) return AiComponentsState.ModelMissing;
			if (model) return AiComponentsState.RuntimeMissing;
			return AiComponentsState.Missing;
		}

		public static bool IsReady => GetState() == AiComponentsState.Ready;

		/// <summary>True when the separately-downloaded DirectML runtime (not the default CPU one)
		/// is present and current. Independent of <see cref="IsReady"/>/<see cref="GetState()"/>,
		/// which always check the CPU flavor.</summary>
		public static bool IsDirectMlReady => GetState(preferDirectML: true) == AiComponentsState.Ready;

		/// <summary>Throws with an actionable message when the components are not present.</summary>
		/// <param name="preferDirectML">Checks <see cref="DirectMlFolder"/>/<see cref="DirectMlRuntimeVersion"/>
		/// instead of the CPU flavor — the two are entirely independent, so this must match whatever
		/// flavor the caller actually intends to construct an <c>OnnxEmbedder</c> against.</param>
		public static void EnsureReady(bool preferDirectML = false) {
			if (TestOverrideModelPath != null)
				return;
			AiComponentsState state = GetState(preferDirectML);
			if (state == AiComponentsState.Ready) return;
			string folder = preferDirectML ? DirectMlFolder : AiFolder;
			string version = preferDirectML ? DirectMlRuntimeVersion : RuntimeVersion;
			throw new InvalidOperationException(
				$"AI matching components are not available ({state}). " +
				$"Download them in Settings → Matching, or place onnxruntime {version} and {ModelFileName} into '{folder}'.");
		}

		static bool HasCurrentRuntimeVersion(string folder, string version) {
			try {
				string marker = Path.Combine(folder, VersionMarkerFileName);
				return File.Exists(marker) && File.ReadAllText(marker).Trim() == version;
			}
			catch { return false; }
		}

		/// <summary>The downloaded ONNX Runtime library file in <paramref name="folder"/>, or null
		/// when absent. Same lookup for either runtime flavor — <see cref="AiFolder"/> (CPU) or
		/// <see cref="DirectMlFolder"/> (DirectML) — since both produce a same-named main library
		/// (just different capabilities inside it), only ever one flavor per folder.</summary>
		internal static string? FindRuntimeLibrary(string folder) {
			try {
				if (!Directory.Exists(folder)) return null;
				// Windows: onnxruntime.dll — Linux: libonnxruntime.so.<ver> — macOS: libonnxruntime.<ver>.dylib
				return Directory.EnumerateFiles(folder)
					.Where(f => {
						string name = Path.GetFileName(f);
						return name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) &&
							!name.Contains("providers", StringComparison.OrdinalIgnoreCase) &&
							(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
							 name.Contains(".so", StringComparison.OrdinalIgnoreCase) ||
							 name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase));
					})
					.OrderByDescending(f => Path.GetFileName(f).Length) // prefer the fully-versioned real file over a bare symlink copy
					.FirstOrDefault();
			}
			catch { return null; }
		}

		/// <summary>Convenience overload defaulting to the CPU runtime's own <see cref="AiFolder"/>
		/// — the shape every pre-existing caller (and test) already uses.</summary>
		internal static string? FindRuntimeLibrary() => FindRuntimeLibrary(AiFolder);

		// Read by the resolver callback below at EVERY native call, not just at registration time
		// (SetDllImportResolver can only be installed once per assembly, but the closure it
		// installs can still read mutable static state on each invocation) — set from whichever
		// flavor OnnxEmbedder's constructor most recently asked for. Safe in practice because one
		// `vbr`/VDF process only ever runs with one hardware-accel preference for its whole
		// lifetime (docs/decisions/0013-gpu-acceleration.md).
		static bool preferDirectMLRuntime;

		/// <summary>
		/// Routes the Managed wrapper's "onnxruntime" DllImport to the downloaded library --
		/// <see cref="DirectMlFolder"/>'s when <paramref name="preferDirectML"/> is requested and
		/// that runtime is actually present there, else <see cref="AiFolder"/>'s CPU build (the
		/// original, unconditional behavior). Must run before the first OnnxRuntime native call
		/// (OnnxEmbedder does so). SetDllImportResolver may only be called once per assembly, hence
		/// the guard -- <paramref name="preferDirectML"/> is still recorded on every call, since the
		/// callback itself reads it fresh each time regardless of whether this particular call
		/// actually performed the one-time registration.
		/// </summary>
		internal static void EnsureResolverInstalled(bool preferDirectML = false) {
			preferDirectMLRuntime = preferDirectML;
			if (resolverInstalled) return;
			lock (resolverLock) {
				if (resolverInstalled) return;
				NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) => {
					if (!name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
						return IntPtr.Zero;
					string? lib = preferDirectMLRuntime ? FindRuntimeLibrary(DirectMlFolder) : null;
					lib ??= FindRuntimeLibrary(AiFolder);
					if (lib != null && NativeLibrary.TryLoad(lib, out IntPtr handle))
						return handle;
					return IntPtr.Zero; // fall through to default probing (PATH / app dir)
				});
				resolverInstalled = true;
			}
		}

		internal static (Uri Url, string ArchiveFileName) GetRuntimeDownloadPlan() {
			string os =
				RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
				RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
				RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
				throw new PlatformNotSupportedException("AI matching is not supported on this operating system.");
			string arch = RuntimeInformation.ProcessArchitecture switch {
				Architecture.X64 => os == "osx" ? "x86_64" : "x64",
				Architecture.Arm64 => os == "linux" ? "aarch64" : "arm64",
				_ => throw new PlatformNotSupportedException($"AI matching is not supported on {RuntimeInformation.ProcessArchitecture}.")
			};
			string ext = os == "win" ? "zip" : "tgz";
			string file = $"onnxruntime-{os}-{arch}-{RuntimeVersion}.{ext}";
			return (new Uri($"https://github.com/microsoft/onnxruntime/releases/download/v{RuntimeVersion}/{file}"), file);
		}

		/// <summary>Downloads whatever is missing (runtime and/or model). Safe to call when Ready.</summary>
		/// <param name="preferDirectML">When true, also ensures the separately-versioned DirectML
		/// runtime (<see cref="DirectMlFolder"/>) is present, alongside the model -- the CPU runtime
		/// is never downloaded in this mode (nothing needs it if DirectML is what's being
		/// requested). Windows-only; throws <see cref="PlatformNotSupportedException"/> elsewhere,
		/// same as the CPU path already does for an unsupported OS/architecture.</param>
		public static async Task DownloadAsync(IProgress<AiDownloadProgress>? progress, CancellationToken token, bool preferDirectML = false) {
			Directory.CreateDirectory(AiFolder);
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

			var downloads = new List<Func<CancellationToken, Task>>(2);
			if (preferDirectML) {
				if (FindRuntimeLibrary(DirectMlFolder) == null || !HasCurrentRuntimeVersion(DirectMlFolder, DirectMlRuntimeVersion))
					downloads.Add(ct => DownloadDirectMlRuntimeAsync(http, progress, ct));
			}
			else if (FindRuntimeLibrary() == null || !HasCurrentRuntimeVersion(AiFolder, RuntimeVersion)) {
				downloads.Add(ct => DownloadRuntimeAsync(http, progress, ct));
			}
			if (!File.Exists(ModelPath))
				downloads.Add(ct => DownloadModelAsync(http, progress, ct));
			// The archives come from different hosts (onnxruntime GitHub release or NuGet vs.
			// the model mirror), so fetching them concurrently roughly halves the first-use wait.
			// Consumers already key progress off AiDownloadProgress.Step.
			await RunDownloadsAsync(downloads, token);
		}

		/// <summary>
		/// Runs the given downloads concurrently. The first failure cancels the
		/// remaining downloads (no point finishing a 100 MB sibling once the feature
		/// cannot become ready) and its exception surfaces — specifically NOT the
		/// sibling's induced OperationCanceledException, which Task.WhenAll would
		/// otherwise rethrow when the canceled task comes first in the list.
		/// </summary>
		internal static async Task RunDownloadsAsync(IReadOnlyList<Func<CancellationToken, Task>> downloads, CancellationToken token) {
			if (downloads.Count == 0)
				return;
			if (downloads.Count == 1) {
				await downloads[0](token);
				return;
			}
			using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			List<Task> tasks = downloads.Select(RunOne).ToList();
			async Task RunOne(Func<CancellationToken, Task> download) {
				try {
					await download(linkedCts.Token);
				}
				catch {
					linkedCts.Cancel();
					throw;
				}
			}
			try {
				await Task.WhenAll(tasks);
			}
			catch when (!token.IsCancellationRequested) {
				foreach (Task t in tasks) {
					Exception? real = t.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException);
					if (real != null)
						System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(real).Throw();
				}
				throw;
			}
		}

		static async Task DownloadRuntimeAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			(Uri url, string archiveName) = GetRuntimeDownloadPlan();
			// Per-attempt temp dir: a fixed shared path let two VDF processes (GUI + CLI,
			// or Web + CLI in one container) clobber each other's in-progress download —
			// FileShare.None collisions, or one process's cleanup deleting the other's files.
			string tempRoot = Path.Combine(Path.GetTempPath(), $"VDF.AiDownload.{Guid.NewGuid():N}");
			Directory.CreateDirectory(tempRoot);
			string archivePath = Path.Combine(tempRoot, archiveName);
			try {
				string runtimeStep = $"ONNX Runtime {RuntimeVersion}";
				await DownloadUtils.DownloadFileAsync(http, url, archivePath, runtimeStep,
					(done, total) => progress?.Report(new AiDownloadProgress(runtimeStep, done, total)), token);

				string extractDir = Path.Combine(tempRoot, "extracted");
				Directory.CreateDirectory(extractDir);
				ArchiveUtils.Extract(archivePath, extractDir,
					archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ArchiveKind.Zip : ArchiveKind.TarGz);

				// Purge any previously installed runtime BEFORE copying: the Linux/macOS
				// library names carry the version (libonnxruntime.so.1.23.2), so upgraded
				// installs would otherwise accumulate versions and FindRuntimeLibrary's
				// tie-break could keep loading the stale one while the marker claims the
				// new version — with GetState() reporting Ready, only deleting the ai
				// folder by hand would have recovered.
				foreach (string old in Directory.EnumerateFiles(AiFolder)) {
					string oldName = Path.GetFileName(old);
					if (oldName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
						try { File.Delete(old); } catch { /* loaded/locked — overwritten below */ }
				}

				// Archives lay out onnxruntime-<rid>-<ver>/lib/<libraries>; flatten lib/ into AiFolder.
				string? libDir = Directory.EnumerateDirectories(extractDir, "lib", SearchOption.AllDirectories).FirstOrDefault();
				if (libDir == null)
					throw new IOException($"Downloaded ONNX Runtime archive has no lib/ directory ({archiveName}).");
				foreach (string file in Directory.EnumerateFiles(libDir)) {
					string name = Path.GetFileName(file);
					if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
					File.Copy(file, Path.Combine(AiFolder, name), overwrite: true);
				}
				await File.WriteAllTextAsync(Path.Combine(AiFolder, VersionMarkerFileName), RuntimeVersion, token);
				if (FindRuntimeLibrary() == null)
					throw new IOException("ONNX Runtime archive extracted but no runtime library was found in it.");
			}
			finally {
				try { Directory.Delete(tempRoot, true); } catch { }
			}
		}

		/// <summary>
		/// Acquires the DirectML-enabled native runtime — a different distribution mechanism from
		/// <see cref="DownloadRuntimeAsync"/>'s GitHub-release archive, since
		/// <c>microsoft/onnxruntime</c>'s GitHub releases only ship CPU and CUDA/TensorRT ("gpu")
		/// Windows builds, never a DirectML one (confirmed against the actual v1.23.2 release asset
		/// listing). DirectML-enabled binaries are only published via NuGet
		/// (<c>Microsoft.ML.OnnxRuntime.DirectML</c>, which itself depends on
		/// <c>Microsoft.AI.DirectML</c> for <c>DirectML.dll</c>) — a <c>.nupkg</c> is a zip, so both
		/// are downloaded via NuGet's direct-package-download endpoint and extracted the same way
		/// the GitHub archive is, pulling each package's own <c>runtimes/win-x64/native/</c>
		/// contents into <see cref="DirectMlFolder"/>. Windows x64 only for now — DirectML on other
		/// architectures (win-arm64) isn't attempted (docs/decisions/0013-gpu-acceleration.md).
		/// </summary>
		/// <exception cref="PlatformNotSupportedException">Not Windows x64.</exception>
		static async Task DownloadDirectMlRuntimeAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
				throw new PlatformNotSupportedException("DirectML acceleration is only supported on Windows x64.");

			Directory.CreateDirectory(DirectMlFolder);
			string tempRoot = Path.Combine(Path.GetTempPath(), $"VDF.AiDownload.{Guid.NewGuid():N}");
			Directory.CreateDirectory(tempRoot);
			try {
				string ortStep = $"ONNX Runtime DirectML {DirectMlRuntimeVersion}";
				string ortPackagePath = Path.Combine(tempRoot, "ort-directml.nupkg");
				await DownloadUtils.DownloadFileAsync(http,
					new Uri($"https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.DirectML/{DirectMlRuntimeVersion}"),
					ortPackagePath, ortStep, (done, total) => progress?.Report(new AiDownloadProgress(ortStep, done, total)), token);

				string dmlStep = $"DirectML {DirectMlRedistVersion}";
				string dmlPackagePath = Path.Combine(tempRoot, "directml.nupkg");
				await DownloadUtils.DownloadFileAsync(http,
					new Uri($"https://www.nuget.org/api/v2/package/Microsoft.AI.DirectML/{DirectMlRedistVersion}"),
					dmlPackagePath, dmlStep, (done, total) => progress?.Report(new AiDownloadProgress(dmlStep, done, total)), token);

				string extractDir = Path.Combine(tempRoot, "extracted");
				string ortExtractDir = Path.Combine(extractDir, "ort");
				string dmlExtractDir = Path.Combine(extractDir, "dml");
				Directory.CreateDirectory(ortExtractDir);
				Directory.CreateDirectory(dmlExtractDir);
				ArchiveUtils.Extract(ortPackagePath, ortExtractDir, ArchiveKind.Zip);
				ArchiveUtils.Extract(dmlPackagePath, dmlExtractDir, ArchiveKind.Zip);

				// Purge any previously installed DirectML runtime before copying -- same
				// stale-file-accumulation rationale DownloadRuntimeAsync's own purge step documents.
				foreach (string old in Directory.EnumerateFiles(DirectMlFolder)) {
					string oldName = Path.GetFileName(old);
					if (oldName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
						try { File.Delete(old); } catch { /* loaded/locked — overwritten below */ }
				}

				// The two packages use different native-layout conventions -- confirmed by
				// downloading and inspecting both real .nupkg files directly (nuget.org's own page
				// for the *package*, not the file layout inside it, doesn't show this):
				// Microsoft.ML.OnnxRuntime.DirectML follows the standard runtimes/<rid>/native/
				// convention every Microsoft.ML.OnnxRuntime.* package uses; Microsoft.AI.DirectML is
				// a native SDK-style package (MSBuild .targets/.props-driven) using bin/<arch>-win/
				// instead, alongside .lib/.pdb/Debug-build files this project has no use for.
				string ortNativeDir = Path.Combine(ortExtractDir, "runtimes", "win-x64", "native");
				if (!Directory.Exists(ortNativeDir))
					throw new IOException("Downloaded Microsoft.ML.OnnxRuntime.DirectML package has no runtimes/win-x64/native/ directory.");
				foreach (string file in Directory.EnumerateFiles(ortNativeDir))
					File.Copy(file, Path.Combine(DirectMlFolder, Path.GetFileName(file)), overwrite: true);

				string dmlDllPath = Path.Combine(dmlExtractDir, "bin", "x64-win", "DirectML.dll");
				if (!File.Exists(dmlDllPath))
					throw new IOException("Downloaded Microsoft.AI.DirectML package has no bin/x64-win/DirectML.dll.");
				File.Copy(dmlDllPath, Path.Combine(DirectMlFolder, "DirectML.dll"), overwrite: true);

				await File.WriteAllTextAsync(Path.Combine(DirectMlFolder, VersionMarkerFileName), DirectMlRuntimeVersion, token);
				if (FindRuntimeLibrary(DirectMlFolder) == null)
					throw new IOException("DirectML ONNX Runtime package extracted but no runtime library was found in it.");
			}
			finally {
				try { Directory.Delete(tempRoot, true); } catch { }
			}
		}

		static async Task DownloadModelAsync(HttpClient http, IProgress<AiDownloadProgress>? progress, CancellationToken token) {
			// Unique temp name for the same two-process reason as the runtime download;
			// the final File.Move is atomic either way.
			string tempPath = ModelPath + $".{Guid.NewGuid():N}.download";
			Action<long, long?> onProgress = (done, total) => progress?.Report(new AiDownloadProgress("AI model", done, total));
			try {
				try {
					await DownloadUtils.DownloadFileAsync(http, new Uri(ModelPrimaryUrl), tempPath, "AI model", onProgress, token);
				}
				catch (Exception e) when (e is not OperationCanceledException) {
					Logger.Instance.Info($"AI model mirror unavailable ({e.Message}), falling back to upstream source.");
					await DownloadUtils.DownloadFileAsync(http, new Uri(ModelFallbackUrl), tempPath, "AI model", onProgress, token);
				}

				string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tempPath, token)));
				if (!hash.Equals(ModelSha256, StringComparison.OrdinalIgnoreCase))
					throw new IOException($"AI model download failed the integrity check (SHA256 {hash}, expected {ModelSha256}).");
				File.Move(tempPath, ModelPath, overwrite: true);
			}
			finally {
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
			}
		}

	}
}
