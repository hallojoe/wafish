# Wafish.ReactUi

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This project embeds the React administration workspace in an ASP.NET Core package.

```mermaid
flowchart LR
    P0["Operator browser"]
    P1["Embedded React workspace"]
    P2["Authenticated administration APIs"]
    P3["Runtime and storage"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

An operator opens `/console`, signs in, edits a route, and sees whether the saved configuration became active.

## Real-world value

A host can ship a browser interface without deploying a separate frontend server.

## What it does

- Build React assets and embed them in the .NET assembly.
- Serve the workspace and provide host-specific API configuration.
- Support administration forms, logs, history, plugins, settings, and AI workflows.

## What it does not do

- Provide the administration API implementation itself.
- Require the removed Razor dashboard or store credentials in browser local storage.

## Start here

Source project: [Wafish.ReactUi.csproj](Wafish.ReactUi.csproj).
Direct project references: [Wafish.Administration](../Wafish.Administration/README.md).

Node.js is required for frontend builds. See [frontend instructions](ClientApp/README.md).
Browser tests start isolated hosts on ports 5219–5224.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../PLAN.md).

## Deployment prefixes and build validation

Configure the host's `UsePathBase` before routing and Wafish middleware.
The shell includes `Request.PathBase` in its UI base, API base, and HTML base URL.
For example, `/edge`, `manage/ui`, and `manage/api` produce URLs below `/edge/manage/`.
Asset routes remain relative to the host routing path. Injected HTML paths are encoded.
JWT, BasicAuth, API-key login, navigation, and realtime connections pass under this configuration.

Frontend inputs include package scripts, lockfiles, source files, public assets, configuration, and build scripts.
An input-list record detects added and removed source files.
Vite emits an asset manifest. Builds and packing reject missing entry, stylesheet, font, chunk, or manifest assets.
`SkipReactBuild=true` skips generation but retains asset validation. Node.js remains required for validation.
The package icon is staged from the canonical root `logo.png`.
Browser fixtures use ports 5219–5224, including three prefixed authentication hosts.

[Verification](../../tests/Wafish.Regression/evidence/review111213/README.md).

Browser verification covers HTTP and HTTPS with nested prefixes for JWT, Basic authentication, and API keys.
Two-factor changes invalidate the current JWT session. The workspace requires a new sign-in.
The browser runner owns fixture process and temporary-state cleanup.
See [verification](../../tests/Wafish.Regression/evidence/administration-lifecycle/README.md).

Mutation errors accept named `outcome` fields and legacy `status` values.
Unknown revisions remain hidden. Validation, conflicts, storage failures, and activation failures retain distinct messages.
See [HTTP migration](../../docs/mutation-contracts.md#consumer-migration).

## Workspace state verification

The development client and browser fixtures use `/wafish` for administration APIs.
Hosted clients use the API prefix supplied by their host. The default workspace path remains `/console`.

Page component checks cover all 18 workspaces, including route/cluster and log/audit variants.
They exercise pending reads, failed reads, recovery, cancellation, stale results, and editable drafts.
Assistant session and message panels show loading and failure states with retry controls.
Overview events update the same locale-specific query that the page displays.
Browser checks cover validation dialogs, realtime disconnection, and mobile navigation with keyboard and axe assertions.
Large-list and plugin profiles verify pagination and seeded bindings.
Process-selected failure profiles verify real mutation envelopes and delayed draft preservation.
See [infrastructure evidence](../../tests/Wafish.Regression/evidence/regression-infrastructure/README.md).

## Build identity

The header, sign-in view, and favicon use the shared Wafish routing mark.
Editable masters and usage guidance are in [brand](../../brand/README.md).

`GET /console/version` returns package version, asset SHA-256, and administration contract version 1.
The configured UI prefix and PathBase also apply to this endpoint. Control-plane access policy remains enforced.
The shell includes the same metadata in `app-config.build`. Responses use `Cache-Control: no-store`.
The hash covers ordered embedded resource names, lengths, and bytes, excluding timestamps and machine paths.
Identical assets produce identical hashes. Keep UI and administration packages on matching releases.

## Passkeys

Configured JWT hosts expose passkey registration and revocation on Account, plus a passkey sign-in button.
Password recovery remains available. Revocation signs out all sessions.
The browser uses native WebAuthn JSON conversion methods and requires a secure context.
Older browsers without these methods retain password sign-in.
See [configuration and limits](../Wafish.Administration/README.md#optional-passkeys-r46),
[browser conversion](https://developer.mozilla.org/en-US/docs/Web/API/PublicKeyCredential/parseCreationOptionsFromJSON_static),
and [credential serialization](https://developer.mozilla.org/en-US/docs/Web/API/PublicKeyCredential/toJSON).
