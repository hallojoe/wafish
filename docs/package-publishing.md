# Package publishing

Use `publish-packages` to publish all 19 packages.
Use a package-specific workflow to publish one package.
`publish-packages` starts automatically when a GitHub release is published, including prereleases.
All workflows also support manual runs through GitHub Actions.
They use the shared [publishing action](../.github/actions/publish-packages/action.yml).
Pushes, pull requests, and draft releases do not start publication.

## Registry setup

### NuGet.org

1. Create the GitHub Actions environment `package-publishing`.
2. Set `NUGET_USER` as a repository or environment variable. Use the NuGet.org profile name, not an email address.
3. Configure trusted-publisher policies with repository owner `hallojoe` and repository `wafish`.
4. Set the policy environment to `package-publishing`.
5. Use the workflow filenames listed below. Enter the filename only, without `.github/workflows/`.
6. Permit new packages and new versions for the intended package scope.

For all packages, configure `publish-packages.yml` with the `Wafish*` package scope.
For individual workflows, configure each filename with its corresponding package ID.
Only configure individual policies for workflows you intend to use.
The shared action runs inside the selected workflow. Use the calling workflow filename in each policy.

Authentication uses `NuGet/login@v1` and GitHub OIDC. No stored NuGet API key is required.
See [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) for policy ownership and configuration.

### GitHub Packages

The destination is `https://nuget.pkg.github.com/hallojoe/index.json`.
Publishing uses the workflow's `GITHUB_TOKEN` with `packages: write` permission.
No personal access token is required for this repository's packages.
Existing packages must grant this repository Actions access.
Check package visibility after first publication; GitHub packages initially default to private.
See [GitHub's NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

Environment deployment restrictions can limit which branches or tags may publish.
Pushes and pull requests run only the `Build` workflow for the solution and website.
Run `Verify` manually for regression tests, dependency audits, and cross-platform package and storage checks before a release.
Publishing workflows still verify package APIs and isolated consumers before uploading packages.

## Publish a GitHub release

The release tag is the package version source. No version edit in build properties is required.

1. Merge the intended source and these publishing workflows into the release branch.
2. Create a GitHub release targeting that exact commit.
3. Choose a version tag, with or without a leading `v`.
4. Set the GitHub prerelease flag according to the table below.
5. Publish the release. The workflow builds, verifies, and publishes all 19 packages to both registries.

| Release tag | GitHub prerelease flag | Package version | Result |
|---|---|---|---|
| `1.1.0` or `v1.1.0` | Off | `1.1.0` | Stable publication |
| `1.1.0-rc.1` or `v1.1.0-rc.1` | On | `1.1.0-rc.1` | Prerelease publication |
| `1.1.0-beta.2` | On | `1.1.0-beta.2` | Prerelease publication |
| Any suffix tag | Off | — | Rejected |
| Clean tag | On | — | Rejected |

NuGet determines prerelease status from the version suffix, not the GitHub checkbox.
The workflow rejects mismatches rather than silently changing the version or release flag.
Malformed tags, build metadata, and numeric identifiers with leading zeros are rejected before builds or authentication.

The resolved version is passed as `Version` and `PackageVersion` to the build and pack commands.
Package metadata, internal package dependencies, compiled informational versions, and isolated consumers use that same version.
[src/Directory.Build.props](../src/Directory.Build.props) supplies only the fixed `0.0.0-dev` fallback for local builds.
The version in `apm.yml` remains independent of NuGet publishing.

The trigger uses `release: published`, including stable releases and prereleases published from drafts.
Draft releases and tag pushes alone do not publish packages.
See [GitHub release events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release).
Editing a release does not start another run. Correct mismatches before publishing the release.
If validation fails, correct the release settings, then run the workflow manually for the same tag.
Rerunning the original failed event uses its original release payload.

Existing published tags still identify their original commits and workflow implementations.
The historical `v1.0.0` tag does not identify the current source. This change does not move that tag or republish it.
Resolve [R56](../PLAN.md#r56) when reconciling historical releases and registry versions. Never reuse a published version for changed source.

## Run a workflow manually

1. Merge the workflow files into the default branch so manual dispatch becomes available.
2. Select `publish-packages`, or a package-specific workflow, in GitHub Actions.
3. Select the branch or tag containing the intended release source.
4. Enter the package version to build, without the optional tag prefix `v`.
5. Select `both`, `nuget`, or `github`. The default is `both`.
6. Enable `dry-run` for a build and package rehearsal. It skips registry authentication and publication.
7. Run the workflow. Inspect the summary and retained package artifact.

A manual run uses its version input without editing source metadata.
When selecting a tag, its normalized version must match the input. Branch runs use the explicit input.
Manual runs do not change GitHub release flags; NuGet prerelease status still follows the input version suffix.

Example rehearsal after these workflows reach the default branch:

```bash
gh workflow run publish-packages.yml --ref main \
  -f version=1.1.0-rc.1 -f registry=both -f dry-run=true
```

## Validation and publication

Each run builds the solution and packs all public packages.
It checks reviewed API snapshots and runs isolated runtime, administration, and UI package consumers.
This also validates related packages before an individual package is selected.
The selected artifacts must contain the expected package ID and version, including matching symbol packages.
The workflow publishes the same package files that passed validation.

The retained artifact contains `.nupkg`, `.snupkg`, an ordered package list, and a SHA-256 manifest.
The manifest records the source commit, ref, package IDs, and version.
All-package publication sends dependencies before dependent packages.
Individual workflows publish only the selected package. They do not publish its dependencies.
Use `publish-packages` for the first release; individual runs assume matching dependencies already exist in the selected registry.

NuGet.org receives packages and symbols. GitHub Packages receives `.nupkg` files only.
Symbol files remain available in the workflow artifact for both destinations.
The workflow does not create a GitHub Release or attach release assets.

Publishing workflows share one concurrency group. An active publication is not cancelled by another run.
GitHub permits only one pending run per group; a newer run can replace an older pending run.
Dispatch package workflows sequentially, or use `publish-packages`.

## Partial failures and retries

Publication is not atomic across packages or registries.
The `both` option publishes to NuGet.org first, then GitHub Packages.
A failure stops the current workflow; already published packages remain available.

Rerun the failed job from the same source revision and inputs.
Duplicate package versions are skipped. Other push failures fail the job.
NuGet symbol pushes run explicitly, allowing retries after a package succeeds but symbols fail.
Duplicate skipping does not prove existing registry files match a new local build.
Never reuse a published version for changed source.

## Workflow inventory

| Package selection | Workflow file |
|---|---|
| All packages | [publish-packages.yml](../.github/workflows/publish-packages.yml) |
| `Wafish` | [publish-wafish.yml](../.github/workflows/publish-wafish.yml) |
| `Wafish.Administration` | [publish-wafish-administration.yml](../.github/workflows/publish-wafish-administration.yml) |
| `Wafish.Client` | [publish-wafish-client.yml](../.github/workflows/publish-wafish-client.yml) |
| `Wafish.Grpc` | [publish-wafish-grpc.yml](../.github/workflows/publish-wafish-grpc.yml) |
| `Wafish.Plugin.Abstractions` | [publish-wafish-plugin-abstractions.yml](../.github/workflows/publish-wafish-plugin-abstractions.yml) |
| `Wafish.Plugin.Cache` | [publish-wafish-plugin-cache.yml](../.github/workflows/publish-wafish-plugin-cache.yml) |
| `Wafish.Plugin.CircuitBreaker` | [publish-wafish-plugin-circuitbreaker.yml](../.github/workflows/publish-wafish-plugin-circuitbreaker.yml) |
| `Wafish.Plugin.Compression` | [publish-wafish-plugin-compression.yml](../.github/workflows/publish-wafish-plugin-compression.yml) |
| `Wafish.Plugin.Metrics` | [publish-wafish-plugin-metrics.yml](../.github/workflows/publish-wafish-plugin-metrics.yml) |
| `Wafish.Plugin.ProxyLog` | [publish-wafish-plugin-proxylog.yml](../.github/workflows/publish-wafish-plugin-proxylog.yml) |
| `Wafish.Plugin.RateLimit` | [publish-wafish-plugin-ratelimit.yml](../.github/workflows/publish-wafish-plugin-ratelimit.yml) |
| `Wafish.Plugin.RateLimit.Redis` | [publish-wafish-plugin-ratelimit-redis.yml](../.github/workflows/publish-wafish-plugin-ratelimit-redis.yml) |
| `Wafish.Plugin.Retry` | [publish-wafish-plugin-retry.yml](../.github/workflows/publish-wafish-plugin-retry.yml) |
| `Wafish.Plugin.ServiceDiscovery` | [publish-wafish-plugin-servicediscovery.yml](../.github/workflows/publish-wafish-plugin-servicediscovery.yml) |
| `Wafish.Plugin.Waf` | [publish-wafish-plugin-waf.yml](../.github/workflows/publish-wafish-plugin-waf.yml) |
| `Wafish.ReactUi` | [publish-wafish-reactui.yml](../.github/workflows/publish-wafish-reactui.yml) |
| `Wafish.Runtime` | [publish-wafish-runtime.yml](../.github/workflows/publish-wafish-runtime.yml) |
| `Wafish.Storage.Abstractions` | [publish-wafish-storage-abstractions.yml](../.github/workflows/publish-wafish-storage-abstractions.yml) |
| `Wafish.Storage.Sqlite` | [publish-wafish-storage-sqlite.yml](../.github/workflows/publish-wafish-storage-sqlite.yml) |

## Local checks

These commands do not publish packages:

```bash
python3 scripts/test-publication.py
python3 scripts/prepare-publication.py --package all --version 1.1.0-rc.1 --check-only
dotnet build Wafish.slnx -c Release -p:ContinuousIntegrationBuild=true \
  -p:Version=1.1.0-rc.1 -p:PackageVersion=1.1.0-rc.1
python3 scripts/verify-packages.py --version 1.1.0-rc.1
python3 scripts/prepare-publication.py --package all --version 1.1.0-rc.1
```

For ordinary development checks, omit the version properties and `--version`; both use `0.0.0-dev`.
Rebuild before switching versions. Package verification rejects assemblies built with a different version.
