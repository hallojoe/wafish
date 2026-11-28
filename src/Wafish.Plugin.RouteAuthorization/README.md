# Route authorization

`route-authorization` adds route-specific access rules. The host still owns authentication and token validation.
Existing named policies must also pass. Explicit anonymous policies cannot be combined with this plugin.

```json
{
  "allowedScopes": ["orders.read", "orders.admin"],
  "claims": [{ "type": "tenant_id", "allowedValues": ["alpha"] }]
}
```

An authenticated caller must have any allowed scope and every claim requirement.
Each claim requirement accepts any listed value. Comparisons are case-sensitive.
Scope values come from repeated or space-separated `scope` and `scp` claims on authenticated identities.
An empty category imposes no condition. At least one category must contain a condition.
Each category accepts at most 32 entries; each claim allows at most 32 values.

Rules run before redirects, cache lookup, aggregation, and forwarding. Denial never calls the backend.
Unauthenticated callers receive the host's authentication challenge. Authenticated callers receive its forbid response.
Without a configured challenge/forbid handler, responses use 401/403. Cookie handlers can redirect according to host settings.
The built-in endpoint pipeline must remain enabled. Configuration validates before persistence and activation.

The editor uses scope lists and claim rows. The `wafish.route_authorization.decisions` counter records allowed/denied outcomes only.

External visitor authorization failures return HTTP 403.
They do not invoke the administration scheme's access-denied handler.
See [external login](../Wafish.Plugin.ExternalLogin/README.md) for visitor session integration.
