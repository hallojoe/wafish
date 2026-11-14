# Wafish.Plugin.Retry: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Test replay and response boundaries

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [async configuration and retry evidence](../../tests/Wafish.Regression/evidence/async-retry/README.md).

Cover partial responses, streamed bodies, cancellation, and exhausted destinations.

Acceptance: Unsafe requests are not replayed and no response bytes are duplicated.

### IMP-02: Make timing tests repeatable

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [async configuration and retry evidence](../../tests/Wafish.Regression/evidence/async-retry/README.md).

Use controlled backoff and jitter sources in focused tests.

Acceptance: Retry timing and total attempt limits can be asserted without long sleeps.

## New valuable feature

### FEAT-01: Add a cluster-wide retry budget

Status: TODO.

Limit extra attempts during a widespread upstream outage.

Acceptance: Retries stop at the configured shared budget while initial requests retain their normal policy.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Disable retries when request replay is unavailable

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: request correctness defect.

Verification: [review items 1–3 evidence](../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [RequestRetryMiddleware.cs](Middleware/RequestRetryMiddleware.cs), `ReadRequestBodyPooledAsync` and `InvokeAsync`.

Bodies above one MiB return no replay buffer, but execution still enters the retry loop.
Unknown-length bodies also enter without capture.
Later attempts can read an already-consumed body.
Determine replay eligibility before the first attempt.

Acceptance: Oversized and chunked bodies use one attempt unless replay is explicitly supported.
Buffered retries send identical bytes and restore the original request stream.

### IMP-04: Bound response buffering and stop unsafe retries

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: streaming risk.

Verification: [review items 1–3 evidence](../../tests/Wafish.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [RequestRetryMiddleware.cs](Middleware/RequestRetryMiddleware.cs), the response stream and final `X-Retry-Count` assignment.

Every attempt buffers the full response, including successful streaming responses.
There is no response-size cap or `HasStarted` retry guard.
The retry header is assigned after copying response bytes.
Introduce bounded capture, explicit streaming bypass, and header assignment before response start.

Acceptance: Large downloads have bounded memory and prompt first-byte delivery.
Started responses cannot be cleared or retried.
Successful retries do not mutate sent headers.

### FEAT-02: Honor upstream retry timing within a total deadline

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P2. Type: feature.

Verification: [async configuration and retry evidence](../../tests/Wafish.Regression/evidence/async-retry/README.md).

Evidence: [RequestRetryMiddleware.cs](Middleware/RequestRetryMiddleware.cs) uses local exponential delay and per-attempt timeouts.

Respect valid Retry-After values with a configured maximum delay.
Apply one total request deadline across attempts and waits.
Preserve the existing restriction on non-idempotent retries.

Acceptance: A long upstream delay cannot exceed the total budget.
Cancellation interrupts waits, and invalid headers use the documented fallback.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
