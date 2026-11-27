# Wafish.Plugin.Redirect

The `redirect` plugin returns route redirects before YARP requires a backend.
Runtime registers the plugin and compiler. Bind configuration through the existing plugin editor or binding API.

## Configuration

```json
{
  "mode": "Target",
  "target": "/docs/{**route.path}",
  "statusCode": 307,
  "queryMode": "Discard",
  "allowedHosts": [],
  "httpsPort": 443
}
```

Use this example with a route matching `/old/{**path}`.
A redirect route can omit `ClusterId`. A nonempty cluster reference must identify an existing cluster.
Create the route, then its binding. Use a configuration change set when both must activate together.
Each route permits one enabled redirect binding. Binding activation controls whether redirects run.

| Setting | Default | Meaning |
|---|---|---|
| `mode` | `Target` | `Target` expands a destination. `HttpsOnly` upgrades HTTP requests. |
| `target` | None | Required in `Target` mode. Prohibited in `HttpsOnly` mode. |
| `statusCode` | `307` | Accepts `301`, `302`, `303`, `307`, and `308`. |
| `queryMode` | `Discard` | Discard incoming parameters, or explicitly `Preserve` them. |
| `allowedHosts` | Empty | Exact destination hosts required for host templates and HTTPS mode. |
| `httpsPort` | `443` | HTTPS mode's destination port, from 1 to 65535. |

`307` is temporary and preserves the request method and body. `308` provides permanent behavior with method preservation.
Clients can change POST to GET for `301` and `302`. `303` directs clients to retrieve the target.
Permanent redirects can remain cached after configuration changes. Start migrations with temporary redirects.

Literal target query parameters always remain. `Preserve` adds incoming parameters with names absent from the target.
Names are case-sensitive. Incoming duplicate parameters remain unless the target overrides their name.
Fragments are literal and appear after the resulting query string.

## Templates and destination hosts

| Token | Placement | Meaning |
|---|---|---|
| `{route.name}` | Path or hostname label | Captured route value, encoded for its URL component. |
| `{**route.name}` | Path | Catch-all route value, preserving path separators. |
| `{request.host}` | Hostname | Effective request hostname without its port. |
| `{request.path}` | Path | Original request path, including `PathBase`. |

Schemes and ports must be literal. Query values, arbitrary headers, and fragments cannot supply template values.
Unknown tokens fail validation. Missing required runtime values return `400` without a `Location` header.
An empty catch-all expands to an empty path value.

For a route matching `/tenant/{tenant}/{**path}`:

```json
{
  "target": "https://{route.tenant}.example.com/{**route.path}",
  "allowedHosts": ["shop.example.com", "docs.example.com"]
}
```

Only these exact expanded destination hosts are accepted. Wildcards, ports, and user information are prohibited in allowlists.
Host comparisons normalize case, IDN names, IP literals, and trailing dots.
A fixed absolute target already explicitly configures its destination host.

For a domain migration preserving the full path:

```json
{"target":"https://new.example.com{request.path}","statusCode":307}
```

Targets must be local absolute paths or HTTP(S) URLs. Protocol-relative URLs and unsafe characters are rejected.
Templates and resulting URLs are limited to 8 KiB. Allowlists permit at most 128 entries.
Static self-redirects fail validation when the source is provable. Expanded self-redirects return `400`.
The plugin does not analyze chains or contact external destinations.

## HTTPS enforcement

```json
{"mode":"HttpsOnly","allowedHosts":["example.com"],"httpsPort":443}
```

HTTP requests redirect to the effective hostname and original path. Query retention remains explicitly configurable.
HTTPS requests continue through normal forwarding. Without a backend, continuing requests return `503`.
The same behavior applies when the redirect binding or plugin is disabled.

Configure trusted forwarded headers before gateway middleware when TLS terminates at another proxy.
The plugin uses `Request.Scheme` and `Request.Host`. It never interprets raw forwarded headers itself.

## Execution and observation

Native endpoint matching, authorization, CORS, and configured timeout middleware remain in effect.
The existing WAF middleware runs before endpoint execution. Enabled local and Redis limits run before the redirect response.
Logs and route metrics observe redirects. No destination or cluster forwarding measurement is invented.
Redirects bypass backend selection, maintenance checks, retry, proxy caching, and compression.
Responses contain the status, validated `Location`, and an empty body. Existing response security headers remain applicable.

Use `UseWafishGateway()` for the complete middleware setup.
Manual hosts must run authentication, authorization, and `UseWafishRuntime()` after routing and before endpoint execution.
`MapWafishProxy()` installs the redirect endpoint wrapper. Disabling its built-in pipeline also disables redirects.
The plugin requires host capability `wafish.redirect-endpoint` version 1.

## Preview

Open a redirect binding in the existing plugin editor. Enter a sample URL, method, path base, and matching headers.
Preview shows the selected route, configuration revision, outcome, status, and `Location`.
Changes to preview inputs hide obsolete output.

The authenticated endpoint is `POST /{administration-prefix}/api/plugins/redirect/preview` and requires read permission:

```json
{
  "binding": {
    "pluginId": "redirect", "scope": "Route", "scopeId": "old-docs",
    "enabled": true, "schemaVersion": 1,
    "configJson": "{\"target\":\"/docs/{**route.path}\"}"
  },
  "request": {"url":"https://example.com/old/install", "method":"GET", "pathBase":"", "headers":{}}
}
```

Preview uses actual endpoint matching and the runtime URL evaluator. Another winning route produces `RouteMismatch`.
Preview does not execute access policies, consume quotas, change health state, save configuration, or contact destinations.
It therefore predicts redirect behavior only. Authentication, WAF, and rate limits can still reject the real request.

Bulk imports, dedicated redirect pages, and redirect-chain analysis are outside this release.
