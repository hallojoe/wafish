# SampleLocalService: proposed work

Reviewed against source on 2026-11-14.
All items below are TODO proposals, not implemented capabilities or scheduled commitments.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements not yet implemented

### IMP-01: Add sample endpoint contract checks

Status: TODO.

Verify ping, echo, instance headers, and trace identity through a gateway.

Acceptance: Both backend instances satisfy the same endpoint contracts.

### IMP-02: Document standalone registration settings

Status: TODO.

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

Status: TODO. Priority: P2. Type: sample mismatch.

Evidence: [Program.cs](Program.cs) and [LocalServiceController.cs](Controllers/LocalServiceController.cs).

The instance endpoint uses the configured registration identity.
Ping and echo responses use the machine name.
Two local instances therefore return the same identity from those controller actions.
Resolve one instance identity and apply it consistently to responses and trace tags.

Acceptance: Both sample processes remain distinguishable through every endpoint.
Response identity matches the registered instance.

### FEAT-02: Demonstrate cancellable streaming

Status: TODO. Priority: P3. Type: sample feature.

Evidence: [LocalServiceController.cs](Controllers/LocalServiceController.cs) returns complete responses.

Add a bounded streaming endpoint with configurable chunk count and interval.
Observe request cancellation and report only synthetic sample data.

Acceptance: The first chunk arrives before completion.
Disconnecting the client stops backend work.
The sample demonstrates cache, compression, and retry streaming behavior.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
