# Wafish.Plugin.RateLimit

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This plugin limits route traffic inside one gateway process. Its binding ID is `rate-limit`.

```mermaid
flowchart TD
    Request["Route request"] --> Partition["Choose local limiter partition"]
    Partition --> Acquire["Acquire permit or wait in configured queue"]
    Acquire --> Decision{"Permit acquired?"}
    Decision -->|"Yes"| Upstream["Forward upstream"]
    Decision -->|"No"| Reject["Return HTTP 429"]
```

## Real-world example

A login route allows a limited request rate per client address. Excess requests receive HTTP 429 with retry guidance.

## Real-world value

A single gateway can protect upstream capacity and reduce abusive request bursts.

## What it does

- Support fixed-window, sliding-window, token-bucket, and concurrency algorithms.
- Partition limits using supported route policy keys.
- Share a named local quota across selected routes.
- Reject exhausted limits with JSON and Retry-After guidance.

## What it does not do

- Share counters between gateway replicas.
- Authenticate users or replace authorization checks.

## Start here

Source project: [Wafish.Plugin.RateLimit.csproj](Wafish.Plugin.RateLimit.csproj).
Direct project references: [Wafish.Plugin.Abstractions](../Wafish.Plugin.Abstractions/README.md), [Wafish](../Wafish/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../PLAN.md).

## Review safety limits

The built-in store admits at most 2,000 partitions. New partitions receive HTTP 429 at capacity.
Route partitions use route identity. Variable paths share the route quota.
See [resource limits and consumer changes](../../docs/security.md#request-resource-limits-and-retry-behavior).

## Review items 14–16

The injected `TimeProvider` controls middleware cleanup scheduling and store idle retention.
Built-in .NET limiter replenishment retains its own system timers. Redis quota timing remains server-owned.

Verification: [review evidence](../../ARCHIVE.md#archive-tests-wafish-regression-evidence-review141516-readme-md).

## Algorithm boundaries

Retry-After uses limiter metadata when available. Token-bucket fallback uses its replenishment period.
Concurrency limits return a one-second retry hint. A hint does not reserve a permit.
Cancellation releases queued ownership. Active partitions survive cleanup and remain owned until their requests finish.
See [algorithm and partition stress checks](../../ARCHIVE.md#archive-tests-wafish-regression-evidence-lifetime-stress-readme-md).

## Named route quotas

Set the same `quotaGroup` on each participating route's `rate-limit` binding:

```json
{
  "enabled": true,
  "quotaGroup": "support-api",
  "algorithm": "FixedWindow",
  "partitionKey": "UserId",
  "permitLimit": 20,
  "window": "1m",
  "queueLimit": 0
}
```

This example permits 20 requests per user per minute across all participating routes.
`UserId` uses `HttpContext.User.Identity.Name`. Requests without that name share the `anonymous` partition.
Authentication must populate the identity before rate limiting.

| Partition key | Named group behavior |
|---|---|
| `Global` or `Route` | One shared pool across the group's routes |
| `IpAddress` | One shared pool per resolved client address |
| `UserId` | One shared pool per identity name |

Different groups and bindings without `quotaGroup` keep separate counters.
The group name replaces route identity; route renames and binding recreation preserve its counters.
Counters remain process-local. Gateway replicas and restarts do not share this state.

Names contain 1–64 lowercase letters, digits, or hyphens, and start with a letter.
Members must use identical effective policies, including algorithm, partition key, queue size, and algorithm-specific settings.
Equivalent durations, such as `1m` and `00:01:00`, match.
Unused algorithm settings do not affect policy identity. Each grouped route allows one enabled local rate-limit binding.

Named quotas require permits or tokens from 1 to 1,000,000 and queues from 0 to 10,000.
Windows and replenishment periods range from 1 millisecond to 1 day.
Use invariant TimeSpan syntax or numeric `s`, `m`, `h`, or `d` suffixes.
Sliding windows require 2–100 segments. Existing ungrouped bindings retain their previous validation behavior.

### Policy changes and removal

Conflicting policies fail validation before persistence. Preview does not allocate counters.
Removing all bindings does not immediately release the group or reset consumed permits.
The store retains policy ownership until every partition has no active or queued requests and is fully replenished.
Both last access and limiter idle time must exceed five minutes before cleanup can remove a partition.
Cleanup runs on rate-limited requests, at five-minute intervals. An idle gateway can retain groups longer.

To change a retained policy, remove its bindings and allow its partitions to retire before reusing the name.
A different group name creates an explicitly separate quota. Restarting the gateway also clears local counters.
A request racing with a conflicting configuration receives HTTP 503 with a one-second retry hint.
It cannot create a second counter set. Exhausted permits or partition capacity return HTTP 429.

The 2,000-partition store limit covers named and ungrouped counters together.
Custom stores must implement [IGroupedRateLimiterStore](../Wafish/Infrastructure/State/IGroupedRateLimiterStore.cs) to support named quotas.
Stores implementing only `IRateLimiterStore` continue to support ungrouped bindings.

Source: [group compiler](RateLimitGroupCompiler.cs), [policy normalization](../Wafish/Models/LocalQuotaGroupPolicy.cs),
and [regression checks](../../tests/Wafish.Regression/NamedQuotaChecks.cs).
