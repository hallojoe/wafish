# Claims, access rules, and aggregation

Approved: 2026-11-27. Baseline: `29fab3a`.

## Delivery

Use stacked branches and PRs. Each PR targets its predecessor. Merge in order; never auto-merge.

1. `feat/gateway-feature-plan`: findings, specification, and backlog entries.
2. `feat/claims-transform`: G06 downstream mappings, editor, documentation, and tests.
3. `feat/route-authorization`: G13 access rules and early enforcement.
4. `feat/internal-route-execution`: reusable child execution and configuration-generation ownership.
5. `feat/request-aggregation`: G01 composition, editor, documentation, and integration tests.

Preserve unrelated changes. Publication is a separate task.

## G06: Downstream claims mappings

Package: `Wafish.Plugin.ClaimsTransform`. Plugin ID: `claims-transform`. Scope: route.

Compile a versioned binding into immutable mappings. Each mapping names a source claim, target, and required flag.
Support headers, query parameters, and placeholders in one downstream path template.
Use authenticated identities only. Require authentication for every bound request.
Missing required claims fail; optional header/query mappings remove their targets. Path claims are always required.
Reject multiple distinct claim values. Replace target values, including client input. Preserve unrelated fields.
Reject duplicate targets and conflicting native transforms before persistence or activation.

Validate before forwarding. Apply outbound values through YARP request transforms; preserve inbound routing and the principal.
Encode query/path values. Reject controls, traversal, and path separators in claim path segments.
Disallow host, connection, framing, forwarding, and credential header targets.
Limit bindings to 32 mappings, each value to 4 KiB, and combined values to 16 KiB.
Challenge unauthenticated requests; return 403 for missing or invalid required claim values. Never log values.

Acceptance: spoofing, optional removal, encoding, ambiguity, conflicts, hot reload, and cancellation have regression coverage.
Current entry point: [snapshot compiler](../src/Wafish/Services/GatewaySnapshotCompiler.cs).

## G13: Route claims and scopes

Package: `Wafish.Plugin.RouteAuthorization`. Plugin ID: `route-authorization`. Scope: route.

Require authentication, any configured scope, and every configured claim requirement.
Each claim requirement accepts any configured allowed value. Comparisons are exact.
Read repeated and space-separated scopes from `scope` and `scp`.
An empty category imposes no condition; at least one category must contain a condition.
Combine requirements with existing named policies; both must pass. Reject explicit anonymous-policy conflicts.
The host owns authentication, token validation, and trusted issuers.

Enforce through a supported host hook before redirects, cache lookup, aggregation, and forwarding.
Do not permit binding order to move authorization after response production.
Reuse framework challenge/forbid behavior. Do not replace the host policy provider.
Require a host capability and reject activation when the pipeline cannot enforce the rules.

Acceptance: denied requests never reach backends, redirects, or cached responses. Native policies remain enforced.
Current entry point: [gateway pipeline](../src/Wafish.Runtime/GatewayPipelineExtensions.cs).

## G01: Parallel JSON reads

Package: `Wafish.Plugin.Aggregation`. Plugin ID: `request-aggregation`. Scope: route.

Aggregate routes accept GET only and have no backend cluster.
Children name a response property, target route UID, local path template, explicit query mappings, and optional header allowlist.
Use parent route/query values in templates. Missing required inputs return 400.
Resolve through gateway routing and verify the matched UID. Never dispatch another matched route.
Preserve parent host, scheme, identity, and credentials. Disallow arbitrary URLs and cross-host overrides.

Use an internal executor, never loopback HTTP. Isolate each child's context, scope, response storage, and cancellation.
Preserve native authorization, G13, WAF, quotas, G06, retry, discovery, destination selection, and circuit behavior.
Validate all child authorization before backend calls. Count parent quota once and child quotas per child execution.
Lease one compatible configuration/runtime generation until all children stop.
Custom host middleware outside the Wafish route contract is not automatically replayed.

All children must succeed. Return a JSON object keyed by child names; map 204 to null.
Reject invalid JSON and unsupported encodings. Do not merge child cookies or headers.
Return `Cache-Control: no-store`. Aggregate response caching is deferred.

| Limit | Default | Maximum |
| --- | ---: | ---: |
| Children | 4 | 8 |
| Parallel calls | 4 | 8 |
| Deadline | 10 seconds | 60 seconds |
| Decoded child response | 1 MiB | 4 MiB |
| Combined decoded responses | 4 MiB | 16 MiB |

Host limits: 16 active aggregates and a 64 MiB response-buffer budget. Reject excess admission with 503; no unbounded queue.
Cancel and await siblings on failure. Preserve policy failures as 401/403/429; deadlines return 504; other failures return 502.
Return no partial data or backend error bodies. Parent cancellation stops all children.

Reject nested aggregates, self-reference, redirect targets, duplicate names, and unavailable targets.
Reject parent redirects, retries, response caching, and G06 mappings; bind G06 to children instead.
Block target deletion or incompatible changes unless the same change set repairs all references.
Disabling aggregation must leave aggregate routes unavailable, never converted to forwarding routes.

Acceptance: parallel execution, policy parity, dependency validation, generation consistency, resource bounds, and shutdown pass tests.
Current entry point: [runtime domains](../src/Wafish.Runtime/Infrastructure/Plugin/PluginRuntimeDomain.cs).

## G06-FULL: Deferred principal transformation

Full G06 remains deferred after downstream mappings ship. Rethink:

- An execution stage after authentication and before authorization.
- Host schemes and `IClaimsTransformation` integration.
- Add/replace/remove semantics, issuer ownership, and multiple identities.
- Principal cloning; never mutate shared authentication state.
- Effects on roles, scopes, quotas, and downstream mappings.
- Idempotence across repeated authentication.
- Snapshot ownership across authorization, redirects, and aggregate children.
- Compatible host capabilities without required additions to existing plugin interfaces.

G01 extensions also remain deferred: dependent calls, partial results, custom aggregators, writes, streaming, and aggregate caching.

## Shared completion requirements

Use existing binding storage, previews, audit records, revision checks, and atomic activation. Validate disabled drafts.
Add dashboard editors and dependent-route feedback. Register schemas, compilers, manifests, packages, and consumer checks.
Preserve public constructors and plugin contracts. Update README catalogs, component docs, examples, and competitor findings.
Add bounded metrics without claim values or response bodies.

Run the Release solution build and both executable regression suites, including scoped configuration and registration checks.
Run frontend build, lint, coverage, and browser checks. Maintain at least 80% coverage of changed executable code.
Verify new packages with isolated consumers. Check activation rejection, rollback, replacement, concurrency, and shutdown.
Mark backlog work complete only after acceptance checks pass; record actual limits and unrun checks.

## Internal execution implementation

The executor uses framework route matching against captured endpoints and verifies the selected route UID.
It rejects admission during configuration transitions. Prepared batches retain destination addresses across subsequent updates.
Authorization preflight, WAF, native quotas, G06, bounded responses, cancellation, and shutdown have executable checks.
Internal destination counters are shared per captured snapshot and remain separate from direct proxy counters.
Custom host middleware and custom passive-health extensions require host integration.
Entry points: [executor](../src/Wafish.Runtime/InternalExecution/InternalRouteExecutor.cs),
[contracts](../src/Wafish/Services/InternalRouteContracts.cs), and
[checks](../tests/Wafish.Regression/InternalRouteExecutionChecks.cs).
