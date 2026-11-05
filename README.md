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

## Your data

- **Repository list:** `repositories.json` in the BorgStudio data folder (Windows `%LOCALAPPDATA%\BorgStudio`,
  macOS `~/Library/Application Support/BorgStudio`, Linux `~/.local/share/BorgStudio`). It contains names and
  locations, never passphrases.
- **Passphrases:** per repository either stored in BorgStudio's vault, in the system keychain (Windows Credential
  Manager, macOS keychain, Linux Secret Service such as GNOME Keyring or KWallet) or asked for every time.
- **Vault:** `vault.json` in the data folder, encrypted with AES-256-GCM under a key derived from your PIN with
  Argon2id – without the PIN nothing in it can be read. The PIN is asked for once per session; by default the vault
  locks itself again after 15 minutes without using the app (Tools → Settings). A forgotten PIN cannot be recovered:
  the vault can only be reset, losing what is stored in it.
- **Settings:** `settings.json` in the data folder.
- **Repository keys:** BorgStudio offers to export the key after creating a repository. Keep the key file and the
  passphrase safe and separate – without them, backups cannot be restored.
- **SSH:** login keys created by BorgStudio (one per repository) and the confirmed server host keys
  (`known_hosts`) are kept in the `ssh` sub folder of the data folder. Server passwords are only used once to
  install a key and are never stored.
- **Hetzner Storage Box:** connects on port 23 with the borg version you choose (1.4 or 1.2). The server's host key
  is checked against the fingerprints Hetzner publishes. Restricted login keys are written to
  `.ssh/authorized_keys` on the box over SFTP; unrestricted ones are installed with Hetzner's `install-ssh-key`.

## Project structure

```
BorgStudio.slnx
├── src/
│   ├── BorgStudio.App                  Avalonia desktop UI (MVVM, CommunityToolkit.Mvvm)
│   ├── BorgStudio.Core                 UI-independent logic: borg CLI, plugins, repositories
│   ├── BorgStudio.Plugins.Abstractions Public plugin API (repository providers)
│   ├── BorgStudio.Providers.Local      Built-in provider: local folder
│   ├── BorgStudio.Providers.Ssh        Built-in provider: SSH server with borg
│   └── BorgStudio.Providers.Hetzner    Built-in provider: Hetzner Storage Box
├── samples/
│   └── BorgStudio.SamplePlugin         Example external plugin (not part of the release)
├── tests/
│   └── BorgStudio.Tests                xUnit tests
├── docs/                               Developer documentation
├── build/                              Packaging assets (app icon source, macOS Info.plist and .icns)
└── .github/                            CI, packaging and release workflows
```

Shared build settings (target framework, nullable, version) live in `Directory.Build.props`.

## Plugins

Repository providers (local folder, SSH server, Hetzner Storage Box, …) are plugins. Built-in providers use the same
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
Every pull request adds its change to the **Unreleased** section of the changelog below; when releasing, that
section gets the new version number.

## Changelog

### Unreleased

**Added**
- Detects an installed borg on startup: on `PATH`, in the usual install locations and, on Windows, inside WSL.
- Plugin system for repository providers, with a window listing the loaded plugins.
- Local repositories: connect an existing one or create a new one, keep the passphrase in the system keychain or
  ask for it every time, export the repository key.
- SSH server provider: confirms the server's host key and creates and installs a login key per repository
  (also when borg runs inside WSL).
- Hetzner Storage Box provider: port 23, host key checked against Hetzner's published fingerprints, choice of
  borg 1.4 or 1.2 on the box.
- App icon and the app version in the window title.

- PIN-protected vault for repository passphrases (Argon2id + AES-256-GCM), unlocked once per session, with an
  automatic lock after a configurable idle time (default 15 minutes). New Settings window (Tools → Settings) to set
  up, change or reset the PIN. Each repository keeps its passphrase in the vault, in the system keychain or not at all.

**Changed**
- Dark design in the Beatport palette: dark ground, neon green for primary buttons, the selected list entry, progress
  bars, check boxes and success messages.

**Fixed**
- The add/edit repository dialog now closes after a successful add or save.

### 0.1.0 – 2026-10-06

**Added**
- Release pipeline: portable, self-contained packages for Windows (x64), macOS (Apple Silicon and Intel) and
  Linux (x64), published on every push to `main`.
