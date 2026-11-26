---
name: wafish-packages
description: Build and verify Wafish NuGet packages, consumer profiles, metadata, and API compatibility. Use for package preparation or an explicitly authorized release task; not automatic publication after builds.
---

# Prepare Wafish packages

1. Read `docs/package-publishing.md`, current package properties, and the selected workflow.
   Inspect `src/Directory.Build.props` for application package metadata and `Directory.Packages.props` for dependency versions.
   The version in `apm.yml` describes the agent package. It does not set NuGet package versions.
2. Verify how the current workflow handles version input. Do not assume the pipeline increments source versions.
   Distinguish source metadata, release tags, staged artifacts, and versions actually available on the requested feed.
3. Build Release outputs before package checks. Use `scripts/verify-packages.py` for isolated local consumers.
   Follow `tests/Wafish.PackageChecks/README.md` for public API and negative compatibility checks.
   Preserve reviewed API and protobuf snapshots; a failing check is not permission to overwrite them.
4. Inspect package metadata, dependencies, embedded React assets, native SQLite loading, and package icons as applicable.
   The root `logo.png` remains required for packaging. README display choices do not remove that dependency.
   Local staging and consumer verification do not prove publication or support on untested platforms.
5. For an explicitly authorized publication task, verify the selected registry, version, source commit, and credentials.
   Use `nuget-trusted-publishing` when setting up NuGet.org OIDC publishing, not for unrelated private feeds.
   Use current workflow guidance for rehearsals and actual uploads. Do not replace published package versions or rewrite release history.

## Validation

Record source revision, package versions, checks, and artifact identities.
Report feed publication only after verifying the uploaded versions on the requested feed.
Keep historical release narratives in `ARCHIVE.md`; keep current compatibility and publishing guidance in their maintained documents.
