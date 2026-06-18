# Packaging & agent-kit impact review

You are reviewing a PR diff for the `microsoft/microsoft-ui-reactor` repo and asking:
**does this change affect the NuGet package, the shipped agent kit, AOT/trim
guarantees, or the build/release surface?** Apply the shared output contract in
`_shared-contract.md`. Set `Domain: packaging` on every finding.

## Distribution surfaces

Reactor ships primarily as one NuGet package plus tooling that must stay in sync:

| Artifact | Source | Notes |
|----------|--------|-------|
| `Microsoft.UI.Reactor` NuGet | `src/Reactor/Reactor.csproj` | Carries the framework, the **analyzers** (packed to `analyzers/dotnet/cs`), and the **agent kit** (packed to `agentkit/`). `PackageId` = `Microsoft.UI.Reactor`; `Version` defaults to `0.0.0-local`, supplied by the release workflow. |
| Analyzers / generators | `src/Reactor.Analyzers`, `src/Reactor.Localization.Generator`, `src/Reactor.Wrappers.Generator` | DLLs are `<None Pack="true" PackagePath="analyzers/dotnet/cs">` in `Reactor.csproj`. A new analyzer/generator assembly must be added to the pack list to ship. |
| Agent kit | `SKILL.md`, `skills/*`, `plugins/reactor/*` | Packed under `agentkit/` via **explicit `<None Include>` entries** in `Reactor.csproj`. |
| CLI (`mur`) | `src/Reactor.Cli` | `mur pack-local` populates `local-nupkgs/` for selfhost. |
| VS Code / VS extensions | `src/vscode-reactor`, `src/vs-reactor` | Separate packaging. |
| WinForms interop, devtools | `src/Reactor.Interop.WinForms`, `src/Reactor.Devtools` | Separate assemblies. |

## What to look for

- **New plugin sub-skill not added to the pack list.** Each
  `plugins/reactor/skills/<skill>/` is packed via an **explicit, per-skill
  `<None Include="..\..\plugins\reactor\skills\<skill>\*.md" ...>` entry** in
  `src/Reactor/Reactor.csproj` (the glob covers `skills/*.md` but **not** the
  per-plugin-skill folders). A new `plugins/reactor/skills/<new-skill>/` with no
  matching pack entry **will not ship** in the NuGet. Flag it — this is the most
  common packaging miss. (References subfolders need their own `references\*`
  entry too.)
- **New analyzer / source generator not packed.** A new
  `src/Reactor.*.Analyzer` or `*.Generator` assembly must be added to the
  `analyzers/dotnet/cs` pack list in `Reactor.csproj`, or it won't load in
  consumer projects.
- **AOT / trimming regressions.** `IsAotCompatible=true` is set for net10.0+
  projects and the **core Reactor library promotes IL trimming / AOT warnings to
  errors**. Flag new reflection, dynamic code, or unannotated trim-unsafe APIs in
  `src/Reactor/` without the proper `[RequiresUnreferencedCode]` /
  `[DynamicallyAccessedMembers]` / feature-guard annotations. CI has
  `aot-selftests` and `aot-trim-proof` jobs — a change that breaks them blocks merge.
- **`WindowsAppSDKSelfContained`.** Class libraries must set
  `WindowsAppSDKSelfContained=false`; only app executables own self-contained
  packaging. Flag a new library project (or a csproj change) that sets it `true`
  or omits the convention.
- **Centralized versions.** Package versions are centrally managed
  (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`); shared
  versions like `WindowsAppSDKVersion` / `Win2DVersion` live in
  `Directory.Build.props`. Flag a `<PackageReference>` that pins its own
  `Version=` inline instead of using central management, or a floating/wildcard
  version.
- **New dependency.** A new third-party package reference: is it added to
  `Directory.Packages.props`? Does it need a `cgmanifest.json` /
  `ThirdPartyNoticeText.txt` entry? Is it AOT/trim-friendly? Flag a heavy or
  trim-hostile dependency added to the core library.
- **Pack metadata.** New packable project missing required metadata
  (`PackageId`, license, README) that the existing `Reactor.csproj` carries.
- **Build/release wiring.** Changes to `.github/workflows/ci.yml`,
  `release.yml`, `coverage.yml`, `docs.yml`, `Directory.Build.targets`, or
  `global.json` (SDK pin) that alter what's built/shipped or which platform —
  flag anything that bypasses the canonical build or changes artifact paths.
- **Target framework / platform.** Changes to `TargetFramework(s)`,
  `SupportedOSPlatformVersion`, or `Platform` defaults (libraries AnyCPU; apps
  default to host arch) that could break consumers.

## What to drop

- Suggestions to bump the package version (the release workflow supplies it via
  `-p:Version=...`; `0.0.0-local` is intentional for selfhost).
- Asking for new packaging artifacts outside the existing distribution model.

## Severity guide for this dimension

- New `plugins/reactor/skills/<skill>/` or new analyzer/generator assembly not
  added to the `Reactor.csproj` pack list → high (it silently won't ship).
- AOT/trim-unsafe code in the core library without annotation → high (breaks the
  warnings-as-errors build / `aot-trim-proof`).
- Library project setting `WindowsAppSDKSelfContained=true` → high.
- Inline / floating package version bypassing central management → medium.
- New dependency missing from `Directory.Packages.props` / `cgmanifest.json` →
  medium.
- New packable project missing license/README/`PackageId` metadata → medium.
