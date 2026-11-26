# Wafish.Administration

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This project provides administration APIs, authentication, realtime updates, and optional AI and MCP services.

```mermaid
flowchart LR
    P0["Authenticated operator client"]
    P1["Administration APIs and hubs"]
    P2["Runtime configuration services"]
    P3["Storage repositories"]
    P0 --> P1
    P1 --> P2
    P2 --> P3
```

## Real-world example

An operator changes a route, checks the resulting revision, and inspects its request logs through an authenticated client.

## Real-world value

Operators get one management boundary that can support different user interfaces.

## What it does

- Manage routes, clusters, bindings, presets, logs, history, and settings.
- Protect administration endpoints and realtime hubs.
- Register AI and MCP only through their explicit feature methods.

## What it does not do

- Embed React pages or depend on the removed Razor dashboard.
- Provide independent remote control of another gateway process.

## Start here

Source project: [Wafish.Administration.csproj](Wafish.Administration.csproj).
Direct project references: [Wafish.Runtime](../Wafish.Runtime/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../PLAN.md).

## Review safety limits

Authentication attempts use bounded source and account windows. Exhaustion returns HTTP 429 with Retry-After.
See [resource limits and consumer changes](../../docs/security.md#request-resource-limits-and-retry-behavior).

## Atomic binding publication

Single and batch binding writes share the runtime publication gate.
Batches commit through revision-checked SQLite transactions. Partial failures leave active state unchanged.
Activation compensation cannot overwrite a newer successful storage revision.

## Notification accounting

`NotificationDispatcher.GetSnapshot()` reports submitted, queued, active, delivered, failed, discarded, and suppressed events.
The webhook settings GET response adds a top-level `dispatch` snapshot beside the existing `data` field.
Queue overflow counts the discarded oldest event. Shutdown counts abandoned queued and active events.
Rejected submissions after shutdown also count as discarded.
Missing subscriptions and cooldown filtering count as suppressed.
Partial endpoint delivery counts as one failed event. Counters measure events, not endpoint attempts.
Snapshots can change during active dispatch. At rest, every submitted event has one accounting category.

[Verification for review items 8–10](../../tests/Wafish.Regression/evidence/review8910/README.md).

Plugin mutations await asynchronous snapshot refresh after commit.
Install, uninstall, and upgrade controller methods now return `Task<IActionResult>`. Their HTTP contracts remain unchanged.
The authorization filter uses the corrected `Wafish.Dashboard` namespace.
See [verification](../../tests/Wafish.Regression/evidence/async-retry/README.md).

## Session lifecycle

Dashboard JWT sessions belong to the current host. Sign in through the login endpoint to obtain a usable token.
A signed token alone no longer authorizes administration access. Custom authorization delegates retain their own policy.
Sessions expire after eight hours. The registry permits at most 10,000 active sessions.
A full registry returns 503 for new logins without evicting active sessions.
Sign-out revokes presented bearer and cookie sessions. It closes their existing realtime connections.
Successful two-factor changes revoke every JWT session. Sign in again after changing two-factor settings.
Restarting the host invalidates its sessions, including deployments with a configured signing secret.
JWT sessions remain local to one host. Deployments with multiple hosts require session affinity.
Basic authentication and API keys remain request credentials. Their existing hub connections are rechecked each second.
Credential changes take effect during the next check; in-progress requests can finish.

Authorization covers built-in plugin controllers, including circuit-breaker actions.
Only the authentication controller's login and logout actions bypass credential checks.
See [verification](../../tests/Wafish.Regression/evidence/administration-lifecycle/README.md).

## Shutdown verification

Real overview and traffic connections close during host shutdown.
Queued and in-flight notification counts remain balanced, including cancelled deliveries.
Tests verify credential exclusion and reject success audit records before persistence commits.
See [shutdown evidence](../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).

## Shared mutation responses

Configuration and plugin writes return shared status, outcome, and nullable revision fields.
Failures use Problem Details. Endpoint-specific fields retain their locations.
HTTP `status` is now numeric; read `outcome` for the application result.
See [consumer migration and error mappings](../../docs/mutation-contracts.md).

## Operation permissions

Authentication runs before operation permissions. Existing hosts retain all four permissions by default.
Set `DashboardOptions.Auth.Permissions` to restrict shared credentials.
An omitted or null setting preserves administrator access. An empty array grants no permissions.
For configuration files, use `Gateway:Dashboard:Auth:Permissions`, for example `["Read"]`.
Supported values are `Read`, `ConfigurationWrite`, `PluginManagement`, and `CredentialManagement`.
Authenticated principals can supply `wafish.permission` claims with these exact names.
When present, these claims replace the shared permissions.
`Auth.AuthorizeOperation` overrides both policies and receives the request cancellation token.
Custom authentication must set an authenticated principal with a stable name for per-user rules.

Read permits inspection, configuration previews, and realtime subscriptions.
Configuration writes require `ConfigurationWrite`; plugin changes and activation previews require `PluginManagement`.
Credential management includes two-factor setup, AI connection settings, webhook secrets, and database downloads.
Denied operations record their actor, target operation, and required permission in the audit log.
Realtime permissions are rechecked each second and at subscription changes.
AI tools and MCP requests use the same policy. Direct in-process tool calls remain trusted.
Login and logout retain their public authentication behavior.

The new endpoints are `POST api/config/changes/preview`, `POST api/config/changes`, and `POST api/plugins/preview`.
The configured administration prefix applies. See [request examples](../../docs/configuration-changes.http) and [workflow](../../docs/configuration-changes.md).

## Operational controls

Protected endpoints expose latency buckets, cache purge, plugin drain progress, and redacted diagnostic bundles.
Binding previews expose WAF exclusions. Binding mutations audit exclusion changes, including batch operations.
Backup downloads use the provider-owned backup contract.

See [registration and operations](../../docs/registration-operations-contracts.md) for limits, configuration, and compatibility.

## Atomic change sets

Versioned change sets combine routes, clusters, and plugin bindings in one transaction.
See [change-set contracts](../../docs/configuration-policy-contracts.md) for API examples, revisions, permissions, and recovery.

Binding previews validate proposed replacements against all stored bindings under the publication gate.
Named quota conflicts return HTTP 400 with `ValidationRejected`; previews and failed saves leave stored and active settings unchanged.

## Optional passkeys (R46)

JWT administration can use registered WebAuthn authenticators as an additional sign-in method.
Passkeys are disabled by default. API-key, Basic, anonymous, and custom-delegate authentication do not enable them.

```json
{
  "Gateway": {
    "Dashboard": {
      "Auth": {
        "AuthMode": "DefaultJwt",
        "Passkeys": {
          "Enabled": true,
          "RelyingPartyId": "gateway.example.com",
          "Origin": "https://gateway.example.com",
          "StatePath": "/protected/wafish/passkeys.json"
        }
      }
    }
  }
}
```

Supply the existing JWT password and signing secret through protected host configuration.
The origin includes its non-default port, if any. Its hostname must equal the relying-party ID.
HTTPS is required, except HTTP localhost for development. Configuration changes require a restart.
A changed origin, relying party, or account cannot reuse the old credential file.

Sign in with the existing password, then open Account and register a named authenticator.
Enrollment and revocation require `CredentialManagement` and an active JWT session.
Enrollment challenges are bound to that session. Authenticator user verification is required for enrollment and login.
Passkey login can replace password and TOTP entry. Password login retains its configured TOTP requirement for recovery.
Revoking any passkey invalidates all sessions and pending ceremonies. Password recovery can register a replacement.

Credential state contains public keys, names, and signature counters. Private keys remain with authenticators.
Use a protected persistent directory owned by one gateway process. Do not share the file between hosts.
Writes replace the file atomically. New Unix files allow only owner read/write access.
Protect the directory against replacement or modification by other users. On Windows, configure an equivalent filesystem ACL.
Corrupt, oversized, or reassigned state rejects startup rather than silently discarding credentials.
Back up this file with the host's protected configuration.

Challenges expire after two minutes and are consumed once, including failed completions.
The service permits at most 128 pending challenges and eight credentials.
Login attempts use the existing authentication throttles. Restart discards pending challenges and sessions.
The server verifies challenge, origin, relying party, user verification, signature, and signature counter through
[FIDO2 .NET 4.3.3](https://github.com/passwordless-lib/fido2-net-lib/releases/tag/4.3.3).
No attestation-provider trust policy is imposed. This is optional account authentication, not device inventory or attested hardware enforcement.

Endpoints use the configured administration prefix:

| Method | Path | Purpose |
|---|---|---|
| GET | `api/passkeys` | List credential names and IDs |
| GET | `api/passkeys/credential?id=...` | Read one public descriptor |
| POST | `api/passkeys/registration/options` | Start session-bound enrollment |
| POST | `api/passkeys/registration` | Verify enrollment; returns 201 and Location |
| POST | `api/passkeys/login/options` | Start public login |
| POST | `api/passkeys/login` | Verify assertion and issue the existing session cookie |
| POST | `api/passkeys/revoke` | Revoke a credential and invalidate sessions |

Only the two login endpoints bypass session authorization. The control-plane network policy still applies.
Disabled passkey actions return 404 after their applicable authorization checks.
Verification failures return 400, unavailable operations return 409, and throttling returns 429 with retry information.

## Saved operator views (R55)

Authenticated accounts can save named log and audit views through `api/operator-views`.
GET lists the current account's views. POST creates one and returns HTTP 201 with its location.
GET, PUT, and DELETE on `api/operator-views/{id}` access only the current account's view.
An identifier owned by another account returns HTTP 404.
Personal preference writes require `Read` permission. They do not change gateway configuration.

The server derives ownership from validated authentication, never from request fields.
Custom authorization must provide one authenticated identity with a stable subject or name claim.
Anonymous or unidentified custom callers cannot own private views.
Shared credentials represent one shared account. Operators using the same credentials share that account's views.
Distinct authenticated accounts have separate preferences.

Views store only names, workspace identifiers, bounded filters, column choices, and update times.
They contain no authentication tokens, passwords, or API keys.
Limits are 20 views per account, 128 accounts per file, and 4 MiB of protected state.
Deleting an account's final view releases its file slot.
Names have a 64-character limit. Filter text and route IDs have limits of 200 and 128 characters.
Column names are restricted to the selected workspace. Unknown request fields are rejected.

`Gateway:Dashboard:OperatorViewsStatePath` optionally selects a dedicated file.
The default is `Auth:TwoFactorStatePath` with `.views` appended.
Give each gateway its own file. Persist its ASP.NET Core Data Protection keys across restarts.
Writes use protected content, private file permissions on Unix, and atomic replacement.
Storage failures return HTTP 503 and preserve the previous committed file.
Corrupt or oversized state is rejected without being overwritten.
Responses disable caching.
