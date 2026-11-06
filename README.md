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
- **Vault and app lock:** `vault.json` in the data folder, encrypted with AES-256-GCM under a key derived from your
  PIN with Argon2id – without the PIN nothing in it can be read. With a PIN, BorgStudio starts locked and shows only
  a lock screen; unlocking also opens the vault. By default it locks itself again after 15 minutes without use
  (Tools → Settings), or at once with Ctrl+L. A forgotten PIN cannot be recovered:
  the vault can only be reset, losing what is stored in it.
- **Settings:** `settings.json` in the data folder.
- **Archive cache:** `archives/` in the data folder – per repository the archives last seen with their sizes, so the
  archives tab opens at once. Encrypted with the vault's key (`<id>.vault`) when a PIN is set up, plain JSON
  (`<id>.json`) otherwise; safe to delete.
- **Backup jobs:** `jobs.json` in the data folder – per job the repository, the source folders, what to leave out,
  the compression and the outcome of the last run. Archives are named `<job>-<computer>-<date and time>`.- **Moving to another computer:** Tools → Export settings writes everything – repositories, stored passphrases
  (vault and keychain), SSH keys, known servers, settings and, if wanted, the borg keys of the repositories – into one
  `.borgstudio` file, encrypted with a password of its own (at least 12 characters; Argon2id + AES-256-GCM). Tools →
  Import settings shows what the file contains and merges it (asking per existing entry) or replaces everything;
  keyfile keys are put back into borg's key folder. External plugins are not part of an export.- **Repository keys:** BorgStudio offers to export the key after creating a repository. Keep the key file and the
  passphrase safe and separate – without them, backups cannot be restored.
- **SSH keys:** kept encrypted in the vault (Tools → SSH keys), so SSH repositories need a PIN. One key can serve
  several repositories. Imported key files are copied (the original stays; a password on it is removed once, the
  vault protects the key). For a borg call the key is written to a private temporary file – in the `ssh` sub folder
  of the data folder, or in `~/.borgstudio/ssh` inside WSL – and deleted right after. The confirmed server host keys
  (`known_hosts`) are kept in the `ssh` sub folder. Server passwords are only used once to install a key and are
  never stored. Key files of older versions are moved into the vault after unlocking.
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

- App lock with a PIN: BorgStudio starts locked and shows a lock screen; unlocking also opens a PIN-protected vault
  for repository passphrases (Argon2id + AES-256-GCM). Locks again after a configurable idle time (default
  15 minutes) or with Ctrl+L. New Settings window (Tools → Settings) to set
  up, change or reset the PIN. Each repository keeps its passphrase in the vault, in the system keychain or not at all.
- Central SSH key management (Tools → SSH keys): keys are kept encrypted in the vault and can be shared by several
  repositories; create, import (password-protected keys too), rename, copy the public key, save the private key,
  delete unused keys. In a repository, pick a key from the list and optionally install it on the server. Key files of
  older versions move into the vault automatically; borg only gets a key as a temporary file for the duration of a call.
- Export and import of all settings (Tools → Export/Import settings): one file encrypted with its own password, with
  repositories, passphrases, SSH keys, known servers, settings and the borg keys; importing merges (with a choice per
  existing entry) or replaces, sets up a PIN on a new computer and restores keyfile keys for borg.
- Backup jobs (repository → tab "Backup jobs"): source folders, what to leave out (caches/temp, build folders,
  recycle bins, CACHEDIR.TAG, file extensions, folders, own borg patterns) and compression per job; several jobs per
  repository. "Back up now" shows live progress with upload speed, elapsed and estimated remaining time; cancelling
  stops borg cleanly and keeps a checkpoint the next run builds on. Files borg could not read are listed as warnings.
  The repository details are now organized in tabs. Jobs are part of exports.
- Archive list (repository → tab "Archives"): all archives newest first with creation time and the job they come
  from; duration, size, own share (what only this archive takes up in the repository) and file count for the newest
  20, more on request. A finished backup shows up right away. The archives are cached per repository (encrypted with
  the vault's key when a PIN is set up): the tab shows them at once and borg is only asked for archives it has not
  described yet, since `borg info` reads all metadata of an archive.
- Cleaning up old archives (`borg prune` + `borg compact`): each backup job can keep a number of daily, weekly,
  monthly and yearly archives (plus hourly, the newest N and everything from the last N days); off for new jobs.
  After each successful backup the job's other archives are deleted and the space is freed. "Clean up…" in the jobs
  tab shows first which archives would go and which stay (and by which rule). Only the archives this computer created
  for the job are touched (`<job>-{hostname}-*`). "Free space" in the overview runs `borg compact` on its own, with
  progress and cancel.

**Changed**
- Dark design in the Beatport palette: dark ground, neon green for primary buttons, the selected list entry, progress
  bars, check boxes and success messages.

**Fixed**
- The add/edit repository dialog now closes after a successful add or save.

### 0.1.0 – 2026-10-06

**Added**
- Release pipeline: portable, self-contained packages for Windows (x64), macOS (Apple Silicon and Intel) and
  Linux (x64), published on every push to `main`.
