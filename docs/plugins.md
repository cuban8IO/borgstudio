# Writing a BorgStudio plugin

BorgStudio gets its repository providers – the places where borg repositories can live – from plugins.
The built-in providers (e.g. *Local folder*) use exactly the same API as external plugins.

A complete, working example is [`samples/BorgStudio.SamplePlugin`](../samples/BorgStudio.SamplePlugin).

## 1. Project

A plugin is a .NET 10 class library that references the plugin API, `BorgStudio.Plugins.Abstractions`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <!-- Produces a .deps.json and copies private dependencies, as needed for loading at runtime. -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Compile against the plugin API, but don't ship a copy: BorgStudio provides it at runtime. -->
    <ProjectReference Include="path/to/BorgStudio.Plugins.Abstractions.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

Do not ship `BorgStudio.Plugins.Abstractions.dll` with your plugin. BorgStudio always uses its own copy, so that
your classes implement the very interfaces BorgStudio knows.

## 2. Code

Implement `IBorgStudioPlugin` (the entry point) and register your providers:

```csharp
public sealed class MyPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new MyProvider());
}
```

An `IRepositoryProvider` describes

- `Id` – stable and unique, stored with every repository that uses the provider. Never change it.
- `DisplayName`, `Description` – shown when the user picks a provider.
- `Fields` – the input it needs (`ProviderField`: key, label, kind, required, default value, hint).
  Kinds are `Text`, `Number`, `FolderPath` (with a folder picker), `Choice` – a list of `Options`
  (`ProviderFieldOption`: the value your provider gets, and the label the user sees), `Toggle`, a switch whose
  value is `"true"` or `"false"`, and `Secret` – entered hidden and kept in BorgStudio's vault, never with the
  repository. Secrets are not part of the values `Validate` and `GetLocation` get; the requests that need them carry
  them in `Secrets`.
- `Validate(values)` – error messages for the entered values; empty when everything is fine.
- `GetLocation(values)` – the borg repository URL (`/path` or `ssh://user@host:port/path`)
  plus extra borg arguments such as `--remote-path=borg-1.4`.
- `IsUntested` (optional, `false` by default) – `true` puts an "Untested" badge next to the provider when the user
  picks one, for providers not yet tried against a real service or target.

Return all texts in `CultureInfo.CurrentUICulture` (BorgStudio itself ships English and German),
for example from `.resx` files.

### Repositories on SSH servers

If your provider's repositories are reached over SSH (most hosted borg services are), return `true` from
`UsesSsh` and set `RepositoryLocation.Ssh`:

```csharp
public bool UsesSsh => true;

public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) =>
    new($"ssh://{user}@{host}:{port}/./{path}")
    {
        BorgArguments = ["--remote-path=borg-1.4"],
        Ssh = new SshEndpoint(host, port, user, RepositoryPath: path), // path as the server sees it
    };
```

BorgStudio then takes care of the rest: it shows the server's host key fingerprint for confirmation on first
contact, creates a login key for the repository and installs it with the user's password (or uses an existing
key), and runs borg with strict host key checking. Your provider never sees passwords or keys.

Hosted services often differ from a plain server. `SshEndpoint` describes how:

| Property | Use it when |
|---|---|
| `KeyInstallation = SshKeyInstallation.Sftp` | the service has no regular shell (no redirections), so the key line is appended to `~/.ssh/authorized_keys` over SFTP |
| `InstallKeyCommand = "install-ssh-key"` | the service has its own command that reads a public key from standard input; BorgStudio uses it for keys not restricted to borg |
| `KeyInstallation = SshKeyInstallation.Command` | the service appends standard input to a file with a command of its own; BorgStudio sends every key line (restricted or not) to `InstallKeyCommand`, e.g. `dd of=.ssh/authorized_keys oflag=append conv=notrunc` |
| `PublishedHostKeyFingerprints = ["SHA256:…"]` | the service publishes its servers' host key fingerprints; a matching key is trusted without asking, any other one only after a warning |

The built-in Hetzner Storage Box provider (`src/BorgStudio.Providers.Hetzner`) uses the first, second and last;
the rsync.net provider (`src/BorgStudio.Providers.RsyncNet`) uses `Command`.

### Setting something up once

If connecting needs something set up first – often with administrator rights, which scheduled runs cannot ask for –
implement `IRepositoryPreparation`. The editor calls `PrepareAsync` when the user saves the repository (with its
secrets and BorgStudio's id of the repository); return the values to keep, e.g. a marker of what was set up, so the next
save can skip it. A provider that also implements `IRepositoryConnector` is connected by the editor itself while the
repository is created or checked. The network disk provider (`src/BorgStudio.Providers.NetworkDisk`) is an example.

### Servers with SFTP only

If the server has no borg, set `RepositoryLocation.MountOverSftp = true` (with `Ssh` describing the login). BorgStudio
mounts the folder holding the repository with sshfs before each borg call and borg works on it like on a local folder.
Keys for such servers are never restricted to borg serve. The built-in SFTP provider (`src/BorgStudio.Providers.Sftp`)
does this.

### Services that set up keys themselves

Some services have no password login: keys are registered through their API (or by hand in their web interface).
Return `SshKeyInstallation.Provider` from the endpoint and implement `ISshKeyProvisioner`. BorgStudio calls
`ProvisionAsync` with the new login key before it first reaches the repository; return the values to keep (e.g. the
repository's address, which `GetLocation` reads from then on, and the server's host key fingerprints), or `null` when
the service cannot be asked – BorgStudio then shows the public key for the user to add by hand. Values without a field
of their own are kept with the repository. The BorgBase provider (`src/BorgStudio.Providers.BorgBase`) is an example.

### Quotas

If the service knows how much space an account has, implement `IStorageQuotaSource`. BorgStudio asks it before
measuring the server's disk; `RunCommandAsync` runs a command on the server with the repository's login key (only
for keys not restricted to borg, otherwise it is `null`). Return `null` when the quota cannot be found out – BorgStudio
then measures as usual.

```csharp
public async Task<StorageQuota?> GetQuotaAsync(StorageQuotaRequest request, CancellationToken cancellationToken) =>
    request.RunCommandAsync is { } run && await run("quota", cancellationToken) is { } output ? Parse(output) : null;
```

### Reports of runs

If the service wants to know what BorgStudio did with a repository (e.g. for statistics of its own), implement
`IRunReportReceiver`. After every backup, check, prune and compact BorgStudio writes a `RunReport` – kind, outcome,
start, duration and borg's figures – to a queue in its data folder and hands it to `SendReportAsync`. Throw
`RepositoryUnavailableException` when it cannot be delivered now (the service is unreachable, credentials are not
readable): the report stays queued and is tried again later, in order, per repository. Throw
`RunReportRejectedException` when the service will never take it (e.g. the repository is gone there): it is dropped.
The BorgStudio Server provider (`src/BorgStudio.Providers.Server`) is an example.

### Repositories that have to be found or connected first

If a repository is not simply at the same place every time – a drive that may get another letter, a share to mount,
a network disk to log in to – implement `IRepositoryConnector` as well. BorgStudio then calls `ConnectAsync` before
every use and works with the location it returns; uses of the same repository share one connection, and the
`disconnect` function you pass to `RepositoryConnection` runs when the last one is done.

```csharp
public async Task<RepositoryConnection> ConnectAsync(RepositoryConnectRequest request, CancellationToken cancellationToken)
{
    var folder = await FindAsync(request.Values, request.RepositoryId, cancellationToken)
        ?? throw new RepositoryUnavailableException("The drive is not attached."); // shown to the user as it is
    return new RepositoryConnection(new RepositoryLocation(folder), disconnect: null)
    {
        UpdatedValues = new Dictionary<string, string>(request.Values) { ["path"] = folder }, // kept with the repository
    };
}
```

`RepositoryId` is the borg repository ID once BorgStudio has reached the repository; `Unattended` is set for
scheduled runs, where nobody can answer questions. `GetLocation` still describes where the repository was last –
BorgStudio shows it in the overview. The built-in USB drive provider (`src/BorgStudio.Providers.Usb`) is an example.

BorgStudio creates every public, non-abstract class implementing `IBorgStudioPlugin` that has a public
parameterless constructor, and calls `Register` once at startup. If anything throws, the plugin is skipped
and the error is shown under *Tools → Plugins…*; other plugins are not affected.

## 3. Install

Copy the build output (your DLL, its `.deps.json` and private dependencies) into a sub folder of the plugin folder.
The sub folder must be named like your assembly, e.g. `plugins/Acme.BorgProvider/Acme.BorgProvider.dll`.

| OS | Plugin folder |
|---|---|
| Windows | `%LOCALAPPDATA%\BorgStudio\plugins` |
| macOS | `~/Library/Application Support/BorgStudio/plugins` |
| Linux | `$XDG_DATA_HOME/BorgStudio/plugins` (usually `~/.local/share/BorgStudio/plugins`) |

Restart BorgStudio. *Tools → Plugins…* lists every plugin, what it provides and any loading problems,
and opens the plugin folder.

Each external plugin is loaded into its own `AssemblyLoadContext`, so it can bring its own versions of
dependencies. Plugins run with the same rights as BorgStudio – only install plugins you trust.
