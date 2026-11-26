# R39: weighted local request costs

Trusted route configuration sets a positive `requestCost`, bounded by the selected algorithm's capacity.
Fixed windows, sliding windows, and token buckets acquire the entire cost atomically.
Concurrency retains a cost of one. Headers cannot change the cost.

Different costs can share one named allowance. Failed acquisitions consume no permits.
Costs are excluded from the limiter key so cost-only changes retain the current allowance.
The optional weighted store interface preserves existing custom stores for cost one.
Unsupported weighted stores return HTTP 503 with retry guidance.

Focused checks cover all three weighted algorithms, positive Retry-After, blocked forwarding,
schema validation, precommit rejection, legacy stores, and queued cancellation during shutdown.
The full core suite also includes limiter lifetime and partition churn checks.

See `regression.json`, `api.log`, and `changed-coverage.json` for verification results.

The Release solution build passed. All 301 core cases and 19 public API baselines passed.
