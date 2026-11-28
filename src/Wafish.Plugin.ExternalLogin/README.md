# External login

`external-login` protects routes with OIDC visitor sessions. It supports static pages and same-origin API calls.
Provider settings load at startup. Route bindings reload through normal Wafish configuration activation.
Changing a site profile invalidates its existing sessions and pending login callbacks.
Visitor authentication is separate from administration authentication.

## Host registration

```csharp
builder.Services.AddWafishStorage().AddWafishRuntime();
builder.Services.AddWafishExternalLogin(builder.Configuration, builder.Environment);
var app = builder.Build();
app.UseWafishGateway();
```

The package namespace is `Wafish.Plugin.ExternalLogin`.
Configure trusted forwarded headers before the gateway when an ingress changes the public origin.
The plugin never trusts arbitrary forwarded headers automatically. HTTPS and certificate validation remain required.
Path-base hosting is not supported. Public origins must use the root path.

```json
{
  "Gateway": {
    "ExternalLogin": {
      "RedisConnectionReference": "Secrets:VisitorRedis",
      "Namespace": "my-visitor-deployment",
      "KeyRingPath": "/run/wafish/visitor-keys",
      "KeyCertificatePath": "/run/secrets/visitor-key-encryption.pfx",
      "KeyCertificatePasswordReference": "Secrets:KeyPassword",
      "Sites": [{
        "Id": "website",
        "Origin": "https://example.com",
        "IdleMinutes": 30,
        "AbsoluteMinutes": 480,
        "Providers": [{
          "Id": "identity",
          "DisplayName": "Sign in",
          "Authority": "https://identity.example.com/realms/website",
          "ClientId": "wafish",
          "ClientSecretReference": "Secrets:VisitorClient",
          "Scopes": ["openid", "profile", "support:write"]
        }],
        "ApiTargets": [{
          "Id": "support",
          "Origins": ["https://support.internal"],
          "Audience": "support-api",
          "RequiredScopes": ["support:write"]
        }]
      }]
    }
  }
}
```

Supply referenced secrets through your host's secret configuration provider.
All replicas require the same namespace, shared key directory, and key encryption certificate.
Production requires key encryption. Development may omit the encryption certificate.
Keep the key directory separate from Redis. Restrict both to the gateway's identity.

Register this exact callback with the provider:

```text
https://example.com/_wafish/auth/callback/website/identity
```

Authorities must support OIDC authorization code flow, PKCE, and client-secret authentication.
Refresh requests use `client_secret_post`. Providers requiring other token authentication methods need an adapter.
Request the provider's refresh-token scope or grant when required.

## Route bindings

Use exact route hosts matching the configured origin's authority, including nondefault ports.
Give protected routes priority over public catch-all routes.

```json
{ "site": "website", "mode": "page" }
```

```json
{ "site": "website", "mode": "api", "apiTarget": "support" }
```

`page` redirects unauthenticated GET/HEAD requests to login. Other requests receive 401.
`api` always returns 401 for missing sessions. It never redirects to login.
Without `apiTarget`, the route enforces a session but forwards no credentials.

Native authorization and `route-authorization` requirements still apply.
Native policies must not select unrelated authentication schemes.
Explicit anonymous policies conflict with this plugin.
Internal aggregation calls enforce the child's site and session requirements without interactive login.
Protected requests bypass the response cache through their original session cookie. Responses use `Cache-Control: no-store`.

The dashboard schema lists configured site and API identifiers. It cannot change providers or expose secrets.

## Browser endpoints

| Endpoint | Behavior |
| --- | --- |
| `GET /_wafish/auth/login` | Provider selector; one provider redirects immediately |
| `GET /_wafish/auth/login/{provider}` | Starts login with a local `returnUrl` |
| `GET /_wafish/auth/session` | Authentication state, display name, and CSRF token |
| `POST /_wafish/auth/logout` | Deletes the local session |

The prefix is reserved. Authentication endpoint requests have a bounded per-instance rate limit.
Sessions use Secure, HttpOnly, host-only cookies. Access and refresh tokens remain encrypted in Redis.
Different sites cannot share hostnames, including different ports on the same hostname.

Read the CSRF token from the session endpoint. Send it in `X-Wafish-CSRF` for unsafe requests.
The request must also carry an `Origin` matching the configured site.
Use same-origin browser calls. Cross-origin browser API access is not supported by this feature.

```javascript
const session = await fetch('/_wafish/auth/session').then(response => response.json());
await fetch('/api/support/tickets', {
  method: 'POST',
  headers: { 'X-Wafish-CSRF': session.csrfToken }
});
```

## Downstream tokens

Configure the identity provider to issue an access token for the API during login.
`Audience` records the required API contract; it does not change the provider's token audience.
Wafish treats access tokens as opaque. The receiving API must validate issuer, audience, expiry, and permissions.
Required scopes are checked against the provider's granted scopes before forwarding.

Only configured HTTPS destination origins receive bearer tokens. Incoming authorization and cookie headers are stripped.
Gateway session cookies are also removed on public routes. Public backends cannot set gateway session cookies.
Internal child requests never forward the parent browser's credentials to unprotected destinations.
Upstream cookies are also removed. Do not use protected routes for applications that require their own cookie sessions.
Do not add host-side custom transforms that replace the final destination after credential enforcement.

Refresh starts within 60 seconds of expiry. Redis serializes refresh across replicas.
An uncertain refresh remains fenced. A request after 30 seconds invalidates an abandoned refresh session.
This prevents reuse of rotated refresh tokens after process failure.
Logout and expired Redis records cannot be restored by late refresh results.

Missing grants return 403. Expired credentials return 401. Infrastructure failures return 503.
Local logout does not revoke an already issued access token or end the identity provider's SSO session.

## Verification and limits

Run the `external-login` regression group. Set `WAFISH_EXTERNALLOGIN_REDIS` to an isolated Redis instance for live checks.
Metrics use the `Wafish.ExternalLogin` meter and `wafish.external_login.outcomes` counter. Labels contain outcomes only.

Direct OAuth adapters, token exchange, account linking, backchannel logout, and dynamic provider management are deferred.
Certificate issuance and renewal remain infrastructure responsibilities.
