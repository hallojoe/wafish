---
name: wafish-build
description: Build Wafish projects, embedded React assets, or the website; diagnose repository SDK and build wiring. Use wafish-test for regression execution and wafish-diagnostics for running gateways.
---

# Build Wafish

Paths below are relative to the repository root. Read the affected project's `AGENTS.md` first.

1. Inspect `global.json`, `Directory.Packages.props`, and the affected project before choosing tools or changing versions.
   Confirm `dotnet --version` resolves a stable .NET 10 SDK. A newer installed major does not satisfy this repository.
   Use `setup-local-sdk` if a suitable SDK is missing; keep installation and environment changes local to the task.
2. Use Node.js 24 and the committed npm lockfiles. Locate the frontend from the project being built.
   The embedded console is `src/Wafish.ReactUi/ClientApp`; the public site is `website`.
   Use `npm ci` for missing dependencies. Do not replace lockfiles merely to repair a local installation.
3. Build with `dotnet build Wafish.slnx -c Release` when validating solution wiring or executable child hosts.
   A focused project build can precede it while fixing errors. Use `msbuild` for imports, targets, or binary-log analysis.
   Check the command's exit code and final summary; informational Sonar target messages alone do not establish failure.
4. For direct console work, use `npm --prefix src/Wafish.ReactUi/ClientApp run build`.
   For the site, use `npm --prefix website run build`; this also generates assets and checks rendered links.
   Read `src/Wafish.ReactUi/README.md` for build fingerprints and embedding requirements.
5. Confirm the intended assets were built before using `--no-build` in checks or launching child hosts.
   Report the exact failed project or build step when a prerequisite blocks verification.

Do not edit `bin`, `obj`, `node_modules`, `coverage`, or embedded `wwwroot` output by hand.
`brand/logo.png` remains a package-icon input even when README branding changes.
Keep package versions centralized. Do not upgrade frameworks or dependencies as an incidental build repair.
For AppHost lifecycle work, use the installed Aspire skills; a successful AppHost build does not start the topology.

## Validation

Check the exit status, relevant warnings, and expected output. Use `wafish-test` to select checks for behavior changes.
Do not report a build as passing while its process is still running.
