# Wafish.Plugin.RateLimit.Redis

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This plugin uses Redis Lua operations for shared route limits. Its binding ID is `rate-limit-redis`.

```mermaid
flowchart TD
    Gateways["Gateway replicas"] --> Keys["Shared Redis rate-limit keys"]
    Keys --> Lua["Atomic Lua operation"]
    Lua --> Decision{"Within limit?"}
    Decision -->|"Yes"| Forward["Forward upstream"]
    Decision -->|"No"| Reject["Return HTTP 429"]
    Lua -. "Redis unavailable" .-> Policy["Apply configured outage policy"]
```

## Real-world example

Two gateway replicas protect the same public API. Requests reaching either replica consume the same Redis-backed allowance.

## Real-world value

Horizontal scaling does not multiply the allowed traffic for each shared key.

## What it does

- Apply fixed-window, sliding-window, and token-bucket limits atomically.
- Use configured key prefixes and connection settings.
- Distinguish exhausted quotas from Redis outages using FailClosed or FailOpen policy.

## What it does not do

- Run Redis automatically outside an explicitly configured host topology.
- Implement the local concurrency-limiter algorithm in Redis.

## Start here

Source project: [Wafish.Plugin.RateLimit.Redis.csproj](Wafish.Plugin.RateLimit.Redis.csproj).
Direct project references: [Wafish.Plugin.Abstractions](../Wafish.Plugin.Abstractions/README.md), [Wafish](../Wafish/README.md).

Live Redis checks use `WAFISH_REDIS_SERVER` and the regression runner's `--redis` option.

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../PLAN.md).

## Review fixes 4–7

Redis keys include a versioned hash of algorithm, limit, window, and burst settings.
Policy changes start fresh counters. Previous keys expire under their existing TTL.
Deploy replicas together when changing this key format; mixed versions use separate counters.

## Connection recovery

Failed connection creation retries on later requests after delays of 1, 2, 4, 8, 16, then 30 seconds.
Concurrent callers share one creation task per connection string. The store retains at most 32 connection entries.
Caller cancellation stops that caller's wait. It does not cancel a shared connection attempt.
Shutdown awaits pending creation and disposes successful connections.
Established multiplexers retain their normal reconnection behavior. Command failures do not replay Lua operations.
A cancelled or interrupted command can already have consumed a permit.
`FailClosed` remains the default. Use distinct `keyPrefix` values to isolate deployments.
The default `keyPrefix` is `wafish:rl`. Changing the prefix starts separate counters.
See [naming migration](../../docs/naming-migration.md#repository-branding-and-configured-names) before upgrading shared deployments.

Verification: [Redis and discovery recovery](../../tests/Wafish.Regression/evidence/recovery/README.md).

## Combined burst and sustained allowances (R38)

Use `DualWindow` to enforce two fixed-window allowances in one atomic Redis operation:

```json
{
  "enabled": true,
  "algorithm": "DualWindow",
  "burstLimit": 10,
  "limit": 100,
  "windowSeconds": 60,
  "failureMode": "FailClosed",
  "keyPrefix": "wafish:rl"
}
```

`burstLimit` applies to a one-second window. `limit` applies to the sustained `windowSeconds` window.
The sustained window must be 2–604,800 seconds. Each allowance must be 1–1,000,000 requests.
Supply `burstLimit` and `windowSeconds` explicitly for this algorithm.
Windows begin on their first accepted request, using Redis server time. They are fixed windows, not sliding windows.
Each request consumes one permit from both windows. Exhausting either allowance rejects the request without modifying either counter.
A rejection returns HTTP 429. `Retry-After` covers every exhausted window, rounded up to whole seconds.
`X-RateLimit-Limit` reports the smaller capacity. `X-RateLimit-Remaining` reports the smaller remaining allowance.

Both counters occupy one Redis hash key, so Redis Cluster never requires a cross-slot operation.
The key includes a `v3` policy hash containing both allowances and window lengths.
Accepted requests set expiry to the later window end. Rejections do not extend expiry.
Changing either allowance starts fresh counters; previous keys retain their bounded expiry.
Existing single-quota algorithms retain their `v2` key format and behavior.
See [Redis script atomicity](https://redis.io/docs/latest/develop/programmability/eval-intro/).

Custom stores can implement the additive `ICombinedDistributedRateLimitStore` capability.
Existing `IDistributedRateLimitStore` implementations remain compatible with single-quota bindings.
A combined binding with an unsupported store follows its configured outage policy. The default remains `FailClosed`.
Redis failures do not fall back to independent acquisitions. Such a fallback could partially consume an allowance.
