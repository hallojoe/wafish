# External login sample

This project runs three roles: a static website, a gateway, and a support API.
The optional Aspire profile also starts Keycloak and Redis.

```bash
EnableExternalLogin=true aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire wait visitor-gateway --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

Use the `Visitor login sample` endpoint from Aspire. The gateway uses a fixed direct endpoint, including isolated runs.
The AppHost supplies the actual callback origin to Keycloak and Wafish.
Trust the ASP.NET Core development certificate through normal platform tools. Certificate validation remains enabled.

Open the public page, then its protected support page. Keycloak offers two development accounts:

| User | Permission |
| --- | --- |
| `writer` | Protected pages and support-ticket creation |
| `reader` | Protected pages only |

Passwords are generated secret parameters. Obtain them through Aspire's local secret configuration.
No working credentials are stored in the realm file.
The support API validates the `support-api` audience and `support:write` scope.
The sample returns a ticket identifier without storing or sending a real support request.

Run the browser checks after readiness:

```bash
WAFISH_VISITOR_ORIGIN=https://localhost:7443 WAFISH_TOPOLOGY_ID=default node --use-system-ca scripts/check-external-login.mjs
```

Use the actual gateway origin. The script reads only the sample credentials from the AppHost's user-secrets file.
Override `WAFISH_APPHOST_SECRETS_FILE` when that file uses a different location.
Install the React UI's npm dependencies and Playwright Chromium before running the script.
Set `WAFISH_VISITOR_VERIFY_REFRESH=true` to test real token refresh. This adds a 65-second expiry-window wait.

`ExternalLogin:GatewayPort` and `ExternalLogin:IdentityPort` default to 7443 and 7444.
Use distinct identity ports for simultaneous topologies. The existing default profile remains container-free.
Keycloak's development realm is disposable. Redis sessions do not require persistence across development restarts.

See the [plugin guide](../../src/Wafish.Plugin.ExternalLogin/README.md) for production configuration and limits.

Set `EnableSecondVisitorGateway=true` to add a second gateway on port 7445.
Both replicas share sessions and use the primary public origin.
Set `WAFISH_VISITOR_REPLICA_ORIGIN=https://localhost:7445` for the browser script to verify the second replica.
The script preserves the canonical Host header when accessing that replica.
Production replicas need an ingress preserving the same public origin.
