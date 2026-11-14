# React UI implementation plan

Approved stack: React, TypeScript, Vite, Tailwind CSS, shadcn/ui with Radix primitives.
Use React Router, TanStack Query, React Hook Form, Zod, and Lucide icons.
Decision recorded in [ui-design.md](ui-design.md).

All delivery tasks are complete as of 2026-11-14. Owner: Codex.
Evidence: [workflow, accessibility, and packaging checks](../tests/Wafish.Regression/evidence/ui/README.md).

## Delivery tasks

| Task | Implementation | Acceptance |
|---|---|---|
| UI-05.1 | Optional Wafish.ReactUi package; compiled frontend assets; responsive workspace navigation; light/dark theme | Loads without Razor; deep links work; navigation fits desktop and mobile |
| UI-05.2 | Mode-aware login; cookie JWT; memory-only API credentials; two-factor controls; logout | Protected API failures return to login; no secret persists in browser storage |
| UI-05.3 | Routes and clusters; common-field editors; advanced JSON; validation; rename; enable; delete; batch actions; import/export; presets | Preserve native configuration fields and drafts after failed saves |
| UI-05.4 | Plugin inventory, activation, bindings, schema-guided configuration, resource lifecycle | Bindings and lifecycle actions use administration APIs |
| UI-05.5 | Overview, logs, traffic, destination metrics, circuits, audit; realtime status | No duplicate subscriptions; pause/reconnect states visible; log detail remains available |
| UI-05.6 | History, snapshots, diffs, rollback; structured operation errors | Validation, conflict, storage, and activation outcomes remain distinct |
| UI-05.7 | System/database settings, backups, notification configuration/history, optional AI and MCP | Disabled integrations stay hidden; sensitive actions require explicit user interaction |
| UI-06 | Browser and API-adapter tests; failure injection; production asset build | Successful workflows and actionable error states pass |
| UI-07 | Keyboard, focus, responsive layouts, contrast, English checks | No critical accessibility violations; approved layouts and translations pass |
| UI-08 | Remove Razor project, pages, assets, wrappers, and fallback controls | React workflows pass; old pages are unavailable; published sample contains no Razor UI |

## Boundaries

The React package references administration contracts and hosting extensions only.
The browser uses HTTP APIs and SignalR. It never reads gateway runtime services directly.
Serve the UI at `/console`. Administration APIs remain under `/wafish`.
UI-08 removes the former Razor client at the user's request.
Standalone hosts can register React and administration without adding Razor.

Bundle production assets with the .NET package. Node.js is a build dependency, not a production server dependency.
Keep package-lock.json and strict TypeScript checks. Run npm audit and frontend tests before completion.
Use same-origin API calls. Development proxy configuration must preserve the host's origin policy.
Do not introduce default retries for mutation requests.

## State and errors

TanStack Query owns server state. Component forms own unsaved drafts.
Invalidate related query keys after mutations, including partial failures.
Use the normalized English locale in query keys.
Keep drafts and selected records on failures.
Use operation status and persisted/active versions in error messages.
A stored but inactive revision must never appear as a successful save.

JWT login uses the HttpOnly cookie. Do not persist returned tokens in browser storage.
API-key and Basic credentials remain in memory for that browser session.
Use SignalR long polling when browser transports cannot carry required authentication headers.
Disconnect hubs and clear cached data during logout.

## Migration

The verified React workspace replaces the removed Razor package and page URLs.
The sample advertises `/console` as its new administration entry point.
Consumers choose React presentation or API-only hosting.
No production deployment is part of this task.
UI-08 removes the old interface after successful replacement verification.
