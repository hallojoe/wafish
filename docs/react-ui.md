# React administration UI

The new workspace uses React, TypeScript, Tailwind CSS, and shadcn/ui with Radix primitives.
The sample serves it at `/console`. It is the only bundled administration interface.
Administration APIs retain the `/aneiang` prefix. The former Razor pages and assets are removed.

## Host registration

Reference `Wafish.ReactUi` and your selected storage package.
The React package depends on administration, without referencing the Razor dashboard package.

```csharp
using Wafish.Administration;
using Wafish.ReactUi;
using Wafish.Runtime;

builder.Services.AddWafishStorage();
builder.Services.AddWafishRuntime();
builder.Services.AddWafishReactUi();

var app = builder.Build();
app.UseWafishRuntime();
app.MapWafishAdministration();
app.MapWafishReactUi();
app.MapWafishProxy();
app.Run();
```

Configure `Gateway:ReactUi:RoutePrefix` to change `console`.
Keep it different from `Gateway:Dashboard:RoutePrefix`, which prefixes administration APIs.
Both prefixes follow endpoint-role restrictions in split deployments.
Register AI and MCP explicitly when required. See [architecture](architecture.md).
The old Razor package, hosting wrappers, and `RazorFallbackEnabled` setting are removed.
Use the registration example above when migrating existing hosts.

## Build and development

Use .NET 10 and Node.js 24 or later with npm.
MSBuild installs locked frontend dependencies when needed and builds changed frontend sources.
Compiled assets are embedded in the .NET assembly. Production does not need a Node.js server.
The sample Dockerfile builds frontend assets in a separate Node.js stage.

```bash
dotnet build Wafish.slnx -c Release
cd src/Wafish.ReactUi/ClientApp
npm ci
npm run dev
```

For frontend development, set `GATEWAY_URL` to the running gateway HTTP endpoint.
Set `VITE_AUTH_MODE` to match its configured mode when using the Vite development server.
Production bootstrap metadata comes from the host, including prefixes, locale, authentication mode, and enabled integrations.
Development requests use Vite's same-origin proxy. Keep the administration network policy enabled.
Do not place credentials in Vite variables or committed environment files.

```bash
GATEWAY_URL=http://127.0.0.1:5200 VITE_AUTH_MODE=DefaultJwt npm run dev
npm test
npm run lint
npm run build
npx playwright install chromium
npm run test:e2e
```

Browser tests start a disposable .NET host with encrypted temporary storage and a generated password.
They do not need Aspire, external AI credentials, or the Razor assembly.
`PLAYWRIGHT_CHROMIUM_EXECUTABLE` can select an existing Chromium installation.
Use `SkipReactBuild=true` only when compiled `wwwroot` assets already exist, such as the Docker build stage.

## Workflows

- Configuration: routes, clusters, advanced native JSON, rename, activation, batch actions, import, and export.
- Recovery: snapshots, comparisons, rollback, and clear failure outcomes with active and persisted versions.
- Extensions: installation, upgrades, activation, schemas, bindings, presets, and resource statistics.
- Observation: overview, logs, persisted log details, traffic, destination health, circuits, and audit records.
- Settings: log capture, database backup, notifications, two-factor authentication, and optional AI/MCP configuration.
- Assistant: sessions, streaming responses, cancellation, and explicit confirmation before executing suggested write tools.

Native JSON remains available for advanced YARP fields and nested plugin or integration configuration.
Schema-derived binding fields retain server-side validation.
Common route and cluster edits preserve fields outside the form.
Failed writes retain the form draft. Mutations do not retry automatically.

JWT sessions use HttpOnly cookies. API-key and Basic credentials remain in tab memory.
Browser storage contains appearance preferences and the normalized English locale.
Basic and API-key hubs use long polling because browser WebSockets cannot attach custom authentication headers.
Realtime connections close on navigation, pause, and logout. Reconnection restores subscriptions.
Administration labels use English. Unsupported locale headers and saved preferences fall back to `en-US`.

Two-factor state uses atomic file replacement with owner-only Unix permissions.
Set `Gateway:Dashboard:Auth:TwoFactorStatePath` to persistent, protected storage for each gateway.
Unreadable or invalid existing state rejects login. Failed saves return an error.

## Verification and limits

See [UI evidence](../tests/Wafish.Regression/evidence/ui/README.md) for commands and results.
Automated accessibility checks supplement keyboard, focus, responsive, and localization checks.
They do not replace testing with every assistive technology.
External AI-provider and notification delivery require deployment-specific credentials and endpoints.
No production deployment is included.
The former native database blocker is resolved under [SEC-14](security.md#native-sqlite-replacement-sec-14).
