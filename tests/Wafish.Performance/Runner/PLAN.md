# Runner: proposed work

Reviewed against source on 2026-11-14.
Completed items include verification evidence. Remaining items are proposals, not scheduled commitments.
The [root execution plan](../../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Bound latency sample memory

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../Wafish.Regression/evidence/benchmark-reliability/README.md).

Replace unbounded sample retention with a documented bounded measurement strategy.

Acceptance: Long soak runs retain accurate percentiles within a stated error bound.

### IMP-02: Record complete run metadata

Status: DONE. Owner: Codex. Completed: 2026-11-14.

Verification: [benchmark reliability](../../Wafish.Regression/evidence/benchmark-reliability/README.md).

Capture runtime, operating system, CPU, effective mode, and source revision when available.

Acceptance: Reports can be compared without guessing their execution conditions.

## New valuable feature

### FEAT-01: Add CI performance regression budgets

Status: TODO.

Compare a run against a selected baseline with explicit tolerances.

Acceptance: The runner reports regressions separately from request failures and measurement noise.

## Source review follow-up: 2026-11-14

Static review findings follow. Implementation and acceptance checks remain pending.
See [solution priorities and dependencies](../../../DOTNET_REVIEW.md).

### IMP-03: Fail managed runs when assertions fail

Status: DONE. Priority: P1. Type: verification defect.

Evidence: [Program.cs](Program.cs), managed reporting and `RunExtendedCaseAsync`.

External mode sets a failure exit code, but managed results do not.
The `allowNonSuccess` flag also accepts transport failures as expected policy rejections.
Classify expected HTTP outcomes separately from connection and body-read failures.

Acceptance: Failed functional assertions produce a nonzero exit code.
Expected HTTP 429 responses can pass, while connection failures cannot.

### IMP-04: Bound response body reads

Status: DONE. Priority: P1. Type: harness hang risk.

Evidence: [Program.cs](Program.cs), `RunLoadAsync`.

Requests use `ResponseHeadersRead`, then copy bodies without a cancellation token.
The client timeout does not bound that separate body-read operation.
See the [HttpCompletionOption timeout contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0).
Apply a per-request deadline and a run shutdown deadline to both stages.

Acceptance: An upstream that sends headers and then stalls cannot hang the run.
The timeout appears as a transport failure in the report.

### FEAT-02: Add scheduled request-rate mode

Status: TODO. Priority: P2. Type: benchmark feature.

Evidence: [Program.cs](Program.cs) uses workers that wait for each response before sending another request.

Add an optional fixed arrival-rate mode with a bounded queue.
Report queue delay, dropped arrivals, completed responses, and service time separately.

Acceptance: A slow gateway does not silently reduce the offered request rate.
Overload remains visible without unbounded client memory.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.

## Implementation evidence

Completed IMP-03, IMP-04 on 2026-11-14. [Verification](../../Wafish.Regression/evidence/review4567/README.md).
