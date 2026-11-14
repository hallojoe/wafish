# WafIsh.Grpc: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add wire compatibility fixtures

Status: TODO.

Store representative encoded messages and verify decoding across protocol revisions.

Acceptance: Additive fields preserve old message behavior.

### IMP-02: Define protocol error mappings

Status: TODO.

Document how configuration outcomes map to response fields and gRPC status codes.

Acceptance: Client and server tests agree on each failure outcome.

## New valuable feature

### FEAT-01: Add a capability handshake

Status: TODO.

Let clients query protocol version and supported registration capabilities.

Acceptance: Older clients continue to register when the new RPC is unavailable.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Define ownership for destination updates

Status: DONE. Priority: P1. Type: protocol gap.

Evidence: [GatewayRegistry.proto](Protos/GatewayRegistry.proto), `UpdateDestinationsRequest`.
See [server implementation](../WafIsh/Services/GatewayRegistryGrpcService.cs), `UpdateDestinations`.

Registration carries `instance_id`; destination replacement does not.
The server replaces cluster destinations, including destinations owned by other registered instances.
Add an instance-scoped update contract and an expected revision.
Keep full-cluster replacement explicit for authorized operators.

Acceptance: Updating instance A preserves instance B.
Stale revisions fail without changing either instance.
Preserve existing field numbers and define legacy request behavior.

### FEAT-02: Negotiate registration leases

Status: TODO. Priority: P2. Type: feature.

Evidence: [GatewayRegistry.proto](Protos/GatewayRegistry.proto), registration and heartbeat messages.

Heartbeats return a next-heartbeat timestamp but no lease identity or expiry contract.
Add optional lease identifiers, renewal intervals, expiry, and drain status.
Coordinate implementation with Client and core services under solution item NET-FEAT-02.

Acceptance: Old clients retain documented behavior.
A stale renewal cannot revive a newer instance lease.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03 on 2026-11-14. [Verification](../../tests/WafIsh.Regression/evidence/review4567/README.md).
