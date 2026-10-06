# BorgStudio

All-in-one app to manage BorgBackup repositories – create, schedule and upload backups, browse archives and restore files.

Cross-platform desktop app (Windows, Linux, macOS) built with .NET 10 and [Avalonia UI](https://avaloniaui.net/).

## Project structure

```
BorgStudio.slnx
├── src/
│   ├── BorgStudio.App     Avalonia desktop UI (MVVM, CommunityToolkit.Mvvm)
│   └── BorgStudio.Core    UI-independent logic: borg CLI, repositories, schedules
└── tests/
    └── BorgStudio.Tests   xUnit tests for Core
```

Shared build settings (target framework, nullable, version) live in `Directory.Build.props`.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [BorgBackup](https://www.borgbackup.org/) installed and on `PATH` (1.x; 2.x is detected separately)

## Build, test, run

```bash
dotnet build
dotnet test
dotnet run --project src/BorgStudio.App
```
