# Wafish.Plugin.CircuitBreaker

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This plugin tracks destination failures and opens circuits for selected clusters. Its binding ID is `circuit-breaker`.

```mermaid
stateDiagram-v2
    [*] --> Closed
    Closed --> Open: Failure threshold reached
    Open --> HalfOpen: Recovery timeout elapsed
    HalfOpen --> Closed: Recovery succeeds
    HalfOpen --> Open: Attempt fails
    note right of Open
        Requests are rejected while the circuit is open.
    end note
    note right of HalfOpen
        Only limited recovery attempts are allowed.
    end note
```

## Real-world example

A payment backend starts returning failures. The gateway rejects or avoids unhealthy destinations while allowing recovery probes.

## Real-world value

Failing destinations receive less repeated traffic, which gives them time to recover.

## What it does

- Track closed, open, and half-open circuit states.
- Apply failure thresholds, sampling rules, and recovery limits.
- Expose circuit operations and restore supported runtime state.

## What it does not do

- Repair an upstream service or guarantee successful payments.
- Replace active health checks or retry policy.

## Start here

Source project: [Wafish.Plugin.CircuitBreaker.csproj](Wafish.Plugin.CircuitBreaker.csproj).
Direct project references: [Wafish.Plugin.Abstractions](../Wafish.Plugin.Abstractions/README.md), [Wafish](../Wafish/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](PLAN.md).

## Review fixes 4–7

Candidate filtering does not reserve probes. Load balancing selects a destination before probe acquisition.
An attempt lease releases canceled probes and records each forwarding outcome once.
Generation checks prevent late outcomes from changing a newer circuit state. Retry surrounds this attempt boundary.

## Review items 14–16

Runtime circuit state accepts an injected `TimeProvider`.
Recovery, sampling, and idle retention use monotonic elapsed time. External legacy state timestamps retain UTC comparison fallback.

Verification: [review evidence](../../tests/Wafish.Regression/evidence/review141516/README.md).

## Concurrent recovery verification

Controlled-time tests cover recovery without wall-clock delays.
Contention checks admit exactly four probes from each wave of 128 competing requests.
Cancelled probes release capacity without recording success or failure.
See [lifetime stress evidence](../../tests/Wafish.Regression/evidence/lifetime-stress/README.md).

## Recovery jitter

`recoveryJitterSeconds` adds a random delay from zero to the configured maximum.
The default is zero. The maximum is 300 seconds.
Each opening samples once. Polling and configuration changes cannot extend an existing recovery deadline.
A failed recovery probe starts a new delay. Probe limits still apply.

## Destination maintenance

Authenticated operators can set process-local maintenance windows for individual destinations.
Windows expire automatically and can optionally admit bounded recovery probes.
See [maintenance contracts](../../docs/need-six-features.md#destination-maintenance).
