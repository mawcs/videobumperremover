# Development setup

How to build and run this project (a fork of Video Duplicate Finder, C#/.NET). You do **not**
need full Visual Studio — VS Code + the .NET SDK is enough.

## Prerequisites

- **.NET 10 SDK** — the only hard requirement. Standalone download from
  <https://dotnet.microsoft.com/download>. Every project targets `net10.0`; the SDK includes
  MSBuild, the compilers, and the ASP.NET Core bits needed by `VDF.Web` (there is no "workload"
  concept outside the Visual Studio installer).
- **VS Code** with the base **C#** extension (`ms-dotnettools.csharp`) — IntelliSense +
  debugging, **no account required**. This is all you need in the editor; builds/tests run from
  the `dotnet` CLI regardless.
  - **C# Dev Kit is optional and NOT recommended for this project.** It's Microsoft's
    proprietary extension and forces a **Microsoft account sign-in** (which can fail on the
    passkey/WebAuthn step in VS Code's embedded browser). It only adds Solution/Test Explorer
    *UI panels* — not needed here. If you installed it and hit the login wall, disable/uninstall
    `ms-dotnettools.csdevkit` and keep the plain C# extension.
  - Full Visual Studio is also unnecessary and would similarly want a Microsoft account.
- **Git** — already in use; see [`AGENTS.md`](../AGENTS.md) for the git workflow (maintainer
  runs all git commands).
- **FFmpeg / FFprobe** — a **runtime** dependency, not a build one. VDF auto-downloads them on
  first launch; you can also put them on `PATH`.

## Build & run (from the repo root)

```sh
dotnet build VideoBumperRemover.sln       # build everything
dotnet run --project VDF.GUI              # run the Avalonia desktop app
dotnet run --project VDF.CLI -- --help    # run the CLI
dotnet run --project VDF.Web              # run the web UI
dotnet test                               # run the test suite
```

## Editor / IDE notes

- **Visual Studio is optional.** The VDF README recommends it, but the whole solution builds
  and runs from the `dotnet` CLI — nothing here requires it.
- **Avalonia XAML previewer:** the live in-editor previewer is a Visual Studio / JetBrains
  Rider strength; VS Code's Avalonia support is thinner. In VS Code you build-and-run the app
  to see UI changes. If the live previewer becomes important for UI work, VS 2026 or Rider are
  the fallback — but it is not required.

## Native AOT publishing (release builds only)

- `VDF.GUI` is configured for **Native AOT** release builds. AOT compilation on Windows invokes
  the native linker, which needs the **MSVC C++ build tools**. The standalone
  **"Build Tools for Visual Studio"** with the *Desktop development with C++* workload is
  sufficient — full Visual Studio is not required.
- This is needed **only** for an AOT `dotnet publish`. Normal debug/dev builds and
  `dotnet run` are plain JIT and need none of it. Install the C++ build tools only when you
  actually want to produce an AOT release binary.

## Troubleshooting

**`NU1100: Unable to resolve '<package>' for 'net10.0'` for *every* package.** NuGet has no
usable package source. Check with `dotnet nuget list source`; if it prints **"No sources
found"** (a machine with no NuGet config at all), add the default feed once:

```sh
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
dotnet restore VideoBumperRemover.sln
```

If nuget.org is listed but `[Disabled]`, run `dotnet nuget enable source nuget.org`. The first
restore downloads a lot (Avalonia, ONNX Runtime, FFmpeg.AutoGen, …) and can take ~a minute.

**Pre-existing test warnings.** The build emits a few `xUnit1031` warnings from VDF's own test
code (`VDF.Core.Tests`). They're upstream's, harmless, and intentionally left untouched to
avoid merge friction with upstream.

## Recommended VS Code version

- Use a current VS Code with the C# Dev Kit updated for **.NET 10**. Confirm the SDK is picked
  up with `dotnet --version` (expect a `10.x` value).
