# Claims transform

`claims-transform` maps authenticated claims into downstream requests. Bind it to a route through the plugin editor.
The host must authenticate requests. This plugin does not validate tokens or change the authenticated principal.

```json
{
  "pathTemplate": "/tenants/{tenant}/orders",
  "mappings": [
    { "claim": "tenant_id", "target": "Path", "name": "tenant" },
    { "claim": "sub", "target": "Header", "name": "X-User-Id" },
    { "claim": "locale", "target": "Query", "name": "language", "required": false }
  ]
}
```

Required defaults to true. Optional mappings remove the target when the source is absent.
Path mappings are always required. Multiple distinct source values reject the request.
Targets replace existing values. Mapping never alters inbound routing or `HttpContext.User`.
Unauthenticated requests challenge; invalid or missing required claims return 403 before forwarding.

Use 1–32 mappings. Values are limited to 4 KiB each and 16 KiB combined.
Targets use letters, digits, underscores, and hyphens. Path values cannot contain separators, percent escapes, or dot segments.
Protected host, credential, framing, connection, and forwarding headers cannot be targets.
Native transforms cannot write the same configured target. Invalid configuration is rejected before persistence.

Claims-to-claims transformation before authorization remains deferred. See the root PLAN.md entry G06-FULL.
