# Wafish.Plugin.RateLimit.Redis: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Test Redis failover and reconnection

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Extend live tests to cover connection replacement and interrupted Lua operations.

Acceptance: Recovery preserves outage policy and shared quota behavior.

### IMP-02: Validate key isolation across deployments

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Test configured prefixes with identical routes and client addresses.

Acceptance: One deployment cannot consume another deployment's quota accidentally.

## New valuable feature

### FEAT-01: Add trusted tenant quota partitions

Status: TODO.

Allow an explicitly configured, authenticated tenant claim to select the shared quota key.

Acceptance: Missing or untrusted claims follow a documented policy and cannot create unlimited key cardinality.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items have verification evidence below.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Separate keys for incompatible algorithms

Status: DONE. Priority: P1. Type: configuration-transition defect.

Evidence: [RedisRateLimitMiddleware.cs](../../../../../src/Wafish.Plugin.RateLimit.Redis/Middleware/RedisRateLimitMiddleware.cs) and [RedisLuaRateLimitStore.cs](../../../../../src/Wafish.Plugin.RateLimit.Redis/RedisLuaRateLimitStore.cs).

Keys contain prefix, route, and client address.
The algorithms use different Redis value types under those same keys.
Changing algorithms can produce WRONGTYPE failures until the old key expires.
Include a compatible policy generation or algorithm identity in keys.

Acceptance: Change fixed-window, sliding-window, and token-bucket policies under traffic.
No incompatible-key errors occur, and old keys expire.
Keep fail-closed outage behavior.

### IMP-04: Recover a failed connection creation task

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: recovery risk.

Evidence: [RedisLuaRateLimitStore.cs](../../../../../src/Wafish.Plugin.RateLimit.Redis/RedisLuaRateLimitStore.cs), the `_connections` dictionary.

A faulted lazy connection task remains cached for the connection string.
Ordinary disconnected multiplexers can reconnect; a faulted construction task cannot.
Evict failed construction entries with bounded retry backoff and safe disposal.

Acceptance: An injected first-connection failure recovers on a later attempt.
Concurrent callers create one replacement without exposing connection secrets.

### FEAT-02: Combine burst and sustained quotas

Status: TODO. Priority: P3. Type: feature.

Evidence: [RedisLuaRateLimitStore.cs](../../../../../src/Wafish.Plugin.RateLimit.Redis/RedisLuaRateLimitStore.cs) evaluates one quota per acquisition.

Support a short burst allowance and a longer sustained allowance in one atomic decision.
Define Redis Cluster key placement and expiry rules.

Acceptance: Exceeding either allowance rejects the request.
Rejected requests do not partially consume another allowance.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../src/Wafish.Plugin.RateLimit.Redis/README.md) and [AGENTS.md](../../../../../src/Wafish.Plugin.RateLimit.Redis/AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../../../../tests/Wafish.Regression/evidence/review4567/README.md).

Completed IMP-01, IMP-02, IMP-04 on 2026-11-14. [Verification](../../../../../tests/Wafish.Regression/evidence/recovery/README.md).
