# Saved operator views

The administration console can save named table filters and visible columns for an authenticated account.
Log and audit views also retain their server filters, log source, and minimum status.
Selecting, creating, or updating a view makes it the default for that workspace.
The last saved selection restores after sign-in or page reload.

Use **Save new view** for a new name and **Update view** for an existing selection.
Use **Visible columns** to select at least one column. Table actions remain available.
**Delete view** removes the saved entry; current filters remain as an unsaved draft.
A failed write preserves current filters and the name draft.
After a revision conflict, **Reload saved views** refreshes revisions without replacing those drafts.

## Ownership

The server derives ownership after successful administration authentication. Clients cannot supply an owner.
Display names, request headers, IP addresses, and actor labels do not establish ownership.
The database stores an opaque identity hash, not account names, passwords, API keys, or session tokens.
Preferences include only explicit filter values, column identifiers, names, and revisions. Row data and request headers are excluded.
Filter text and view names are retained as entered; use them only for information suitable for stored preferences.

| Administration mode | Private view identity |
| --- | --- |
| Basic authentication | Verified account username, scoped to Basic authentication. |
| Default or custom JWT | Verified subject, scoped to the configured JWT mode. New sessions retain the account's views. |
| Custom authorization delegate | Exactly one authenticated identity with one stable `NameIdentifier` or `sub` claim. Authentication type and claim issuer boundaries participate in ownership. |
| API key or anonymous access | Unavailable. These modes cannot identify an individual account. |

A custom delegate must authenticate and authorize the caller before setting its principal and returning success.
Ambiguous subjects or multiple authenticated identities make private views unavailable.
Replacing `IDashboardAuthorizationService` bypasses the built-in private identity integration and leaves saved views unavailable.
Use the built-in service's custom authorization delegate to integrate another identity system.
Visitor authentication does not automatically grant administration access or private operator preferences.

People sharing one account share that account's preferences.
Changing usernames, authentication modes, authentication types, or subject issuers creates a different preference namespace.
Old entries remain until their owner deletes them or database maintenance removes them.
Credential rotation within the same identity preserves preferences.
Account data remains accessible to authorized database and backup administrators.

## Persistence and bounds

SQLite migration `023_operator_views` adds a separate preference table.
View writes do not alter configuration revisions, activate plugins, or publish gateway settings.
Custom storage providers can implement the optional `IOperatorViewRepository` contract.
Without that provider, the console hides saved-view controls and the API reports unavailable persistence.

Names are unique within one account and workspace, using ordinal case-sensitive comparison.
Each account can retain 50 views across all workspaces. The database admits 4,096 views in total.
At capacity, existing views can still be updated or deleted.
Each workspace has at most one default view for an account.
Default selection and optimistic revisions change in one transaction.
Failed writes and cancelled transactions preserve the previous default and preferences.

| Field | Bound |
| --- | --- |
| Workspace | 1–64 lowercase ASCII letters, digits, or hyphens. |
| Name | 1–64 characters; not whitespace-only and no control characters. |
| Table search and server filter | Each at most 256 characters, without control characters. |
| Route filter | At most 128 characters, without control characters. |
| Minimum HTTP status | Null or an integer from 100 through 599. |
| Columns | 1–32 unique identifiers, each 1–64 ASCII letters, digits, underscores, dots, or hyphens. |
| Write body | At most 8 KiB. Unknown JSON properties are rejected. |

Removed column identifiers are ignored by the console. If none remain, all current columns are shown.
Pagination, selected data rows, expanded details, live payloads, and credentials are not stored.
Views are held only in component memory during a session. Authentication changes clear the workspace and cancel pending preference requests.
No preference or credential data is added to browser storage by this feature.

## API

Routes below use the default `/wafish` administration prefix. Custom prefixes and `PathBase` remain supported.
Authentication and the `Read` operation permission apply to every route, including personal preference writes.
Private preferences do not require permission to change gateway configuration.

| Route | Purpose |
| --- | --- |
| `GET /wafish/api/operator-views/{workspace}` | Read availability and the caller's views. |
| `PUT /wafish/api/operator-views/{workspace}/{id}` | Create or update one caller-owned view. Use a GUID identifier. |
| `DELETE /wafish/api/operator-views/{workspace}/{id}?expectedRevision=1` | Delete a caller-owned view at its current revision. |

All responses use `Cache-Control: no-store`.
Writes require `X-Wafish-Operator-View: 1`. PUT requires JSON.
A supplied `Origin` must match the request's scheme and host. Machine clients can omit Origin but must send the explicit header.

See the [HTTP request examples](operator-views.http) for authenticated reads, creation, and deletion.

Example PUT body:

```json
{
  "name": "Backend failures",
  "settings": {
    "search": "",
    "filter": "timeout",
    "route": "support-api",
    "minimumStatus": 500,
    "historical": true,
    "columns": ["timestamp", "routeId", "statusCode"]
  },
  "isDefault": true,
  "expectedRevision": 0
}
```

Revision zero creates a view. Updates and deletions require the revision returned by the latest read.
Selecting another default also increments the previous default's revision.
Conflicts, duplicate names, and capacity failures return HTTP 409 with a named outcome.
A foreign or missing deletion returns 404. Clients cannot enumerate another owner's entries.
Unsupported account identities return `available: false` on reads and 403 on writes.
Missing storage returns unavailable reads and 503 writes.

Verification covers database rollback, concurrent updates, account and issuer isolation, session renewal, UI drafts, and browser restoration.
See [identity handling](../src/Wafish.Administration/Infrastructure/Auth/DashboardUserIdentity.cs),
[repository](../src/Wafish.Storage.Sqlite/SqliteOperatorViewRepository.cs),
[API](../src/Wafish.Administration/Modules/Dashboard/Controllers/OperatorViewsController.cs),
and [console controls](../src/Wafish.ReactUi/ClientApp/src/components/saved-views.tsx).
