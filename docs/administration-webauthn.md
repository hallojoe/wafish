# Administration authenticators

Wafish supports passkeys and FIDO2 security keys for its configured JWT administration account.
Enable this feature explicitly. It supports `DefaultJwt` and `CustomJwt` authentication.
The authenticator must verify its user, for example through a PIN or biometrics.
Configured TOTP remains required. Password and TOTP login remain available for recovery.

This feature uses [Fido2 4.3.3](https://github.com/passwordless-lib/fido2-net-lib/tree/4.3.3) for WebAuthn verification.
It requests ES256 or RS256 credentials without attestation identification.
It does not restrict authenticator vendors or establish device trust through attestation metadata.
Visitor authentication remains separate. See [external login](../src/Wafish.Plugin.ExternalLogin/README.md).

## Configure the host

```json
{
  "Gateway": {
    "Dashboard": {
      "Auth": {
        "AuthMode": "CustomJwt",
        "JwtUsername": "operator",
        "WebAuthn": {
          "Enabled": true,
          "RelyingPartyId": "gateway.example.com",
          "RelyingPartyName": "Gateway administration",
          "Origins": ["https://gateway.example.com"],
          "CredentialStorePath": "/var/lib/wafish/webauthn-credentials.json"
        }
      }
    }
  }
}
```

Supply `JwtPassword` and `JwtSecret` through the host's secret configuration.
Keep the credential file separate from `TwoFactorStatePath`.
Configure a private, persistent directory writable by the Wafish process.
Generated files use owner-only permissions on Unix. Configure equivalent directory permissions on Windows.

`RelyingPartyId` is a DNS name without a scheme, port, or path.
Each origin must use that exact hostname. Parent-domain and subdomain sharing are not supported.
Origins include the scheme and optional port. They cannot contain credentials, paths, queries, or fragments.
HTTPS is required, except for HTTP `localhost` during development.
The existing control-plane origin policy also applies.
Behind a trusted proxy, configure the external scheme and host correctly.

Configure these options before startup. Changes require a host restart.
Changing the account or relying-party ID makes an existing credential file incompatible.
Startup rejects incompatible, corrupt, or oversized files. It does not silently replace them.

## Register and use an authenticator

1. Sign in with the configured account password and TOTP, when enabled.
2. Open **Settings → Account → Authenticators**.
3. Enter the current password, configured TOTP code, and an authenticator name.
4. Select **Add authenticator**. Complete the browser's verification prompt.
5. Sign in again. Successful enrollment revokes existing JWT sessions and their realtime connections.
6. Enter the username and select **Sign in with an authenticator**.
7. Enter TOTP when requested. Complete the browser's verification prompt.

Remove a credential from the same account panel. Removal requires fresh password and configured TOTP proof.
Successful removal revokes all current JWT sessions. Failed persistence preserves existing sessions and credentials.
A browser without WebAuthn support can still use password recovery and remove registered credentials.

If an authenticator is lost, sign in with the password and configured TOTP. Remove the lost credential.
This feature adds no recovery codes or bypass endpoint.
Loss of all configured recovery credentials requires the host's existing administrator recovery procedure.

## Storage and session boundaries

The credential file contains public keys, credential IDs, account binding, signature counters, and display metadata.
It contains no authenticator private keys or passwords. Protect its integrity and backup access.
Only one process can own a credential file. An exclusive lease rejects simultaneous writers.
Credentials survive host restarts. JWT sessions and pending ceremonies do not.

JWT sessions remain local to one host. This feature does not add shared multi-host account storage or sessions.
Do not share the credential file between active replicas.
Synced passkeys are accepted when they satisfy user verification and counter checks.
Backup eligibility cannot change after registration. Verified backup state is persisted with successful assertions.

| Bound | Limit |
| --- | --- |
| Registered credentials | 20 |
| Credential label | 64 characters |
| Pending ceremonies | 1,024 per host |
| Ceremony lifetime | 2 minutes |
| Ceremony request body | 64 KiB |
| Credential state file | 1 MiB |
| Configured origins | 16 |

Ceremonies are single-use and bound to the account, operation, and security generation.
Enrollment also binds to the active JWT session. Login binds to a secure, HttpOnly, SameSite=Strict browser cookie.
WebAuthn attempts use the existing bounded source and account authentication limits.
Exhaustion returns HTTP 429 with `Retry-After`.
The server persists the verified signature counter before issuing a JWT session.

## API entry points

Paths below follow the configured administration prefix. Preserve `PathBase` when hosting beneath a parent path.
All mutations require an exact allowed `Origin`. Credential management requires `CredentialManagement` permission.
Login options and completion are public authentication entry points, subject to control-plane and attempt limits.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `api/webauthn/credentials` | List public credential descriptions and enabled state. |
| GET | `api/webauthn/credentials/{credentialId}` | Read one credential description. |
| POST | `api/webauthn/enrollment/options` | Verify fresh proof and create browser enrollment options. |
| POST | `api/webauthn/enrollment/complete` | Verify and persist enrollment; return HTTP 201. |
| POST | `api/webauthn/login/options` | Create browser-bound assertion options. |
| POST | `api/webauthn/login/complete` | Verify assertion and TOTP; issue the JWT cookie. |
| POST | `api/webauthn/credentials/remove` | Verify fresh proof, remove a credential, and return HTTP 204. |

Responses use numeric HTTP status codes. Failure responses contain `code` and `message`.
See the [HTTP examples](administration-webauthn.http) and [service contract](../src/Wafish.Administration/Infrastructure/Auth/IDashboardWebAuthnService.cs).
