# NuGet publish workflow — design

Publish Custodex's library packages to the repository's private GitHub Packages NuGet feed from a manually-triggered GitHub Actions workflow, with versions derived from git by MinVer.

## Overview

A single workflow, `.github/workflows/publish.yml`, is dispatched manually against `main`. It packs the four consumer-facing libraries and pushes them to `https://nuget.pkg.github.com/Seriousnes/index.json`, authenticated with the workflow's built-in `GITHUB_TOKEN`. Package versions come from MinVer, which reads the latest reachable git tag plus commit height — no version is hand-maintained in the repository.

The change has three parts: the packable surface (which projects ship and their metadata), the versioning and symbol configuration (centralised in `Directory.Build.props`), and the workflow itself.

## Packable surface

Four libraries ship as packages:

| Project | PackageId | Description |
|---|---|---|
| `Custodex.Core` | `Custodex` | The runtime-configurable ReBAC + ABAC authorization engine — the portable, dependency-light evaluation core. |
| `Custodex.Abstractions` | `Custodex.Abstractions` | Public contracts (interfaces and records) for the engine — the dependency for consumers and third-party storage and condition providers. |
| `Custodex.Storage.InMemory` | `Custodex.Storage.InMemory` | In-memory storage providers — a database-free path for tests, local development, and demos. |
| `Custodex.Storage.Postgres` | `Custodex.Storage.Postgres` | PostgreSQL storage provider built on Npgsql and Dapper. |

`Custodex.Core` overrides its `PackageId` to `Custodex` so consumers install the engine as `dotnet add package Custodex`. The storage providers reference Core by project reference; NuGet emits that dependency under Core's `PackageId`, so the produced packages depend on `Custodex`. `Custodex.Abstractions` is a transitive dependency of `Custodex`, so it is always present on the feed and restorable alongside it.

Packability is opt-in. `Directory.Build.props` sets `IsPackable=false` for every project; each of the four libraries sets `IsPackable=true`. `dotnet pack Custodex.slnx` then emits exactly those four and skips tests, the Aspire host and service-defaults, the service and client hosts, and the benchmarks. A new project added later does not publish unless it opts in.

Each library carries a `Description`, which keeps `dotnet pack` free of the missing-description warning that `TreatWarningsAsErrors` would otherwise promote to a build failure. Shared package metadata lives in `Directory.Build.props`: `Authors`, `PackageLicenseExpression` (Apache-2.0), `PackageProjectUrl` and `RepositoryUrl` set to `https://github.com/Seriousnes/Custodex`, and `RepositoryType` set to `git`. The `RepositoryUrl` value is functional, not cosmetic: GitHub Packages uses it to authorize the `GITHUB_TOKEN` push and to link each package to the repository.

## Versioning — MinVer

MinVer is referenced from `Directory.Build.props` with `PrivateAssets=all` so it computes the version for every project at build time without becoming a package dependency. The hand-maintained `<Version>` property is removed; MinVer owns `Version`, `AssemblyVersion`, `FileVersion`, and `PackageVersion`. `MinVerTagPrefix` is `v`, so release tags read `v0.2.0`. The default auto-increment is `patch`.

Version resolution follows git state:

- **HEAD is on tag `v0.2.0`** → version `0.2.0`, a clean release.
- **HEAD is past the latest tag** → an auto pre-release `0.2.1-alpha.0.<height>`, where `<height>` is the number of commits since that tag.
- **No tag is reachable** → `0.0.0-alpha.0.<height>`.

The repository has no tags today, and that is the intended starting state: until the first `vX.Y.Z` tag is created, packages publish as `0.0.0-alpha.0.<height>`, accurately marking the engine as pre-release. Tagging a commit `vX.Y.Z` and pushing the tag begins the stable line from that point.

## Symbols and source debugging

Symbols are embedded rather than shipped as separate symbol packages: `Directory.Build.props` sets `DebugType=embedded` and removes the `IncludeSymbols`/`SymbolPackageFormat` settings. Source Link ships with the .NET SDK, so the build enables it through properties alone — `EmbedUntrackedSources=true` and `PublishRepositoryUrl=true` — letting consumers step into Custodex source from the published packages. The workflow passes `ContinuousIntegrationBuild=true` at pack time for deterministic, normalised output. Each `.nupkg` therefore carries its own debugging information, and the push uploads only `.nupkg` files.

## The publish workflow

`.github/workflows/publish.yml`:

- **Trigger** — `workflow_dispatch` only, dispatched against `main`. A single boolean input, `dry_run`, packs and lists the resolved package versions without pushing, for previewing a publish.
- **Permissions** — `contents: read` and `packages: write`. The built-in `GITHUB_TOKEN` carries both; no personal access token is involved.
- **Concurrency** — a `publish-*` group with `cancel-in-progress: false`, so a publish in flight is never interrupted.
- **Steps**:
  1. Checkout with `fetch-depth: 0`, giving MinVer the full history and tags it needs.
  2. Set up the .NET 10 SDK (`10.0.x`).
  3. `dotnet restore Custodex.slnx`.
  4. `dotnet pack Custodex.slnx --configuration Release --no-restore -p:ContinuousIntegrationBuild=true --output artifacts`. Letting `pack` build in one invocation keeps the MinVer-computed version on the produced packages.
  5. List the `artifacts` directory so the resolved versions appear in the run log.
  6. When `dry_run` is false: add the GitHub Packages source for `Seriousnes`, then `dotnet nuget push "artifacts/*.nupkg" --source github --api-key ${{ secrets.GITHUB_TOKEN }} --skip-duplicate`. The push glob is quoted so `dotnet` expands it rather than the shell. `--skip-duplicate` makes re-running on an unchanged commit a no-op, since MinVer versions are a pure function of git state.

## Operating it

- **Cut a stable release `0.2.0`** — `git tag v0.2.0 && git push origin v0.2.0`, then run the workflow → the feed receives `0.2.0`.
- **Share an in-progress build** — run the workflow on untagged `main` → the feed receives a pre-release such as `0.2.1-alpha.0.5`, which NuGet surfaces only to consumers who opt into pre-releases.
- **Preview without publishing** — run the workflow with `dry_run` enabled to see the resolved versions in the run log without touching the feed.

## File changes

The `MinVer` reference is pinned to its current stable version. Source Link needs no package reference — it is part of the .NET SDK.

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

    <IsPackable>false</IsPackable>

    <Authors>Custodex contributors</Authors>
    <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/Seriousnes/Custodex</PackageProjectUrl>
    <RepositoryUrl>https://github.com/Seriousnes/Custodex</RepositoryUrl>
    <RepositoryType>git</RepositoryType>

    <DebugType>embedded</DebugType>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>

    <MinVerTagPrefix>v</MinVerTagPrefix>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MinVer" Version="7.0.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

`src/Custodex.Core/Custodex.Core.csproj` adds:

```xml
<PropertyGroup>
  <IsPackable>true</IsPackable>
  <PackageId>Custodex</PackageId>
  <Description>The runtime-configurable ReBAC + ABAC authorization engine — the portable, dependency-light evaluation core.</Description>
</PropertyGroup>
```

`src/Custodex.Abstractions/Custodex.Abstractions.csproj`, `src/Custodex.Storage.InMemory/Custodex.Storage.InMemory.csproj`, and `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj` each add `<IsPackable>true</IsPackable>` and a `<Description>` from the table above.

`.github/workflows/publish.yml`:

```yaml
name: Publish

on:
  workflow_dispatch:
    inputs:
      dry_run:
        description: Pack and list resolved versions without pushing
        type: boolean
        default: false

permissions:
  contents: read
  packages: write

concurrency:
  group: publish-${{ github.workflow }}
  cancel-in-progress: false

jobs:
  publish:
    name: Publish to GitHub Packages
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Restore
        run: dotnet restore Custodex.slnx

      - name: Pack
        run: dotnet pack Custodex.slnx --configuration Release --no-restore -p:ContinuousIntegrationBuild=true --output artifacts

      - name: List packages
        run: ls -1 artifacts

      - name: Push
        if: ${{ !inputs.dry_run }}
        run: |
          dotnet nuget add source --username Seriousnes --password ${{ secrets.GITHUB_TOKEN }} --store-password-in-clear-text --name github "https://nuget.pkg.github.com/Seriousnes/index.json"
          dotnet nuget push "artifacts/*.nupkg" --source github --api-key ${{ secrets.GITHUB_TOKEN }} --skip-duplicate
```
