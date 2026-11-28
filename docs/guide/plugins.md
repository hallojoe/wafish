# Choose your policies

Wafish includes 15 first-party plugins. A plugin can supply request middleware, background work, or management views.
Bindings attach configuration to a supported route or cluster scope.

## Plugin catalog

| Plugin ID | Purpose | Read more |
|---|---|---|
| `route-authorization` | Require authenticated scopes and claims before route responses. | [Route authorization](../../src/Wafish.Plugin.RouteAuthorization/README.md) |
| `request-aggregation` | Combine independent GET JSON routes with bounded parallel execution. | [Aggregation](../../src/Wafish.Plugin.Aggregation/README.md) |
| `claims-transform` | Map authenticated claims into downstream requests. | [Claims transform](../../src/Wafish.Plugin.ClaimsTransform/README.md) |
| `external-login` | Require visitor sessions and forward configured API tokens. | [External login](../../src/Wafish.Plugin.ExternalLogin/README.md) |
| `redirect` | Return validated redirects, including HTTPS upgrades, without a backend. | [Redirect](../../src/Wafish.Plugin.Redirect/README.md) |
| `waf` | Inspect configured request inputs and enforce allow/deny rules. | [WAF](../../src/Wafish.Plugin.Waf/README.md) |
| `rate-limit` | Apply local rate-limit policies. | [Local rate limits](../../src/Wafish.Plugin.RateLimit/README.md) |
| `rate-limit-redis` | Coordinate rate-limit counters through Redis. | [Redis rate limits](../../src/Wafish.Plugin.RateLimit.Redis/README.md) |
| `response-cache` | Cache eligible public responses within the process. | [Cache](../../src/Wafish.Plugin.Cache/README.md) |
| `request-retry` | Retry eligible failures within bounded attempts and time. | [Retry](../../src/Wafish.Plugin.Retry/README.md) |
| `circuit-breaker` | Control unhealthy destination eligibility and recovery probes. | [Circuit breaker](../../src/Wafish.Plugin.CircuitBreaker/README.md) |
| `compression` | Negotiate supported response encodings. | [Compression](../../src/Wafish.Plugin.Compression/README.md) |
| `traffic-metrics` | Observe route traffic and latency. | [Metrics](../../src/Wafish.Plugin.Metrics/README.md) |
| `cluster-metrics` | Observe cluster traffic and latency. | [Metrics](../../src/Wafish.Plugin.Metrics/README.md) |
| `proxy-log` | Capture bounded request metadata with configured redaction. | [Request logs](../../src/Wafish.Plugin.ProxyLog/README.md) |
| `service-discovery` | Refresh cluster destinations from configured providers. | [Discovery](../../src/Wafish.Plugin.ServiceDiscovery/README.md) |

## Apply a binding

1. Enable the plugin through its management workflow.
2. Select a route or cluster supported by its manifest.
3. Configure a binding using the plugin's current schema.
4. Preview the proposed configuration.
5. Apply the change and inspect its active revision and health.

For example, an eligible route cache can use:

```json
{
  "enabled": true,
  "ttlSeconds": 60,
  "tags": ["catalog"],
  "fillWaitMilliseconds": 1000
}
```

This is the binding's configuration object, not a complete administration API request.
Credentials, private responses, and HTTP cache directives still affect cache eligibility.
See [change-set examples](../configuration-policy-contracts.md) for complete route and binding requests.

## Order and lifecycle

Ordering follows plugin manifests, declared dependencies, and scope bindings.
Do not infer actual order from the website's simplified demonstration model.
The [plugin contract](../plugin-lifecycle-contract.md) explains startup, cancellation, shutdown, and compatibility.

Retired plugin generations wait for active request leases before disposal.
External plugin code must cooperate with cancellation and resource ownership.

## Keep the boundaries visible

- WAF rules reduce specific risks. They do not guarantee application security.
- Retries cannot make non-idempotent operations safe by themselves.
- Local cache sharing and retry budgets apply per process.
- Redis-backed rate limits require a configured Redis service and a deliberate outage policy.
- Administration permissions do not establish tenant ownership isolation.

Read the [security boundaries](../security.md) before enabling production policies.
