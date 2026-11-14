# Standalone sample HTTPS

Build the solution from the repository root:

```bash
dotnet build Wafish.slnx -c Release
python scripts/check-sample-https.py
```

The script implements a complete disposable HTTPS recipe.
It creates a one-day localhost certificate with DNS and IP subject alternatives.
It exports the certificate as a password-protected PFX and configures Kestrel directly.
The client trusts only the generated certificate for this test. Certificate validation remains enabled.
No machine-wide certificate trust is changed.

The script injects random login and signing secrets through process environment variables.
It creates a private SQLite directory and uses the custom UI prefix `secure/console`.
It verifies readiness, anonymous rejection, JWT login, secure HttpOnly cookies, authenticated administration, and proxy forwarding.
It also checks the startup operator URL against the actual HTTPS listener.
The script stops its process and deletes its temporary files after verification.

## Persistent manual hosts

Use a certificate trusted by your client for an enduring local host.
Configure these settings through your private host configuration:

| Setting | Purpose |
|---|---|
| `Kestrel:Endpoints:Gateway:Url` | HTTPS origin, such as `https://localhost:8443` |
| `Kestrel:Endpoints:Gateway:Certificate:Path` | Absolute path to the PFX file |
| `Kestrel:Endpoints:Gateway:Certificate:Password` | Certificate password |
| `Gateway:Dashboard:Auth:JwtPassword` | Administrator login password |
| `Gateway:Dashboard:Auth:JwtSecret` | Strong, persistent JWT signing secret |
| `Gateway:Dashboard:Auth:TwoFactorStatePath` | Private writable authentication state path |
| `Gateway:Storage:Sqlite:ConnectionString` | Private writable database location |
| `Gateway:ReactUi:RoutePrefix` | Browser workspace path, such as `secure/console` |
| `Gateway:Dashboard:RoutePrefix` | Administration API path, such as `secure/admin` |

Environment variables replace colons with double underscores.
Keep the gRPC endpoint separate when HTTP/2 cleartext is required.
Use the printed Dashboard URL after startup. Do not construct it from a fixed HTTP port.

The sample does not configure forwarded-header trust.
Behind a reverse proxy, retain HTTPS on the gateway connection.
Do not trust arbitrary `X-Forwarded-Proto` or client-address headers.
If you customize forwarded-header middleware, explicitly restrict its trusted proxies before authentication middleware runs.
Secure cookies require an HTTPS browser connection.

## Non-root container smoke check

```bash
podman build -f samples/SampleGateway/Dockerfile -t localhost/wafish:smoke .
python scripts/check-sample-https.py --image localhost/wafish:smoke
```

Docker is supported with `--engine docker` and an image built by Docker.
The image declares a non-root user. The check also verifies the running user ID.
Podman maps that user to the caller for private writable storage.
The HTTPS port is published only on host loopback.
Injected credentials remain in a private temporary environment file.
The check removes only its own container and temporary files. It retains the image.
The SDK stage includes Node.js for packaged asset verification; the runtime stage contains no Node.js dependency.
