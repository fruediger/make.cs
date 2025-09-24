# make.cs

make.cs is a minimal, file‑based C# build tool for managed projects with native runtime assets. It’s designed for multi‑flavor NuGet packaging (core, RID‑specific, meta) and was originally built to orchestrate the building and packaging of [SDL3#](https://github.com/fruediger/SDL3Sharp) and related projects. The tool is intentionally generic and can be adapted to other .NET projects with similar needs. It is cache‑aware, reproducible, and extensible via MSBuild.

## Requirements

- .NET 10 SDK must be installed  
- The `dotnet` CLI must be available in your PATH  

## Running the tool

You can run `make.cs` in several ways. Example shown with the `build` subcommand:

**Normal invocation:**

```shell
dotnet run make.cs -- build
```

**Unix‑like systems (shell script):**

```shell
./make.sh build
```

If you get a permission error, run:

```shell
chmod +x make.sh
```

**PowerShell:**

```shell
./make.ps1 build
```

**Windows CMD:**

```shell
make.cmd build
```

The wrapper scripts simply forward arguments to the tool.

## Commands, Options, and Examples

### Configuration file argument

Every invocation of `make.cs` can optionally take a **positional argument** that specifies a configuration file or a directory.  

- If a file is given, that file is used as the configuration.  
- If a directory is given, the tool looks for `make.json` inside that directory.  
- If omitted, the tool defaults to `make.json` in the current working directory.  

Example (using a custom config file):

```shell
./make.sh build ./myconfig.json
```

> [!NOTE]
> If a configuration file is found and used, the tool automatically changes the current working directory to the directory where the configuration file is located, for the lifetime of the tool.

### build

Builds the managed project.

```shell
./make.sh build
```

| CLI option   | Config property | Description                                  |
|--------------|----------------|----------------------------------------------|
| `--project`  | `project`      | Path to .csproj or directory containing one. |
| `--no-logo`  | `noLogo`       | Suppress startup banner.                     |

### pack

Packages NuGet artifacts: core, RID‑specific, and meta packages.

```shell
./make.sh pack
```

| CLI option                          | Config property              | Description                                                   |
|-------------------------------------|------------------------------|---------------------------------------------------------------|
| `--output-dir`                      | `outputDir`                  | Output directory (default: `./output`).                       |
| `--cache-dir`                       | `cacheDir`                   | Cache directory (default: `./cache`).                         |
| `--temp-dir`                        | `tempDir`                    | Temporary working directory (default: `./temp`).              |
| `--runtimes-version`                | `runtimesVersion`            | Version of runtime assets for RID packages.                   |
| `--runtimes-url`                    | `runtimesUrl`                | URL/format string for runtime archives.                       |
| `--runtimes-license-spdx`           | `runtimesLicenseSpdx`        | SPDX license expression for RID packages.                     |
| `--runtimes-license-file-url`       | `runtimesLicenseFileUrl`     | URL/format string to license file for RID packages.           |
| `--runtimes-license-spdx-file-url`  | `runtimesLicenseSpdxFileUrl` | URL/format string to text file with SPDX identifier.          |
| `--targets`                         | (no config)                  | Flavors to pack: core, meta, specific RIDs, or all.           |
| `--no-symbols`                      | (no config)                  | Skip symbols package for core.                                |
| `--strict`                          | (no config)                  | Fail if a requested RID is missing.                           |

### push

Pushes packages to a NuGet feed. May invoke `pack` if needed.  
**Note:** `--api-key` is required.

```shell
./make.sh push --api-key YOUR_API_KEY
```

| CLI option       | Config property | Description                                               |
|------------------|-----------------|-----------------------------------------------------------|
| `--api-key`      | (no config)     | Required. NuGet API key (never stored in config).          |
| `--nuget-source` | `nugetSource`   | NuGet feed URL (default: <https://api.nuget.org/v3/index.json>). |
| `--no-pack`      | (no config)     | Skip packing even if cache is stale.                      |
| `--fail-stale`   | (no config)     | Fail if cache is stale instead of packing.                 |

_All `pack` options are also accepted, since `push` may need to call `pack` before pushing._

### clean

Removes output, cache, and temp directories.

```shell
./make.sh clean
```

_No additional options._

---

**Note:** Configuration file properties mirror CLI options. CLI flags always take precedence. An example `make.json` is included in the repository; `_notes` entries are just documentation and ignored by the tool.

## How packing works

During `pack`, the tool generates temporary `.csproj` files and defines two MSBuild properties:

- **`MakeFlavor`**: Indicates the package flavor (`core`, `rid`, or `meta`).  
- **`MakeFlavorRid`**: For RID‑specific packages, set to the RID (e.g. `win-x64`). Not set for core or meta.

**Flavors:**

- **Core**: `MakeFlavor=core`  
- **RID‑specific**: `MakeFlavor=rid`, `MakeFlavorRid=<RID>`; includes native binary under `runtimes/{RID}/native`; license metadata from SPDX or license file options.  
- **Meta**: `MakeFlavor=meta`; depends on core and/or RID packages.

**Customizing builds:**

- `Directory.Build.props` can set defaults but is imported too early to see `MakeFlavor` or `MakeFlavorRid`.  
- `Directory.Build.targets` is imported later and can use these properties for flavor‑ or RID‑specific logic.

Example in `Directory.Build.targets`:

```xml
<Target Name="CustomRidPostPack" AfterTargets="Pack" Condition="'$(MakeFlavor)' == 'rid'">
  <!-- Custom logic for RID-specific packages -->
</Target>

<PropertyGroup Condition="'$(MakeFlavorRid)' == 'linux-x64'">
  <!-- Linux-specific tweaks -->
</PropertyGroup>
```

## License

Licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.
