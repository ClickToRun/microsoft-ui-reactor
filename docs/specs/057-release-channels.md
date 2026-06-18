# 057 — Release Channels (Stable / Preview / Nightly)

## Status

**Draft — design proposal.** No pipeline changes beyond the opt-in
`publishNuGet` / `nuGetFeed` plumbing already present in
`build/pipelines/templates/reactor-build-steps.yml`. Amends spec
[022 — Packaging and Distribution](022-packaging-and-distribution.md) §8
(Versioning) and §10 (Feeds and Access Control); read 022 first.

**Author:** _draft for review_
**Date:** 2026-06-08

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Goals and Non-Goals](#2-goals-and-non-goals)
3. [Key Insight: Prerelease Labels _Are_ Channels](#3-key-insight-prerelease-labels-are-channels)
4. [The Public-Access Constraint](#4-the-public-access-constraint)
5. [Proposed Channel Model](#5-proposed-channel-model)
6. [Versioning Per Channel](#6-versioning-per-channel)
7. [Pipeline Design](#7-pipeline-design)
8. [Automatic Build, Human-Approved Publish](#8-automatic-build-human-approved-publish)
9. [Consumer Experience](#9-consumer-experience)
10. [Why Not True Public Nightlies (Options Considered)](#10-why-not-true-public-nightlies-options-considered)
11. [Open Questions](#11-open-questions)
12. [Implementation Phases](#12-implementation-phases)

---

## 1. Problem Statement

We want a Chrome-style "pick your risk level" story for consuming Reactor:
a developer should be able to choose whether they track the **stable**
release, an early-access **preview**, or the bleeding-edge **nightly** build —
and express that choice declaratively in their `.csproj`.

The naive approach — publish every channel as its own stream to nuget.org —
fails on two hard constraints:

1. **nuget.org versions are immutable and permanent.** You can _unlist_ but
   never _delete_. Publishing hundreds of per-commit nightly versions there
   permanently clutters the public version history and the package's version
   dropdown. We cannot spam nuget.org.
2. **The public cannot reach our internal feed.** Reactor's Azure Artifacts
   feed lives in a Microsoft-internal Azure DevOps org and requires Microsoft
   Entra auth. An external `dotnet restore` against it gets a 401. So "publish
   nightlies to the ADO feed" only serves Microsoft-internal consumers, not the
   open-source community we are targeting at 022's P3 (public) phase.

This spec resolves how to offer channel-style consumption **within** those
constraints.

## 2. Goals and Non-Goals

### Goals

- A consumer expresses channel choice with a single `<PackageReference>` and a
  floating version range — no custom tooling.
- The public (P3) gets at least two channels: **stable** and **preview**.
- Channel cadence is automated: tags drive stable/preview; a schedule drives
  internal nightlies.
- **Public releases are automatic to build but require a human approval to
  publish.** A tag push auto-builds and signs; the irreversible nuget.org push
  waits behind a one-click approval gate so a fat-fingered tag never reaches the
  public.
- Reuse the existing `publishNuGet` / `nuGetFeed` parameters in
  `reactor-build-steps.yml` rather than inventing new publish plumbing.
  Internal nightly pushes should flow through `NuGetAuthenticate` + the build
  identity rather than a separate API key secret.
- Every channel build remains uniquely and monotonically versioned (no
  collisions on any feed).

### Non-Goals

- **Not** standing up self-hosted public-feed infrastructure (a static v3 feed
  for anonymous public nightlies) in this iteration. Ranked as a future option
  in §10, deferred until there is demonstrated demand.
- **Not** changing the package IDs or the four-package set
  (`Microsoft.UI.Reactor`, `.Advanced`, `.Devtools`, `.Templates`).
- **Not** introducing per-channel API differences. All channels build the same
  source at a point in time; the only difference is cadence, stability promise,
  and feed.

## 3. Key Insight: Prerelease Labels _Are_ Channels

NuGet has no first-class "channel" concept. The equivalent primitive is the
**SemVer prerelease label**, and the consumer selects a channel with a
**floating version range**. We do not build separate tracks; we build one
version stream where the label encodes the channel, and consumers opt in by
wildcard.

| Channel | Example version | Consumer selects with |
| --- | --- | --- |
| stable | `0.2.0` | `Version="0.*"` |
| preview | `0.2.0-preview.3` | `Version="0.*-preview*"` |
| nightly | `0.2.0-nightly.20260608.2` | `Version="0.*-nightly*"` |

The floating-label syntax (`0.*-nightly*`) is the whole trick: NuGet restores
the highest version whose prerelease label starts with the given prefix. SemVer
ordering guarantees the channels stack correctly —
`0.2.0-nightly.* < 0.2.0-preview.* < 0.2.0` — and an in-flight nightly racing
toward `0.2.0` still outranks the current stable `0.1.0`.

## 4. The Public-Access Constraint

For a public OSS package, the venues the _general public_ can `dotnet restore`
from **without authentication** are limited:

| Venue | Public? | Anonymous restore? | Floating `*-nightly*`? | Immutable clutter risk |
| --- | --- | --- | --- | --- |
| **nuget.org** | ✅ | ✅ | ✅ | ⚠️ permanent — unlist only |
| **Internal ADO feed** | ❌ (Entra auth) | ❌ | ✅ | low (retention policies) |
| **GitHub Packages (NuGet)** | ✅ | ❌ (PAT required even for public) | ✅ | medium |
| **GitHub Releases assets** | ✅ | ✅ (file download) | ❌ (pin a file) | none (deletable) |
| **Self-hosted static v3 feed** | ✅ | ✅ | ✅ | none (you own retention) |

The takeaways:

- **nuget.org is the only free, anonymous, floating-capable public feed** — but
  its permanence means it must host only curated, low-volume channels.
- **GitHub Packages is disqualified** for a frictionless public channel: it
  requires a PAT to restore even _public_ packages (a long-standing limitation).
- **The internal ADO feed is perfect for high-volume nightlies** (retention
  auto-prunes) but is invisible to the public.

This is the same compromise the .NET / EF Core / ASP.NET teams make: daily CI
builds go to an internal feed; only curated stable + preview reach nuget.org.

## 5. Proposed Channel Model

| Channel | Cadence | Versioning | Feed | Publish | Audience |
| --- | --- | --- | --- | --- | --- |
| **stable** | per release tag `vX.Y.Z` | tag verbatim (`0.2.0`) | nuget.org | human-approved | public |
| **preview** | per milestone tag `vX.Y.Z-preview.N` | tag verbatim | nuget.org | human-approved | public |
| **nightly / CI** | scheduled (daily) + per-commit on `main` | `X.Y.Z-nightly.<date>.<n>` | internal ADO feed | automatic | Microsoft insiders, dependabot-style early validation |

**Public consumers get stable + preview.** This is what ~95% of consumers
actually want, and "preview" _is_ the public early-access channel. **Nightlies
stay internal** — high churn, auto-pruned, never on nuget.org.

The "choose your channel" story for the public collapses to **stable vs
preview**, which is honest about what we can durably offer, while the team and
automated validators still get per-commit nightlies internally.

## 6. Versioning Per Channel

All channels derive their base version from [MinVer](https://github.com/adamralph/minver)
(022 §8): the next version after the latest reachable `v*` tag.

- **stable / preview** — built from an **exact tagged commit**, so MinVer emits
  the tag verbatim (`0.2.0`, `0.2.0-preview.3`). Tag-first, as today.
- **nightly / CI** — built from **untagged commits**, many per day, so the base
  needs a monotonic, collision-proof suffix. Mirror the per-PR suffix pattern
  already in `reactor-build-steps.yml`:

  ```text
  <MinVer-base>-nightly.<yyyyMMdd>.<CDP_DEFINITION_BUILD_COUNT>
  ```

  The date keeps it human-readable; `CDP_DEFINITION_BUILD_COUNT` (OneBranch's
  per-definition monotonic counter) guarantees uniqueness within a day. Example:
  `0.2.0-nightly.20260608.2`.

NuGet normalization (022 §8) still applies: numeric components must be plain
integers (leading zeros are stripped), so the date is treated as a label
segment, not a numeric component — it lives after the `-nightly.` prerelease
delimiter, which is safe.

## 7. Pipeline Design

The build seams already exist. `reactor-build-steps.yml` exposes `publishNuGet`
(bool) and `nuGetFeed` (push destination). The **internal nightly** push
reuses this build-job plumbing directly through `NuGetAuthenticate` / feed
identity. The
**public nuget.org** push is different: on the governed OneBranch pipeline,
publishing to a public, non-Azure feed is its own _release_ pathway and must run
in a dedicated release stage, not from the build job (§8.3). A channel switch
sets the version label/suffix in the resolve-version step and routes the push to
the right place.

### 7.1 Channel derivation (no new manual knob required)

Derive the channel from the trigger rather than asking the operator:

| Trigger | Channel |
| --- | --- |
| Tag push `vX.Y.Z` (no prerelease) | stable |
| Tag push `vX.Y.Z-preview.N` | preview |
| Scheduled (cron) / push to `main` | nightly |

The Official pipeline is `trigger: none` today; nightlies need a `schedules:`
cron block (or a dedicated nightly pipeline that injects the shared template
with `channel: nightly`).

### 7.2 Channel → version label

Extend the "Resolve version" step: for `nightly`, append
`-nightly.<date>.<counter>` to the MinVer base; for `stable`/`preview`, keep the
verbatim tag behavior.

### 7.3 Channel → feed + gating

| Channel | Feed | Push mechanism | Credential | Gate |
| --- | --- | --- | --- | --- |
| stable / preview | nuget.org | OneBranch release stage, `NuGetCommand@2 push` (§8.3) | nuget.org service connection | Environment + Approver Group |
| nightly | internal ADO feed | build-job push (`publishNuGet` plumbing) | `NuGetAuthenticate` / feed identity | none |

nuget.org pushes stay **gated** behind a human approval (§8) so a public release
is always deliberate. Internal nightly pushes run unattended on the scheduled
pipeline since the ADO feed is low-stakes and auto-pruned — and, being an Azure
Artifacts feed, they are a normal build-job operation rather than a governed
_release_.

## 8. Automatic Build, Human-Approved Publish

The release model is **auto-build, human-approved publish**: a tag push triggers
an automatic build + ESRP sign + pack, then the run **pauses** at the nuget.org
publish stage until a maintainer approves. The build is automatic; the
irreversible public push is not.

### 8.1 Why gate the publish (and only the publish)

A nuget.org version is **permanent** — you can _unlist_ it but never delete,
overwrite, or reuse the version number. The approval gate converts an
irreversible public mistake into a reversible local one:

- **A tag is cheap and easy to get wrong.** `git push --tags` pushes _every_
  local tag; a typo (`v0.20` instead of `v0.2.0`) or a stale experimental tag in
  someone's clone matches the `v*` trigger.
- **Signing proves authenticity, not quality.** A green, ESRP-signed build is
  not evidence the release is _good_ — only that the bits are ours.
- **The gate is the last reversible point.** Up to the push, everything (tag,
  build, signed artifacts) can be discarded. After the push, the version exists
  forever.

Nightly → internal feed is **not** gated: it is auto-pruned, not public, and a
bad nightly simply ages out.

### 8.2 What happens on a fat-fingered tag

Walk a typo (`v0.20`, meant `v0.2.0`) through the stages:

| Stage | Result | Reversible? |
| --- | --- | --- |
| Trigger fires | Bad tag matches `v*`, pipeline queues | yes — starting a build is free |
| Build + sign + pack | MinVer emits the typo'd version; bits sit in the artifact drop | yes — nothing public |
| **Publish gate** | Run **pauses**; approver sees "Publish `<version>` to nuget.org?" and **rejects** | yes — delete tag, re-push correct one |
| (if approved anyway) | Version is on nuget.org **permanently** | no — unlist + supersede only |

The gate catches the typo at the one moment it is most visible: the resolved
version is shown in the approval prompt. **Recovery if it slips through:** unlist
the bad version (hides it from search and the version dropdown; already-pinned
consumers can still restore it), then publish a correct higher version that
floating ranges prefer. The dead version number is a permanent scar but causes
no functional harm once superseded.

### 8.3 Mechanics: OneBranch release stage + Environment approval

On the governed OneBranch pipeline the publish is _not_ a plain stage running
`dotnet nuget push`. Publishing to a public, non-Azure feed (nuget.org) is
OneBranch's **"Deploy Box Products"** pathway, declared with
`parameters.release.category: NonAzure`, and the push must run in a dedicated
_release_ stage with its own rules. The two-stage shape:

```yaml
extends:
  template: v2/OneBranch.Official.CrossPlat.yml@templates
  parameters:
    release:
      category: NonAzure          # opt into the box-products release pathway
    stages:
    - stage: build                # tag-triggered, runs automatically
      jobs:
      - job: main
        pool: { type: windows }
        steps:
        - template: /build/pipelines/templates/reactor-build-steps.yml@self
          parameters: { pack: true, sign: true, publishNuGet: false }
        # build stage emits the signed .nupkg as a named pipeline artifact

    - stage: publish
      dependsOn: build
      condition: <tag build>
      variables:
        ob_release_environment: Production   # mandatory on a release stage
      jobs:
      - job: push
        templateContext:
          inputs:                            # declarative artifact download
          - input: pipelineArtifact          # no checkout / no download task
            artifactName: drop_nupkg
        pool:
          type: release                      # marks this as a release job
        steps:
        - task: NuGetCommand@2               # dotnet nuget push is NOT allowed
          inputs:
            command: push
            packagesToPush: '$(Pipeline.Workspace)/**/*.nupkg'
            nuGetFeedType: external
            publishFeedCredentials: <nuget.org service connection>
```

OneBranch-imposed rules that shape the above:

- **`pool.type: release`** plus the mandatory `ob_release_environment` stage
  variable is what makes a stage a _release_ stage. Public production pushes use
  `Production`.
- **No source build, no `checkout`** — a release stage may only consume
  artifacts already produced and binary-scanned by a build job; OneBranch blocks
  `checkout` automatically.
- **No manual artifact download** — `- download:` / `DownloadPipelineArtifact`
  are blocked. Artifacts are declared via `templateContext.inputs`, auto-download
  to `$(Pipeline.Workspace)`, and get SBOM + governed-build validation injected.
- **Restricted task list** — the push uses the allowed **`NuGetCommand@2`** task
  with `command: push`; `dotnet nuget push` (a raw CLI/script) is _not_ on the
  box-products allow-list. nuget.org credentials come from a **service
  connection** (`publishFeedCredentials`), not a `NUGET_API_KEY` env var.

The approval gate itself is a standard [ADO Environment](https://learn.microsoft.com/azure/devops/pipelines/process/environments)
bound to the release stage with a **OneBranch Approver Group** as the required
check:

- The build stage runs automatically and publishes the signed `.nupkg` artifact.
- The publish (release) stage `dependsOn` build and binds to the
  `microsoft-ui-reactor-release` ADO Environment via `ob_deploymentjob_environment`
  (the project-unique Environment _resource_; distinct from `ob_release_environment:
  Production`, which is the OneBranch release-ring classification).
- ADO **pauses** the run at that boundary and notifies approvers. The agent is
  released during the wait — no pool slot is held.
- Approve → the release stage runs `NuGetCommand@2 push`. Reject → the run ends,
  nothing publishes.
- Approvals **time out** (default 30 days, configurable). If nobody acts, the
  stage auto-rejects and the run fails — it does not wait forever.

Because the signed artifacts persist for the run's retention period, an approval
minutes or hours later still publishes the exact bits signed at tag time.

> **Implementation note.** Create the `microsoft-ui-reactor-release` ADO Environment and add
> a OneBranch Approver Group check on it, then bind the release stage to it (via the
> two stage variables the box-products docs describe, `ob_release_usedeploymentjob`
> and `ob_deploymentjob_environment`). The Environment name is project-unique — in a
> shared ADO project it must not collide with other teams' environments, and it is
> _not_ the fixed release-ring set (`ob_release_environment: Production` carries that).
> Provision a nuget.org NuGet **service connection** holding the API key and reference it as
> `publishFeedCredentials` — the key is _not_ passed as a YAML/secret env var.
> Surface the resolved version in the stage/run display name so the approver can
> eyeball it before clicking.

### 8.4 Pre-publish compliance gate (NuGet.org Microsoft policy)

The approval gate (§8.1) makes the publish _deliberate_; it does **not** make it
_valid_. A maintainer approving "Publish `0.2.0-preview.3` to nuget.org?" cannot
see, from the prompt, that one of the four packages is missing required
metadata. nuget.org enforces the
[Microsoft NuGet compliance policy](https://aka.ms/Microsoft-NuGet-Compliance)
(ProjectUrl, license, `Microsoft` authors, an approved copyright notice, …) on
**every** Microsoft-signed package, and rejects a non-compliant one with an
HTTP 400 **at push time**.

That is the worst possible moment to find out, because **the push loop is not
atomic.** `NuGetCommand@2 push` iterates the packages one at a time, so a
rejection on the _last_ package lands after the earlier ones are already public
and permanent (§8.1 — unlist-only). This actually happened: build 118951 pushed
`Microsoft.UI.Reactor`, `.Advanced`, and `.Devtools` successfully, then failed on
`Microsoft.UI.Reactor.ProjectTemplates` ("The package metadata is missing
required ProjectUrl"), leaving a half-published `preview.3`.

To convert that irreversible publish-time failure into a cheap build-time one,
the **build stage** runs the official Microsoft verifier **before** the gate:

- **Step:** `Verify NuGet.org Microsoft compliance (NuGet.VerifyMicrosoftPackage)`
  in `build/pipelines/templates/reactor-build-steps.yml`, gated by `pack: true`,
  positioned after the last `dotnet pack` and before any publish.
- **Tool:** [`NuGet.VerifyMicrosoftPackage`](https://github.com/NuGet/NuGetGallery/tree/main/src/VerifyMicrosoftPackage)
  — the NuGetGallery team's own verifier, so the rule set never drifts from what
  nuget.org actually enforces. It is a `net472` console exe shipped _inside_ the
  `1.0.0` package's `tools/` folder (not a `dotnet tool`). The sealed build
  container can't reach nuget.org, so the package is seeded into the internal
  feed and restored via a throwaway `PackageReference` project; the step then
  runs `NuGet.VerifyMicrosoftPackage.exe out\nupkg\*.nupkg` (the `*.nupkg`
  wildcard correctly excludes `.snupkg`).
- **Effect:** any non-compliant package makes the tool exit `1`, which fails the
  build stage **before** the artifact ever reaches the publish stage. A
  metadata regression can no longer produce a partial public release; it shows
  up as a red build on the PR/tag instead.

The durable fix for the specific ProjectUrl gap is centralized in the root
`Directory.Build.props` (guarded `PackageProjectUrl` / `RepositoryUrl` /
`RepositoryType` defaults) so every current and future packable project inherits
compliant metadata; this verifier step is the backstop that proves it for every
package on every build.

## 9. Consumer Experience

```xml
<!-- Conservative: stable only -->
<PackageReference Include="Microsoft.UI.Reactor" Version="0.*" />

<!-- Early adopter: latest public preview (nuget.org) -->
<PackageReference Include="Microsoft.UI.Reactor" Version="0.*-preview*" />

<!-- Microsoft insiders only: last night's build (needs the internal feed as a source) -->
<PackageReference Include="Microsoft.UI.Reactor" Version="0.*-nightly*" />
```

The nightly line additionally requires the internal feed in `nuget.config`:

```xml
<configuration>
  <packageSources>
    <add key="reactor-internal"
         value="https://pkgs.dev.azure.com/microsoft/.../microsoft-ui-reactor/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

**Reproducibility caveat for nightly.** Floating to `-nightly*` means _every
restore can pull a newer build_ — excellent for catching regressions early, but
non-reproducible unless the consumer also commits a lock file
(`packages.lock.json`). This should be documented prominently for the nightly
channel; stable/preview consumers who pin exact versions are unaffected.

## 10. Why Not True Public Nightlies (Options Considered)

If genuine **public, anonymous, floating** nightlies become a requirement, the
options, ranked:

1. **Frequent public previews instead of nightlies (recommended).** Publish
   `-preview.N` to nuget.org on a cadence we will keep forever (e.g. weekly or
   per merged feature). Fewer, curated, still early-access, no permanent
   per-commit clutter. Zero new infrastructure — this is the §5 model.
2. **GitHub Releases as the nightly channel.** Cut a `nightly` pre-release and
   attach the `.nupkg` files (we already attach packages to Releases in
   `release.yml`). Power users drop the nupkg into a local-folder feed. Public,
   anonymous, deletable (auto-cleans), no nuget.org pollution. Downside: no
   floating `*-nightly*` — consumers pin a downloaded file.
3. **Self-hosted static v3 feed.** Host a flat-container index on GitHub Pages
   or blob storage (e.g. via `Sleet` or `BaGet`). Gives true public, anonymous,
   floating `*-nightly*` restore — the most Chrome-like outcome. Downside: real
   infrastructure we own, secure, and operate. Overkill until nightlies are a
   core adoption driver.
4. **GitHub Packages.** Rejected — the mandatory-PAT-to-restore requirement
   defeats the "frictionless public channel" goal.

Recommendation: ship §5 now (option 1's preview channel covers public
early-access), keep nightlies internal, and revisit option 3 only if demand
materializes.

## 11. Open Questions

- **Nightly trigger home.** Add a `schedules:` cron to the existing Official
  pipeline (branching behavior on channel), or stand up a separate, simpler
  `OneBranch.Nightly.Reactor.yml` that injects the shared template with
  `channel: nightly`? A separate pipeline keeps the release pipeline focused and
  avoids accidental nuget.org pushes from a scheduled run.
- **Approver group for `microsoft-ui-reactor-release`.** Who can approve a public push?
  Define the OneBranch Approver Group before Phase A goes live (§8.3).
- **nuget.org credential vehicle.** A NuGet _service connection_ (holding the
  API key, referenced as `publishFeedCredentials`) vs. **ESRP Release** for the
  box-products push. The service connection is simpler; ESRP Release is the
  heavier compliance path. Pick before provisioning (§8.3).
- **Internal feed retention policy.** Confirm the ADO feed's retention count so
  nightlies prune automatically and don't grow unbounded.
- **Preview cadence commitment.** Weekly? Per-feature? This is the public
  early-access SLA and should be set deliberately (it is permanent on
  nuget.org).
- **Lock-file guidance.** Decide whether to actively recommend
  `packages.lock.json` for nightly consumers in the docs, or just warn.
- **Channel label spelling.** `nightly` vs `ci` vs `dev` for the internal label;
  `preview` vs `beta` for the public early-access label. Pick once — the label
  is part of the public contract (`0.*-preview*` ranges depend on it).

## 12. Implementation Phases

### Phase A — Stable + preview to nuget.org (auto-build, approved publish)

1. Add a `tags: include: ['v*']` trigger to the Official pipeline so a tag push
   auto-builds + signs (replacing today's manual queue).
2. Add `parameters.release.category: NonAzure` and split the run into a `build`
   stage (build + sign + pack, emitting the signed `.nupkg` as a named pipeline
   artifact) and a `publish` _release stage_ (`pool.type: release`,
   `ob_release_environment: Production`) that consumes the artifact via
   `templateContext.inputs` and pushes with `NuGetCommand@2` (§8.3).
3. Provision the portal-side prerequisites: the `microsoft-ui-reactor-release` ADO
   Environment with a OneBranch Approver Group check, and a nuget.org NuGet
   **service connection** referenced as `publishFeedCredentials`.

After this, cutting a public release is: push a `vX.Y.Z` (stable) or
`vX.Y.Z-preview.N` (preview) tag, then approve the pending publish. The
`publishNuGet` build-job plumbing is retained for the internal nightly push
(Phase B) and ad-hoc off-tag internal-feed runs; public nuget.org pushes go
through the release stage.

### Phase B — Internal nightly channel

1. Add the channel derivation + `-nightly.<date>.<counter>` suffix to the
   resolve-version step.
2. Add a scheduled trigger (cron) that builds `main` with `channel: nightly`,
   `publishNuGet: true`, `nuGetFeed: <internal ADO feed>`.
3. Document the internal-feed `nuget.config` + `0.*-nightly*` consumption for
   insiders.

Implemented shape:

- `build/pipelines/OneBranch.Nightly.Reactor.yml` schedules the nightly run on
  `main` and passes `channel: nightly` + `publishNuGet: true` into the shared
  build template.
- The shared publish step uses `NuGetAuthenticate` / feed identity and pushes to
  the internal Azure Artifacts feed with `dotnet nuget push --api-key az`.

### Phase C — (deferred) Public nightly

Only if demand appears: implement §10 option 3 (self-hosted static v3 feed) and
add a publish target for it. Out of scope for the initial rollout.
