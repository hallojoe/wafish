# SampleLocalService: proposed work

> Historical record, archived 2026-11-15. This document is not an active plan.
> See the [current backlog](../../../../../PLAN.md). Old statuses describe their original review date.

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add sample endpoint contract checks

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../../../../tests/Wafish.Regression/evidence/sample-contracts/README.md).

Verify ping, echo, instance headers, and trace identity through a gateway.

Acceptance: Both backend instances satisfy the same endpoint contracts.

### IMP-02: Document standalone registration settings

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../../../../tests/Wafish.Regression/evidence/sample-contracts/README.md).

Show explicit gateway URL, gRPC URL, credentials, and destination configuration.

Acceptance: A reader can register one standalone instance without editing gateway internals.

## New valuable feature

### FEAT-01: Add an idempotent order example

Status: TODO.

Provide a sample write operation that demonstrates safe repeated requests with an idempotency key.

Acceptance: Repeated keys do not create duplicate sample orders, and conflicting payloads are rejected.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Use one backend instance identity

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [sample contracts](../../../../../tests/Wafish.Regression/evidence/sample-contracts/README.md). Priority: P2. Type: sample mismatch.

Evidence: [Program.cs](../../../../../samples/SampleLocalService/Program.cs) and [LocalServiceController.cs](../../../../../samples/SampleLocalService/Controllers/LocalServiceController.cs).

The instance endpoint uses the configured registration identity.
Ping and echo responses use the machine name.
Two local instances therefore return the same identity from those controller actions.
Resolve one instance identity and apply it consistently to responses and trace tags.

Acceptance: Both sample processes remain distinguishable through every endpoint.
Response identity matches the registered instance.

### FEAT-02: Demonstrate cancellable streaming

Status: TODO. Priority: P3. Type: sample feature.

Evidence: [LocalServiceController.cs](../../../../../samples/SampleLocalService/Controllers/LocalServiceController.cs) returns complete responses.

Add a bounded streaming endpoint with configurable chunk count and interval.
Observe request cancellation and report only synthetic sample data.

Acceptance: The first chunk arrives before completion.
Disconnecting the client stops backend work.
The sample demonstrates cache, compression, and retry streaming behavior.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](../../../../../samples/SampleLocalService/README.md) and [AGENTS.md](../../../../../samples/SampleLocalService/AGENTS.md) when the implemented behavior changes.
