# Development setup

How to build and run this project (a fork of Video Duplicate Finder, C#/.NET). You do **not**
need full Visual Studio — VS Code + the .NET SDK is enough.

## Prerequisites

- **.NET 10 SDK** — the only hard requirement. Standalone download from
  <https://dotnet.microsoft.com/download>. Every project targets `net10.0`; the SDK includes
  MSBuild, the compilers, and the ASP.NET Core bits needed by `VDF.Web` (there is no "workload"
  concept outside the Visual Studio installer).
- **VS Code** with the **C# Dev Kit** extension recommended (adds `.sln` / multi-project
  support and a test runner; the solution has ~a dozen projects). The plain **C#** extension
  also works.
- **Git** — already in use; see [`AGENTS.md`](../AGENTS.md) for the git workflow (maintainer
  runs all git commands).
- **FFmpeg / FFprobe** — a **runtime** dependency, not a build one. VDF auto-downloads them on
  first launch; you can also put them on `PATH`.

## Build & run (from the repo root)

```sh
dotnet build VideoDuplicateFinder.sln     # build everything
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

## Recommended VS Code version

- Use a current VS Code with the C# Dev Kit updated for **.NET 10**. Confirm the SDK is picked
  up with `dotnet --version` (expect a `10.x` value).
