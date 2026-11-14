# WafIsh.Plugin.Waf: proposed work

Reviewed against source on 2026-11-14.
Statuses below distinguish completed review work from remaining proposals.
The [root execution plan](../../PLAN.md) remains the historical and cross-project record.

## Improvements

### IMP-01: Add fuzz tests for decoding and normalization

Status: TODO.

Generate encoded paths, Unicode bodies, malformed escapes, and split reads.

Acceptance: Checks terminate within bounds and preserve the documented rejection behavior.

### IMP-02: Measure false positives with a benign corpus

Status: TODO.

Maintain representative valid API payloads alongside attack fixtures.

Acceptance: Rule changes report both missed attack fixtures and rejected benign fixtures.

## New valuable feature

### FEAT-01: Add an explicit detection-only mode

Status: TODO.

Allow selected rules to report matches without blocking during policy evaluation.

Acceptance: Blocking remains the default, and reports clearly identify non-blocking decisions.

## Source review follow-up: 2026-11-14

Static review findings follow. Completed items include verification evidence.
See [solution priorities and dependencies](../../DOTNET_REVIEW.md).

### IMP-03: Reject invalid CIDR prefixes during validation

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: validation defect.

Verification: [review items 1–3 evidence](../../tests/WafIsh.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [IpMatcher.cs](Helpers/IpMatcher.cs), `IsInCidrRange`, and [WafPlugin.cs](WafPlugin.cs).

CIDR parsing does not constrain prefix lengths.
Invalid IPv4 lengths produce unintended masks.
IPv6 lengths above 128 can access beyond the address buffer.
Validate address families and prefix ranges before activation.

Acceptance: IPv4 prefixes outside 0–32 and IPv6 prefixes outside 0–128 are rejected.
Valid boundary prefixes work without request-time exceptions.

### IMP-04: Sanitize WAF rejection logs

Status: DONE. Owner: Codex. Completed: 2026-11-14. Priority: P1. Type: sensitive-data exposure risk.

Verification: [review items 1–3 evidence](../../tests/WafIsh.Regression/evidence/review123/README.md).
Base commit `bc1b617`; implementation remains uncommitted. Release build and both regression runners pass.
Compatibility changes are recorded in the linked evidence.

Evidence: [WafMiddleware.cs](Middleware/WafMiddleware.cs), `LogWafBlock`.
See [SqlInjectionRuleChecker.cs](Middleware/SqlInjectionRuleChecker.cs), `MatchText`.

Rejection logs include the full query and a prefix of matched text.
Truncation does not remove tokens or sensitive form values.
Log rule identifiers and sanitized field metadata instead of raw matched content.

Acceptance: Seeded secrets never appear in captured application logs.
Operators retain route, rule, trace, and rejection context.

### FEAT-02: Add narrow rule exclusions

Status: TODO. Priority: P2. Type: feature.

Evidence: [SqlInjectionRuleChecker.cs](Middleware/SqlInjectionRuleChecker.cs) scans complete query and body text.

Add stable rule identifiers and exclusions for specific routes and parsed fields.
Require explicit configuration and include exclusions in change previews.
Keep size and decoding limits mandatory.

Acceptance: A legitimate excluded field passes while the same pattern elsewhere remains blocked.
Every exclusion appears in the configuration audit.

## Completion rule

Complete an item only after implementation and its acceptance checks pass.
Record the date, verification commands, evidence, and any compatibility impact.
Update [README.md](README.md) and [AGENTS.md](AGENTS.md) when the implemented behavior changes.
