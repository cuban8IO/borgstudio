# BorgStudio

All-in-one app to manage BorgBackup repositories – create and upload backups, clean up old archives, browse
archives and restore files.

Cross-platform desktop app (Windows, Linux, macOS) built with .NET 10 and [Avalonia UI](https://avaloniaui.net/).
The **[product page](https://borgstudio.cuban8io.net/)** shows what it does, the
**[wiki](https://github.com/cuban8IO/borgstudio/wiki)** explains every function step by step.

## Features

- **Repositories** in a local folder, on a USB or external drive, on a network disk (iSCSI, NVMe/TCP), on an SSH server,
  on an SFTP-only server, on a Hetzner Storage Box, at rsync.net or at BorgBase –
  create new ones or connect existing ones; borg runs natively or, on Windows, inside WSL.
- **Backup jobs** with source folders – or a removable drive (USB stick, external disk, memory card), recognized
  wherever it is attached – exclusions and compression; live progress with upload speed and remaining time; cancelling
  keeps a checkpoint.
- **Commands before and after** a job's backup (PowerShell, cmd, sh, bash, zsh), an **upload limit** and **low
  priority** per repository, and **conditions** for scheduled runs (mains power, no metered connection, cable, target
  reachable).
- **Trust in backups**: integrity checks (`borg check`, also thorough) by hand, after backups or by schedule; restore
  tests that restore a sample and compare it with the source; releasing a lock left behind.
- **Running tasks** in the status bar: backups, checks, restores, exports, searches and downloads with their progress
  and the time still to go, queued and held back runs, and runs the system's task scheduler started – each with *Show*
  and, where possible, *Cancel*. The windows of longer work (archive browser, search, versions, comparison, export,
  update) leave the main window usable.
- **Schedules**: every few minutes or hours, daily, on chosen weekdays or monthly – started by the system's task scheduler (also while
  BorgStudio is closed) or by BorgStudio itself in the background (notification area, start at login); missed times
  are made up for. Other programs can start jobs through an incoming webhook.
- **Notifications** by system notification, e-mail and webhook (ntfy, Discord, Slack, Gotify, JSON) – failures,
  warnings, full storage, overdue jobs, and a daily summary.
- **Statistics**: an overview of all repositories (state, storage, success rate), and per repository the storage over
  time with a forecast of when it will be full, what each backup newly stored and the activity of the last weeks.
- **Cleaning up** old archives per job (daily, weekly, monthly, yearly …) with a preview, and freeing space.
- **Archives**: list with size and own share, a browser with folder tree and search, search across all archives and
  the versions of a file; compare two archives; rename, comment, keep and delete archives; export an archive or a
  selection as zip, tar, tar.gz or tar.zst.
- **Restore** files and folders into a folder or back to their original place, with a check for files that would be
  overwritten.
- **Mount** an archive or the whole repository as a read-only folder and open it in the file manager.
- **Security**: app lock with a PIN and an encrypted vault for passphrases and SSH keys, or the system keychain;
  export and import of all settings in one encrypted file.
- **Updates** from within the app, checked against the release's checksums; UI in English and German.

## No warranty

BorgStudio is free and provided **without any warranty**, as far as the law allows. It runs the well-proven borg
underneath, but a backup is only as good as its restore: try restoring from your backups now and then, and keep the
passphrase and the repository key safe – without them nobody can restore your data.

## Download

Portable builds for Windows (x64), macOS (Apple Silicon and Intel) and Linux (x64) are on the
[Releases](https://github.com/cuban8IO/borgstudio/releases) page. They include the .NET runtime:
unpack and start, no installation needed. BorgStudio looks for a new version at startup and once a day and updates
itself after asking (Help → Check for updates; can be switched off in Tools → Settings). Version 0.1.0 has no
updater yet: download 0.2.0 once by hand and replace the old files.

[BorgBackup](https://www.borgbackup.org/) 1.2 or newer (1.4 recommended) must be installed separately.
BorgStudio finds it on `PATH` and in the usual install locations (e.g. Homebrew); on Windows it also
looks for borg inside WSL. borg 2 is still in beta and not supported yet. On Linux, .NET needs ICU (`libicu`), which
desktop distributions have installed. The UI is available in English and German:
it follows the system language (English if that is neither) or the choice under Tools → Settings.

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
- **Settings:** `settings.json` in the data folder (app lock, UI language, update checks).
- **Updates:** a downloaded update is unpacked into `updates/` in the data folder and removed after installing;
  `updates/update.log` tells how the last installation went.
- **Archive cache:** `archives/` in the data folder – per repository the archives last seen with their sizes, so the
  archives tab opens at once. Encrypted with the vault's key (`<id>.vault`) when a PIN is set up, plain JSON
  (`<id>.json`) otherwise; safe to delete.
- **Backup jobs:** `jobs.json` in the data folder – per job the repository, the source folders, what to leave out,
  the compression and the outcome of the last run. Archives are named `<job>-<computer>-<date and time>`. A job backing
  up a removable drive keeps the drive's id (`volume:<UUID or serial number>`, or `file:<id>` for a drive without one,
  with the id in `.borgstudio-id` on the drive) instead of source folders.
- **Run history:** `history/<job>.json` in the data folder – the last 100 runs of each job (outcome, sizes, clean-up);
  removed with the job. `locks/` holds one lock file per repository while a backup runs, so two BorgStudio runs never
  work on the same repository at once.
- **Schedules:** kept with the jobs in `jobs.json`; the system’s task scheduler gets one entry per scheduled job
  (Windows: Task Scheduler folder `\BorgStudio`, macOS: `~/Library/LaunchAgents/net.cuban8io.borgstudio.*.plist`,
  Linux: `~/.config/systemd/user/borgstudio-*.timer`). Removing a job or its schedules removes the entry.
- **Scheduled runs:** `logs/scheduled.log` gets one line per run started without window (`BorgStudio --run-job <job>`).
  With a PIN, scheduled runs open the vault with its key kept in the system keychain (entry `scheduled-access`) – or,
  without a keychain, in `scheduled-access.key`, readable only by the user. It follows PIN changes and goes when the
  vault is reset.
- **Running runs:** `running/<process>-<id>.json` in the data folder – each run started without window leaves word of itself
  there while it waits or works (the window lists it); removed when it ends, ignored once its process is gone.
- **Moving to another computer:** Tools → Export settings writes everything – repositories, stored passphrases
  (vault and keychain), SSH keys, known servers, settings and, if wanted, the borg keys of the repositories – into one
  `.borgstudio` file, encrypted with a password of its own (at least 12 characters; Argon2id + AES-256-GCM). Tools →
  Import settings shows what the file contains and merges it (asking per existing entry) or replaces everything;
  keyfile keys are put back into borg's key folder. External plugins are not part of an export.
- **Repository keys:** BorgStudio offers to export the key after creating a repository. Keep the key file and the
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
- **rsync.net:** connects on port 22 with `borg14` or `borg12` on the server. The host key is checked against the
  fingerprints rsync.net publishes. Login keys (restricted to borg by default) are appended with
  `dd of=.ssh/authorized_keys oflag=append conv=notrunc`, as rsync.net documents; if the service does not accept the
  restricted key, the editor suggests an unrestricted one. With an unrestricted key the storage overview shows the
  account's quota (rsync.net's `quota` command).
- **Network disk (iSCSI, NVMe over TCP on Linux):** before each use BorgStudio logs in to the target – unless its
  volume is attached already – finds the repository on it by its ID (like a USB drive) and logs out afterwards if it
  logged in. Logging in needs administrator rights, so saving the repository in the editor sets that up once: on
  Windows (built-in iSCSI initiator) with a UAC prompt, two tasks in the Task Scheduler (`\BorgStudio\`) that log in to
  and out of exactly this target with highest rights; on Linux (open-iscsi or nvme-cli) with pkexec, a sudoers rule
  (`/etc/sudoers.d/borgstudio-<id>`) for exactly this login and logout, and the volumes are mounted with udisks.
  CHAP is optional (iSCSI; the secret is kept in the vault and, on Windows, in the login task). macOS has no built-in
  initiator; Windows clients have no NVMe/TCP. Removing the repository leaves the tasks or the sudoers rule in place.
- **SFTP server (without borg):** BorgStudio mounts the folder holding the repository with sshfs where borg runs
  (WSL, Linux, macOS with macFUSE) and borg works on it like on a local folder – slower than borg on the server. sshfs
  runs in the foreground, held by BorgStudio; borg only starts after checking the folder is mounted, so it never writes
  to the empty local folder. The mount is reused by the next call and unmounted two minutes after the last one (and
  when BorgStudio or a scheduled run ends). The login key is added over SFTP with the password and is not restricted to
  borg (that would block SFTP). Install sshfs first, e.g. `sudo apt install sshfs` in WSL.
- **BorgBase:** with an API token (kept in the vault; "Create Only" is enough) BorgStudio registers the login key,
  creates or finds the repository by name, trusts the host key BorgBase reports for its server and shows the quota
  (the repository's, or the account's). Without a token the editor shows the public key to add in BorgBase's web
  interface, and the repository's address is entered by hand. The key gets full access (BorgStudio prunes and
  compacts).
- **USB or external drive:** the repository is found by its borg repository ID wherever the drive is attached – where
  it was last seen, at the same folder on every drive, then up to two levels deep on every drive (network drives
  included); a new place is remembered. A local repository on an external drive can be switched over in the editor
  (*Keep as USB drive*). Optionally the drive is ejected safely after scheduled backups and checks (Windows: like
  "Safely remove hardware", Linux: `udisksctl`, macOS: `diskutil eject`) – also after several scheduled runs in a row
  only once, after the last. *Back up when the drive is attached* (per repository, while BorgStudio is open – window or notification area):
  BorgStudio asks, then runs all jobs of the repository one after the other; not again while a job backed up
  successfully by itself (scheduled or on attaching – not by hand) within the set hours (default 12). Inside WSL, BorgStudio mounts drives that
  were attached after WSL started (`mount -t drvfs`) and unmounts them again after borg is done.
- **Removable drive as the source of a job:** the drive is recognized by its file system's UUID or serial number
  (Windows: the volume serial number, Linux: `findmnt`/`lsblk`, macOS: `diskutil`); a drive without one gets a small
  hidden file `.borgstudio-id` when it is chosen. borg runs in the drive's root folder, so the archives hold paths like
  `DCIM/…` whatever letter or mount point the drive has (and borg's file cache keeps working). The whole drive or chosen
  folders on it, with exclusions relative to its root; `System Volume Information`, `.Spotlight-V100`, `.fseventsd`,
  `.Trashes` and `.borgstudio-id` are always left out. Not attached: a run by hand says so, a scheduled run waits for the
  drive like for a condition and is skipped if it does not come. Optionally *ask when the drive is attached* (per job,
  while BorgStudio is open; all jobs of the drive in one question) and eject it after automatic runs. Archives of a
  drive cannot be restored to their "original place" – restore them into a folder, e.g. on the drive.

## License

BorgStudio is licensed under the [MIT License](LICENSE). The release packages include `LICENSE.txt` and
`THIRD-PARTY-NOTICES.txt` with the licenses of everything they contain (Avalonia, SkiaSharp, HarfBuzz, SSH.NET,
BouncyCastle, the .NET runtime, the Inter font and more); `.github/scripts/third-party-notices.mjs` writes the latter
from the restore data when the packages are built.

## Project structure

```
BorgStudio.slnx
├── src/
│   ├── BorgStudio.App                  Avalonia desktop UI (MVVM, CommunityToolkit.Mvvm)
│   ├── BorgStudio.Core                 UI-independent logic: borg CLI, plugins, repositories
│   ├── BorgStudio.Plugins.Abstractions Public plugin API (repository providers)
│   ├── BorgStudio.Providers.Local      Built-in provider: local folder
│   ├── BorgStudio.Providers.Ssh        Built-in provider: SSH server with borg
│   ├── BorgStudio.Providers.Sftp       Built-in provider: SFTP-only server (sshfs)
│   ├── BorgStudio.Providers.Hetzner    Built-in provider: Hetzner Storage Box
│   ├── BorgStudio.Providers.Usb        Built-in provider: USB or external drive
│   ├── BorgStudio.Providers.NetworkDisk Built-in provider: network disk (iSCSI, NVMe/TCP)
│   ├── BorgStudio.Providers.RsyncNet   Built-in provider: rsync.net
│   └── BorgStudio.Providers.BorgBase   Built-in provider: BorgBase (GraphQL API)
├── samples/
│   └── BorgStudio.SamplePlugin         Example external plugin (not part of the release)
├── tests/
│   └── BorgStudio.Tests                xUnit tests
├── docs/                               Developer documentation
├── site/                               Product page (GitHub Pages)
├── build/                              Packaging assets (app icon source, macOS Info.plist and .icns)
└── .github/                            CI, packaging and release workflows
```

Shared build settings (target framework, nullable, version) live in `Directory.Build.props`.

## Plugins

Repository providers (local folder, USB drive, network disk, SSH server, SFTP server, Hetzner Storage Box, rsync.net,
BorgBase, …) are plugins. Built-in providers use the same
API as external ones; see [docs/plugins.md](docs/plugins.md) for writing and installing a plugin.

## Build, test, run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, to actually use the app,
BorgBackup (see above). UI texts live in `src/BorgStudio.App/Resources/Strings*.resx`.

```bash
dotnet build
dotnet test
dotnet run --project src/BorgStudio.App
```

To try the update locally, publish two versions (`-p:Version=0.0.1` and `0.0.2`), serve a copy of GitHub's
release JSON with the newer package and its `SHA256SUMS` from a local web server and start the older one with
`BORGSTUDIO_UPDATE_FEED` set to the JSON's URL.

The screenshots of the product page (`site/images/en` and `site/images/de`) come from the real windows, filled with
made-up example data and fake borg, rendered without a screen:

```bash
dotnet run -c Release --project tools/BorgStudio.Screenshots -- en site/images/en
dotnet run -c Release --project tools/BorgStudio.Screenshots -- de site/images/de
```

Further arguments name single pictures, e.g. `-- en site/images/en dashboard compare`.

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
| `main` | releases only – every push to `main` publishes a release, unless it only changes the product page (`site/` and its build script), the README, `.gitignore` or the workflows |

Release versions are calculated automatically from the last `vX.Y.Z` tag: if any `feature/*` branch was merged
since then, the minor version goes up (0.**x**.0), otherwise the patch version (0.0.**x**).
Every pull request adds its change to the **Unreleased** section of the changelog below; when releasing, that
section gets the new version number.

## Changelog

### 0.5.0 – 2026-11-23

**Added**
- Integrity check per repository (`borg check`, thorough with `--verify-data`, repair after damage), with its history,
  dashboard state and a "check errors" notification (#53). Automatic checks after backups or by schedule, and monthly
  schedules for checks and jobs (#54).
- Restore test per job: a random sample restored and compared byte by byte, or the whole archive read without writing;
  with the automatic checks or by hand (#55).
- Release a lock left behind (`borg break-lock`) after a warning that tells who holds it (#56).
- Commands before and after a job's backup, with the shell chosen per system and selectable error handling (#57).
- Upload limit (optionally only between two times) and low priority (`nice`) per repository (#58).
- Conditions for scheduled runs: mains power, no metered connection, cable instead of Wi-Fi, target reachable (#59).
- Archives: compare two archives (`borg diff`) (#60, #69); rename, comment, keep and delete archives (#61); export an
  archive or a selection as zip, tar, tar.gz or tar.zst (#62).
- New repository types: USB or external drive, found by the repository's ID wherever it is attached, with optional
  ejecting (#63) and backing up when the drive is attached (#64); rsync.net (#65); BorgBase with API token, key setup and
  quota (#66); SFTP-only servers mounted with sshfs (#67); network disks over iSCSI and NVMe/TCP (#68). Plugin API:
  `IRepositoryConnector`, `IStorageQuotaSource`, `ISshKeyProvisioner`, `IRepositoryPreparation`, secret and toggle fields.
- A removable drive as the source of a job, recognized under any letter or mount point (#72).
- Schedules every few minutes (#70).
- Running tasks in the status bar, with *Show* and *Cancel*; the windows of longer work no longer block the main
  window (#73).
- Repository types not yet tested against a real service or target (rsync.net, network disk) are marked "Untested"
  when choosing where a repository is. Plugin API: `IRepositoryProvider.IsUntested`.

**Changed**
- The tray icon is shown whenever BorgStudio runs, and closing the window hides it there; archive marks for checks and
  restore tests; three state lines per repository in "All repositories"; local builds name their commit in the title
  (#69).
- The archive list fits narrow windows (#70).

**Fixed**
- A killed borg no longer shows the internal process id line; English texts use English quotes; exporting a whole
  archive leaves out the folders above what was backed up (#71).

### 0.4.0 – 2026-11-20

**Changed**
- Backups run through one shared runner (`BackupRunner`) – the groundwork for scheduled backups. Each run is kept in
  the job’s history (last 100 runs). While a backup works on a repository, another one for the same repository is
  turned away with a clear message instead of failing on borg’s lock, and a job’s last run recorded by another
  BorgStudio process is no longer overwritten when the app saves its jobs.

**Added**
- Backups without window: `BorgStudio --run-job <job id>` runs one job as a schedule will start it (exit code 0 backed
  up, 1 failed, 2 skipped, 3 unknown job). Nothing is asked: what is missing ends the run with its reason, recorded with
  the job – disk not connected, repository busy, passphrase missing, vault not open for scheduled runs, server never
  confirmed. These count as failures; a repository that asks for its passphrase every time is skipped.
- Schedules (backup job editor → Schedule): any number per job – daily at a time, on chosen weekdays, or every 1–12
  hours from a time – and "make up for missed times once at the next start". The job list shows the schedules and the
  next run. BorgStudio keeps the system’s task scheduler in step whenever the jobs are saved and at startup: a task
  `\BorgStudio\<job>` in the Windows Task Scheduler, a LaunchAgent per job on macOS (plus one that catches up at
  login, `BorgStudio --catch-up`), a systemd user timer per job on Linux (`Persistent=true`). The first schedule of a
  job offers to store a passphrase that is asked every time and, with a PIN, asks whether scheduled runs may open the
  vault.
- Key for scheduled runs (`ScheduledAccess`): lets them open the PIN-protected vault through the system keychain, or a
  file only the user can read where there is none. Set up with the first schedule (next step).
- Background mode (Settings → Scheduled backups → "BorgStudio in the background"): instead of the system’s task
  scheduler, BorgStudio runs the schedules itself. It starts at login without window (`--background`: a `Run` entry in
  the registry on Windows, a LaunchAgent on macOS, `~/.config/autostart/borgstudio.desktop` on Linux), stays in the
  notification area, and closing the window only hides it (a hint says so the first time). Scheduled backups run in the
  app, one after the other, with their progress in the window; missed times are made up for once at startup. The
  vault stays locked for the window – the scheduler opens its own copy with the key for scheduled runs. The icon shows
  a running backup, a failed one or paused schedules; its menu opens the window, shows the next backup, starts a job,
  pauses the schedules and quits. Switching modes removes or restores the system’s entries and the start at login.
- Only one BorgStudio per user: starting it again brings the running one’s window forward.
- Notifications (Tools → Notifications): every run – by hand, in the background and scheduled – can be reported as a
  system notification (Windows toast – shown under a “BorgStudio” start menu shortcut that BorgStudio creates and
  keeps pointing at itself, as Windows requires for apps without package; macOS notification center; `notify-send` on
  Linux), by e-mail over SMTP (MailKit)
  and through an outgoing webhook (BorgStudio’s JSON with all details, or ready-made for ntfy, Discord, Slack and
  Gotify, with an optional token header). Each channel reports the events chosen for it – failures, skipped runs,
  warnings, “working again”, every success – and has a Test button. The same problem of a job is reported only once
  until the job works again (can be switched off); single jobs can be left out in their editor. The SMTP password and
  the webhook’s token are kept like passphrases (in the vault with a PIN – scheduled runs use their key – otherwise in
  the system keychain) and travel with an export. Sending problems never fail a backup; they go to
  `logs/notifications.log`.
- Storage (repository overview → Storage): after every backup – and with "Measure" – BorgStudio measures how full the
  repository’s storage is and shows it as a bar: the local disk, `df` on the server for SSH keys that may run more than
  `borg serve`, otherwise the repository’s size from borg against a capacity entered in the repository editor
  (Advanced; e.g. the size of a storage box). Below a threshold per repository (10 % free by default) it counts as
  almost full; as full below 1 % free, with less free than the last backups added, or after a backup failed for lack
  of space (each switchable in Tools → Notifications). Both are new notification events, reported once per level.
  borg’s “No space left on device” and “Insufficient free space” are now recognized as such.
- Daily check (Tools → Notifications → More, 08:00 by default): reports jobs without a successful backup for longer
  than the days set in their editor (“overdue”, off by default, once until the job backs up again) and sends a daily
  summary – the backups since the last check, the storage of each repository, the overdue jobs – to the channels that
  want it, in addition to their single messages; on quiet days only if wanted. With the task scheduler it is an entry of
  its own (`BorgStudio --daily-check`, made up for when the computer was off); in the background BorgStudio runs it
  itself.
- Incoming webhook (Settings → Incoming webhook; background mode only): `POST /run/<job name or id>` starts a job like a
  scheduled one – after the one running now, without questions; `?wait=1` answers with the outcome when it is done.
  `GET /status` lists the running and queued jobs and each job’s last and next run, `POST /cancel` stops the running
  backup. Every call needs the token (`Authorization: Bearer <token>`; kept like a passphrase, “New token” replaces it),
  and a job must allow it in its editor. Listens on 127.0.0.1 (port 8473 by default), optionally on all addresses;
  a small built-in HTTP server, no admin rights needed.
- Storage history and forecast (repository overview → Storage): every measurement is kept (the last one a day, two years),
  together with borg’s repository statistics, which come with every backup. The bar splits the disk into this repository,
  other data and free space; below it: how fast the storage grows and when it will probably be full (linear trend of the
  last 90 days, with a range; before there are enough measurements, estimated from what the backups of the last 30 days
  added minus what clean-ups freed), and what deduplication and compression save.
- Statistics tab per repository: the storage over time with the forecast as a dashed line up to the disk size or capacity;
  what deduplication and compression save (original size, stored, ratios); the backups – success rate (30 days), last
  successful backup, average duration and newly stored data, and a column per backup (red failed, yellow warnings), for all
  jobs or one; and the activity of the last 12 weeks as a calendar. Hovering a chart shows its values. The charts are
  BorgStudio’s own controls, no chart library.
- Overview of all repositories (“All repositories” above the list; the app starts there): what all repositories take up
  and save, the success rate of 30 days, which storage fills up first and when; each repository with its storage bar and
  its state, problems first – judged by the schedules (a missed or failed scheduled backup is a warning, two missed ones
  or a job’s overdue days critical; jobs started by hand only by failures), plus almost full or full storage; and the
  activity of all jobs. Clicking a repository opens it.

**Fixed**
- BorgStudio no longer crashes at startup when .NET runs without culture data (Linux without ICU).

### 0.3.0 – 2026-11-17

**Added**
- Product page at https://borgstudio.cuban8io.net/ (`site/`, English at `/` and German at `/de/`) with the
  advantages, the main functions with screenshots, examples and the current release with its downloads, read from
  GitHub when the page is opened. `.github/scripts/build-site.mjs` builds the German page from `site/i18n/de.json`
  and adds what search engines need: canonical and language links, Open Graph data, schema.org structured data, a
  sitemap and robots.txt. It is published by `.github/workflows/pages.yml` on changes to main and after every
  release. Pushes to main that only change the page, the README, `.gitignore` or the workflows create no release.
  The footer links to a legal notice (Impressum) and a privacy policy in both languages; the build refuses to publish
  a page that still contains a placeholder.
- "No warranty" note on the product page, in the README and in the wiki.
- MIT License (`LICENSE`); every release package carries `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` (in a macOS
  bundle under `Contents/Resources`), linked from the About window.
- In the app: a "no warranty" notice at the first start (also after updating from an older version, with a PIN after
  unlocking) and every time a new repository is created; a Help menu with the guide, the website, the update check
  and "About BorgStudio" (version, links to the website, guide, source, legal notice and privacy policy).

**Changed**
- "Check for updates" moved from the Tools menu to the new Help menu.

### 0.2.0 – 2026-11-09

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
- Archive browser (tab "Archives" → "Open…" or double-click): folder tree and the files of each folder in a table
  (resizable, sortable columns) with size and date, a search over the whole archive (part of the name, or patterns
  like `*.pdf`) and tick boxes to select files and folders. Windows drives backed up through WSL show as `C:\…`. The
  file list is read from borg line by line with progress and remaining time (`borg list` reports none: counted against
  the archive's file count), then cached per archive – archives never change – encrypted with the vault's key when a
  PIN is set up.
- Restore (archive browser → "Restore selection…" or "Restore whole archive…"): into a folder – directly (the selected
  items land in it) or with the full path as in the archive – or back to the original location (with borg in WSL:
  Windows drives, one run per drive). Before writing, BorgStudio checks which files exist already and asks before
  overwriting them, naming those changed after the backup. borg extract runs with progress, pace (MB/s), remaining
  time and the current file; it can be cancelled (what was restored stays). Files borg could not write are listed;
  "Open folder" shows the result.
- Search all archives (tab "Archives" → "Search all archives…"): finds files and folders by name (part of the name or
  patterns like `*.pdf`) in every archive of the repository, one row per path with how many versions it has and in how
  many archives it is. Archives whose file list was never read are read by borg once – after asking, with an estimate
  of how long it takes (or only the others are searched) – archive by archive with progress ("Archive 7 of 42") and
  remaining time; then they are cached like an opened archive.
- Versions of a file (search → double-click, or archive browser → "Versions…"): one row per change (size or date) with
  the archives in which it stayed the same counted together, and the stretches of archives it is missing from
  (deleted, or not there yet). Any version can be restored, from the newest archive that has it.
- Mount archives (tab "Archives" → "Mount" or "Mount all archives"): `borg mount` makes an archive – or the whole
  repository, one folder per archive – a read-only folder below `~/borgstudio-mounts` and opens it in the file
  manager. With borg in WSL, Explorer reaches it through `\\wsl.localhost\…`; that needs `user_allow_other` in WSL's
  `/etc/fuse.conf` once, which BorgStudio adds after asking (as root in WSL, no password) or shows the command for.
  Several archives can be mounted at once; the status bar lists them with "Open" and "Unmount". While a repository has
  mounts, backups and clean-ups ask to unmount first (borg holds the lock). Closing BorgStudio unmounts everything;
  mounts left behind by a crash are cleaned up on the next start.
- Language setting (Tools → Settings): system language, English or German; takes effect after a restart, which the
  settings window offers right away.
- Updates: BorgStudio asks GitHub for the newest release at startup and once a day (can be switched off) and shows a
  banner when there is one; Tools → Check for updates asks at once. "Update now" shows what is new, downloads the
  package for this system with progress, checks it against the release's `SHA256SUMS`, closes the app (a running
  backup asks first, mounts are unmounted) and lets the new version copy itself over the old files – only files of the
  package are written, the replaced ones are put back if anything fails – and start again. Where the app may not
  change its folder, or for local builds, the release page is offered instead. Releases now include `SHA256SUMS`.

**Changed**
- Every wait shows a progress bar with percent, elapsed and remaining time, so it is clear the app is working:
  testing a connection, exporting the key, freeing space, loading archives and their details (borg now reports
  archive by archive), cleaning up (prune reports per archive), and the steps in the add/edit repository dialog.
  Where borg reports no progress, the bar runs on how long the same operation took last time ("about 0:30 left");
  these durations are remembered per repository in `durations.json`.
- Dark design in the Beatport palette: dark ground, neon green for primary buttons, the selected list entry, progress
  bars, check boxes and success messages.

**Fixed**
- The add/edit repository dialog now closes after a successful add or save.

### 0.1.0 – 2026-10-06

**Added**
- Release pipeline: portable, self-contained packages for Windows (x64), macOS (Apple Silicon and Intel) and
  Linux (x64), published on every push to `main`.
