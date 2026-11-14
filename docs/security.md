# Security configuration and verification

## Local execution and production access

Running locally reduces exposure only when listeners use loopback interfaces.
Environment names do not grant access. Development, Staging, and Production use the same checks.

- Authentication is required for administration by default.
- Anonymous access requires `Gateway:Security:ControlPlane:AllowAnonymousLoopback=true` and authentication mode `None`.
- Anonymous requests require loopback source, destination, and Host values.
- Forwarding headers disqualify anonymous loopback requests.
- Remote administration requires HTTPS and valid credentials.
- Browser origins, when present, must match the requested scheme and authority.
- Registration must be enabled explicitly. Disabled registration removes REST and gRPC mappings.
- AI and MCP require separate service registration. Configuration cannot enable unregistered features.

Loopback access is a local development convenience. It does not protect against other local processes.
Use authentication on shared development machines. Restrict production administration through network controls and HTTPS.
Configure trusted proxies explicitly before forwarding headers. Never accept arbitrary client forwarding headers.
See [ASP.NET Core proxy configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Credentials and precedence

`Gateway:Security:ControlPlane` is the shared configuration section.
Its explicit settings override legacy `Gateway:ApiAuth` and dashboard authentication settings.
Explicit registration callbacks apply last.
Supported modes are `ApiKey`, `BasicAuth`, `DefaultJwt`, `CustomJwt`, and explicit local `None`.
Empty credentials and unknown modes fail validation.
JWT mode accepts signed Bearer tokens. It does not accept the login password as a Bearer token.
JWT validation requires HS256, issuer, subject, signature, and expiry.

Use environment variables or a host secret provider. Do not commit working credentials.
For the standalone sample, set a password before startup:

```bash
read -rsp 'Dashboard password: ' Gateway__Dashboard__Auth__JwtPassword
export Gateway__Dashboard__Auth__JwtPassword
ASPNETCORE_AUTO_BROWSER=0 dotnet run --project samples/SampleGateway --no-launch-profile
unset Gateway__Dashboard__Auth__JwtPassword
```

The sample uses username `admin`. Missing passwords prevent startup.
Both sample listeners bind to `127.0.0.1`. Registration is disabled by default.
Startup output does not print credentials.

For shared API-key authentication, supply these variables through your secret provider:

```text
Gateway__Security__ControlPlane__AuthMode=ApiKey
Gateway__Security__ControlPlane__ApiKey=<secret>
```

For JWT, inject `Gateway__Security__ControlPlane__JwtSecret` with at least 32 UTF-8 bytes.
Without an injected key, the host creates `Data/.jwt-secret` under its content root.
Unix permissions are restricted to the owner. Preserve this file across restarts and protect backups.
A malformed persisted key fails startup. It is not silently replaced.
API keys are header-only. URL JWT tokens are restricted to the exact traffic and overview hub paths.

## Browser cookies and CORS

Dashboard login cookies always use `Secure` and `HttpOnly`.
Use HTTPS for browser access outside localhost.

`UseWafIshGateway()` applies the host's configured CORS policies.
It does not grant access to every origin by default.
For cross-origin clients, register an explicit policy before building the host:

```csharp
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("https://dashboard.example.com")
    .WithMethods("GET", "POST")
    .WithHeaders("Authorization", "Content-Type")));
```

Choose origins, methods, and headers that match your clients.
Named YARP route policies remain supported.

## Registration client TLS

The client uses platform certificate validation in every environment.
Untrusted or mismatched certificates fail. Trust development certificates through the normal platform store.
`Gateway:Registration:GrpcUrl` selects an explicit gRPC endpoint without the legacy HTTP port-plus-one assumption.

## MCP

MCP binds only `127.0.0.1` on its configured port. It never scans for alternative ports.
The parent authentication policy protects every request. Anonymous parent authentication prevents listener startup.
Untrusted Origin headers are rejected. Wildcard CORS is not enabled.
Requests over 64 KiB are rejected. The listener stops with its parent host.
See [MCP transport requirements](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/basic/transports/streamable-http.mdx).

## Buffer ownership

`CacheOptimizedRingBuffer<T>` now shares managed storage across struct copies.
Repeated disposal cannot free native memory twice. Disposed buffers reject reads and writes.
The struct no longer has unmanaged layout. Native interop consumers must update their integration.
Concurrent access still requires external synchronization.

## Response caching and compression

The cache requires explicit public responses. Authentication, cookies, ranges, and conditional requests bypass caching.
Private, no-store, no-cache, Set-Cookie, partial, and bodyless responses cannot populate it.
GET and HEAD share representations. HEAD misses never populate GET entries.
Response freshness respects s-maxage, max-age, Expires, Age, and the configured TTL ceiling.
Request freshness restrictions are enforced. An unsatisfied only-if-cached request returns 504.

Keys include route generation, scheme, host, path, query, and representation headers.
Operational tracing headers and request cache directives are excluded.
Responses that Vary on excluded headers or `*` are not stored.
Legacy `varyByQuery=false` and narrower `varyHeaders` cannot reduce this isolation.
Configuration publication invalidates the previous generation.

`Gateway:ResponseCache:SizeLimitBytes` sets the total budget. The default is 64 MiB.
The budget covers reserved body capacity, header allowance, captures, and entries still being read after eviction.
Entries use least-recently-used eviction. Capture overflow releases its reservation and continues forwarding all bytes.
Each capture reserves `maxBodyBytes` plus 65 KiB for metadata. Small budgets can disable capture.
The budget accounts for retained cache data, not the entire process or transport buffers.

Compression runs inside the cache boundary. Compressed variants retain their encoding and Vary headers.
Large responses stream. Unknown-length responses use at most 64 KiB of prefix storage.
A flush before the minimum size selects uncompressed streaming.
Unknown-length responses with a larger minimum can bypass compression.
Ranges, bodyless responses, existing encodings, and no-transform responses bypass compression.
Encoding selection respects exact tokens, quality values, and explicit exclusions.
The runtime applies framework compression only to administration pages and assets.
Proxy compression requires the route compression plugin.

## WAF body inspection

Inspection reads all permitted bytes, including short reads and unknown-length bodies.
Configured body limits count bytes. Injection scanning has a 100 KiB inspection ceiling.
Exceeding either applicable boundary returns 413. Required inspection never skips overflowing content.
Inspection rejects compressed bodies and unsupported text encodings with 415.
Invalid UTF-8 returns 400. Read failures and regex timeouts fail closed with 503.
Successful inspection rewinds the body for downstream consumers. Cancellation propagates.

## Redis rate limits

The Redis client uses typed StackExchange.Redis APIs and persistent connections.
Use `Gateway:RateLimit:Redis:ConnectionString`, or a binding's `redisConnectionString` override.
`Gateway:Storage:Redis` does not configure rate limiting.

`failureMode` accepts `FailClosed` or `FailOpen`. The default is `FailClosed`.
During outages, FailClosed returns 503 with Retry-After. FailOpen explicitly permits requests.
Exhausted counters return 429 in both modes.
Recovery reconnects and resumes shared enforcement. No implicit localhost connection is assumed.
Connection failures log the exception type without the connection string.

## Administration session and two-factor storage

The React UI stores Basic and API-key credentials only in tab memory.
JWT sessions use the existing HttpOnly cookie. Browser storage contains language and appearance preferences only.
Basic and API-key requests omit ambient browser credentials and send explicit authentication headers.
An explicit locale header preserves translations without requiring cookies.
These modes use authenticated SignalR long polling.

Two-factor state uses atomic file replacement and owner-only Unix file permissions.
Set `Gateway:Dashboard:Auth:TwoFactorStatePath` to protected, persistent storage for each gateway.
Unreadable or malformed existing state rejects login with HTTP 503.
Failed state writes return HTTP 503. They cannot report successful setup or disablement.
JWT password verification uses constant-time hash comparison.
Windows deployments must restrict access through directory and file ACLs.

## Native SQLite replacement (SEC-14)

The retired `SQLitePCLRaw.bundle_e_sqlcipher` package has been replaced with `SQLite3MC.PCLRaw.bundle` 2.7.0.
Runtime checks load SQLite3 Multiple Ciphers 2.7.0 and SQLite 3.54.1.
The former dependency loaded SQLCipher 4.5.2 with SQLite 3.39.2.
The replacement exceeds the SQLite versions listed for the reviewed vulnerabilities, including CVE-2025-6965.
See the [SQLite advisory table](https://sqlite.org/cves.html) and [maintainer release notes](https://github.com/utelle/SQLite3MultipleCiphers-NuGet/blob/main/CHANGELOG.md).

The native dependency release blocker is resolved after compatibility and published-container verification.
See [SEC-14 evidence](../tests/WafIsh.Regression/evidence/sec14/README.md).
This does not replace deployment-specific configuration and external integration checks.

### Encryption is optional

The checked-in sample and Aspire connection strings do not enable database encryption.
Without a `Password` setting, the database remains ordinary SQLite.
A password opens an existing encrypted database or creates a new encrypted database.
Adding a password does not convert an existing plaintext database automatically.

The connection factory selects the SQLCipher 4 file format for password-protected file connections.
Existing default SQLCipher 4 databases retain their format and password.
Explicit URI cipher settings remain supported.
See the [maintainer's SQLCipher compatibility instructions](https://github.com/utelle/SQLite3MultipleCiphers-NuGet#how-to-open-an-existing-database-encrypted-with-sqlcipher).

Supply passwords through a secret provider. Keep existing passwords during this dependency upgrade.
Back up databases before deploying an upgrade. Do not convert or rekey live files as part of this change.
Use `Microsoft.Data.Sqlite.Core` with the single SQLite3MC bundle; do not add a competing native bundle.

### Backup consistency

The backup endpoint opens databases through the same connection factory as normal storage operations.
Backups retain committed WAL changes and preserve encryption.
A failed backup returns HTTP 503. It never falls back to copying a live main database file.
The raw database-download endpoint remains distinct from the consistent backup endpoint.
Use `/aneiang/api/settings/database/backup` for a consistent live backup, adjusted for your administration prefix.
