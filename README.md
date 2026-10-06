# BorgStudio

All-in-one app to manage BorgBackup repositories – create, schedule and upload backups, browse archives and restore files.

Cross-platform desktop app (Windows, Linux, macOS) built with .NET 10 and [Avalonia UI](https://avaloniaui.net/).

## Download

Portable builds for Windows (x64), macOS (Apple Silicon and Intel) and Linux (x64) are on the
[Releases](https://github.com/cuban8IO/borgstudio/releases) page. They include the .NET runtime:
unpack and start, no installation needed.

[BorgBackup](https://www.borgbackup.org/) 1.2 or newer (1.4 recommended) must be installed separately.
BorgStudio finds it on `PATH` and in the usual install locations (e.g. Homebrew); on Windows it also
looks for borg inside WSL. borg 2 is still in beta and not supported yet. The UI is available in English and German.

## Project structure

```
BorgStudio.slnx
├── src/
│   ├── BorgStudio.App                  Avalonia desktop UI (MVVM, CommunityToolkit.Mvvm)
│   ├── BorgStudio.Core                 UI-independent logic: borg CLI, plugins, repositories
│   ├── BorgStudio.Plugins.Abstractions Public plugin API (repository providers)
│   └── BorgStudio.Providers.Local      Built-in provider: local folder
├── samples/
│   └── BorgStudio.SamplePlugin         Example external plugin (not part of the release)
├── tests/
│   └── BorgStudio.Tests                xUnit tests
├── docs/                               Developer documentation
├── build/                              Packaging assets (macOS Info.plist)
└── .github/                            CI, packaging and release workflows
```

Shared build settings (target framework, nullable, version) live in `Directory.Build.props`.

## Plugins

Repository providers (local folder, SSH server, storage services, …) are plugins. Built-in providers use the same
API as external ones; see [docs/plugins.md](docs/plugins.md) for writing and installing a plugin.

## Build, test, run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, to actually use the app,
BorgBackup (see above). UI texts live in `src/BorgStudio.App/Resources/Strings*.resx`.

```bash
dotnet build
dotnet test
dotnet run --project src/BorgStudio.App
```

Portable package for one platform, e.g. Linux:

```bash
dotnet publish src/BorgStudio.App -c Release -r linux-x64 -o publish
```

## Branches and releases

| Branch | Purpose |
|---|---|
| `feature/*` | a new feature, merged into `dev` via pull request |
| `fix/*` | a bug fix, merged into `dev` via pull request |
| `dev` | integration branch for the next release |
| `main` | releases only – every push to `main` publishes a release |

Release versions are calculated automatically from the last `vX.Y.Z` tag: if any `feature/*` branch was merged
since then, the minor version goes up (0.**x**.0), otherwise the patch version (0.0.**x**).
