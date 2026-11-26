# R54: Idempotent synthetic orders

The sample accepts one bounded idempotency key and a typed synthetic order payload.
New keys return HTTP 201. Identical replays return HTTP 200 with the original order.
A conflicting payload returns HTTP 409 without changing the committed order.
Each response identifies the current backend and trace while preserving the stored order identity.

Both Aspire backends share one topology-specific local ledger.
Exclusive file leases serialize writes across processes. Atomic replacement preserves committed state on write failure.
The ledger retains at most 256 orders for ten minutes. Expired keys can create new orders.
Capacity and storage failures return HTTP 503 with Retry-After. Valid replays still work at capacity.
The sample is unauthenticated and stores only synthetic order data. It is not a distributed production order service.

Focused checks cover concurrent writers, cross-instance replay, restart reads, conflict handling, cancellation, and expiry.
They also cover input bounds, capacity, private file permissions, corrupt state, failed writes, and sanitized HTTP errors.
The real Aspire scenario verifies simultaneous writes across separate backend processes and replay after backend restart.
It also verifies replay through the gateway, backend identity, and W3C trace propagation.
The existing topology scenario verifies gateway, backend, and AppHost restart contracts.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-orders
python3 scripts/check-idempotent-orders.py
python3 scripts/check-topology.py
dotnet run --project tests/Wafish.Regression -c Release
```

See `focused.json`, `orders-topology.log`, `topology.log`, `regression.json`, and `changed-coverage.json`.
Coverage combines Release regression checks and Debug Aspire hosts, including both entry points.

Changed executable C# coverage is 91.51% (97/106 lines). Partial source ranges count as uncovered.

The Release solution build and all 307 core regression cases passed. Both real Aspire scenarios passed.
