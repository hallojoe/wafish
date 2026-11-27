# Wafish.Plugin.Aggregation

`request-aggregation` combines independent GET JSON routes into one JSON object.
Bind it to a GET-only route without a cluster. Child references use stable route UIDs.
Aggregate requests cannot contain a body.
Select child routes by name in the dashboard. Their UIDs remain stable when names change.

```json
{
  "children": [
    { "name": "profile", "routeUid": "<profile-route-uid>", "path": "/profiles/{route.id}" },
    {
      "name": "orders",
      "routeUid": "<orders-route-uid>",
      "path": "/orders",
      "query": [{ "name": "customer", "value": "{route.id}" }],
      "forwardHeaders": ["X-Correlation-ID"]
    }
  ],
  "maxChildren": 4,
  "parallelism": 4,
  "timeoutSeconds": 10,
  "maxChildBytes": 1048576,
  "maxCombinedBytes": 4194304
}
```

Path and query templates accept `{route.name}` and `{query.name}`.
Missing, ambiguous, oversized, or unsafe inputs return 400. Path values are encoded as individual segments.
The matched child route must equal the configured UID. Local paths cannot select administration endpoints or another route.

All child authorization checks finish before forwarding begins.
Children preserve authentication credentials, route authorization, WAF, quotas, claims mappings, retry, and destination policies.
Parent quotas apply once. Child quotas apply to each child execution.
See [internal execution](../Wafish.Runtime/README.md#internal-route-execution) for host integration limits.

Every child must return successful UTF-8 JSON. A 204 response becomes `null`.
The response uses child names as properties and `Cache-Control: no-store`.
Child headers and cookies are not copied. Invalid JSON, unsupported encodings, transport errors, and size failures return 502.
Policy failures retain 401, 403, or 429. Deadlines return 504; admission failures return 503.
Failures cancel and join all siblings. No partial result or backend error body is returned.

Limits default to four children, four parallel calls, ten seconds, 1 MiB per child, and 4 MiB combined.
Maximum values are eight children, eight parallel calls, sixty seconds, 4 MiB per child, and 16 MiB combined.
Each host allows sixteen active aggregates and reserves at most 64 MiB for raw response buffers.
Reservations include internal buffers and returned byte arrays. JSON parsing and framework overhead are additional bounded costs.

Configuration validation rejects missing targets, self-reference, nested aggregates, and redirect targets.
Referenced routes cannot be deleted or changed incompatibly unless the same change set repairs their references.
Changes to referenced route matching require an updated child request in that change set.
Aggregate parents cannot use redirects, retries, response caching, or claims transforms.
Disabling a binding leaves its clusterless route unavailable.

Metrics use `wafish.aggregation.requests` with bounded status tags. Claim values and response bodies are excluded.
Dependent calls, writes, partial results, streaming, custom composition, and aggregate caching remain deferred.

Entry points: [compiler](AggregationCompiler.cs), [handler](AggregationHandler.cs), and [admission](AggregationAdmission.cs).
Verify with `dotnet run --project tests/Wafish.Regression -c Release -- --group aggregation`.
