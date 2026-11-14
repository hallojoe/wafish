# Mutation and registration outcomes

## HTTP configuration responses

Configuration writes use one response contract across registration, routes, clusters, import, rollback, plugin bindings, and plugin state management.
Success responses use `application/json`. Failure responses use `application/problem+json`.
Existing endpoint fields, including `data`, identifiers, and batch details, retain their locations.

| Field | Meaning |
|---|---|
| `status` | Numeric HTTP status. |
| `code` | Compatibility alias for the HTTP status. |
| `outcome` | Named application outcome. |
| `success` | Whether the operation completed successfully. |
| `message` | Operation summary. Failures also expose `type`, `title`, and `detail`. |
| `persistedVersion` | Confirmed stored revision, or `null`. |
| `activeVersion` | Confirmed active revision, or `null`. |

Revisions are nullable when validation occurs before a mutation or an unexpected failure prevents confirmation.
Plugin binding revisions remain in their endpoint-specific data; they are distinct from gateway configuration revisions.

| Outcome | HTTP status | Meaning and next action |
|---|---:|---|
| `Applied` | 200 | The operation completed. |
| `ValidationRejected` | 400 | Correct the request. |
| `VersionConflict` | 409 | Reload and compare revisions before retrying. |
| `StorageFailed` | 503 | Storage rejected the change. Active configuration remains unchanged. |
| `ActivationFailed` | 500 | A stored revision may exceed the active revision. Recover activation before another mutation. |
| `InternalError` | 500 | The outcome is uncertain. Inspect configuration before retrying. |

Authorization and missing-resource failures use their existing HTTP codes.
They expose `Unauthenticated`, `PermissionDenied`, or `NotFound` when handled by the mutation response filter.
Authentication middleware can reject requests before MVC response filters run.

## Consumer migration

The HTTP `status` field previously carried the application outcome on some endpoints.
Read `outcome` for that value. Read `status` or `code` for the numeric HTTP status.
The React client accepts both response formats during migration.
Unknown revisions display no revision label. A missing revision is not zero.

```json
{
  "status": 500,
  "code": 500,
  "outcome": "ActivationFailed",
  "success": false,
  "message": "Configuration was stored but activation failed.",
  "persistedVersion": 12,
  "activeVersion": 11,
  "type": "about:blank",
  "title": "Internal Server Error",
  "detail": "Configuration was stored but activation failed."
}
```

## Registration client

Use `RegisterResultAsync`, `UnregisterResultAsync`, or `UpdateDestinationsResultAsync` for structured results.
Boolean wrappers remain available. Missing-route unregistration remains successful through its Boolean wrapper.

`RegistrationResult` includes status, confirmed revisions, remote status text, `RetryAdvice`, and `MayHaveApplied`.
Caller cancellation propagates as `OperationCanceledException`, including through Boolean mutation wrappers.
A cancelled request can still complete remotely after its commit begins. Inspect state before retrying.

The client never automatically replays these mutation calls.
Storage rejection permits a retry. Conflicts require a revision refresh.
Credentials and validation require request correction.
Transport failure, timeout, unknown outcomes, and activation failure require inspection before retrying.
A transport error cannot prove that the gateway rejected an operation before commit.

Old successful responses without revisions remain supported. Their revisions remain `null`.
Old failed responses without outcomes map to `Unknown`. Future outcome names remain available through `RemoteStatus`.
Contradictory success and outcome fields map to `Unknown`.

## gRPC mapping

The `wafish.v1.GatewayRegistry` methods and field numbers remain unchanged.
Application mutation failures return gRPC `OK` with `success=false`, a named `status`, and available revision fields.
This preserves response compatibility with older clients.

| gRPC transport status | Client outcome |
|---|---|
| `InvalidArgument` | `ValidationRejected` |
| `Unauthenticated` | `Unauthenticated` |
| `PermissionDenied` | `PermissionDenied` |
| `NotFound` | `NotFound` |
| `Aborted`, `AlreadyExists` | `VersionConflict` |
| `Unavailable` | `Unavailable` |
| `DeadlineExceeded` | `DeadlineExceeded` |
| `Internal` | `InternalError` |
| Other statuses | `Unknown` |

Unexpected service exceptions return sanitized `Internal` errors.
Configured registration timeouts also set gRPC deadlines.
Existing catch-all route parameters keep their names and do not receive a second catch-all segment.
Fixed [wire fixtures](../tests/contracts/grpc/README.md) verify old messages, additive fields, unknown fields, and optional presence.
