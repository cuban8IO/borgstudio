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
- `Validate(values)` – error messages for the entered values; empty when everything is fine.
- `GetLocation(values)` – the borg repository URL (`/path` or `ssh://user@host:port/path`)
  plus extra borg arguments such as `--remote-path=borg-1.4`.

Return all texts in `CultureInfo.CurrentUICulture` (BorgStudio itself ships English and German),
for example from `.resx` files.

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
