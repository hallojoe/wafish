# UI inventory and design decision

Prepared: 2026-11-14. Owner: Codex. Decision: option C selected by the user.

Open [interactive wireframes](ui-wireframes.html) to compare navigation and inspect an editor.
The prototype uses sample data. It cannot change gateway configuration.

## Workflow inventory

The current UI has 15 page routes, shared editors, and optional AI controls.
Contracts remain in the administration package. Prefixes below are relative to the configured administration prefix.

| Existing workflow | Proposal | Destination | Administration contracts |
|---|---|---|---|
| Overview, system information | Retain; simplify summary cards | Overview | GET api/overview/snapshot, api/info; overview hub |
| Route list, create, edit, rename, enable, delete, bulk operations | Retain; replace modal-heavy editing with a details pane | Configuration / Routes | GET api/routes; PUT/DELETE api/config/routes/{id}; batch endpoints |
| Clusters, destinations, balancing, health, transport | Retain; separate common fields from advanced JSON | Configuration / Clusters | GET api/clusters; PUT/DELETE api/config/clusters/{id} |
| JSON editor, schema help, validation | Retain as an advanced mode; provide a plain textarea fallback | Route and cluster editor | POST api/config/validate; existing schema assets |
| Import, export, presets | Retain; show a review before applying changes | Configuration / Import and export | api/config/import, export, validate; api/presets |
| History, diff, snapshot, rollback, clear history | Retain; show revision and change outcome together | Configuration / History | api/config/history, diff/{versionId}, snapshot, rollback/{versionId} |
| Plugin list, center, bindings, batch bindings | Retain; combine overlapping entry points | Extensions / Plugins | api/plugins, api/plugin-bindings and batch endpoints |
| Plugin resources, lifecycle, health, resource metrics | Retain; use plugin details | Extensions / Resources | api/plugin-resources, totals, {pluginId} and command endpoints |
| Proxy logs, history, details, filtering, capture settings | Retain; keep sensitive capture disabled by default | Observe / Logs | api/logs, history, detail/{id}, stats, settings |
| Traffic and realtime subscriptions | Retain; show connected, reconnecting, and paused states | Observe / Traffic | api/traffic/stats; traffic hub |
| Cluster metrics and circuit state/configuration | Retain; group destination health information | Observe / Destinations | cluster metrics and circuit-breaker endpoints |
| Audit log | Retain; filter by target, operation, result, and time | Observe / Audit | api/audit-logs |
| Notifications, webhook settings, test, history, cooldown | Retain; separate configuration from delivery history | Settings / Notifications | api/webhook/settings, test, history, cooldown/clear |
| System and database settings, backup | Retain; label runtime facts separately from editable settings | Settings / System | api/settings/system, database, database/backup |
| Login, logout, authentication status, two-factor setup | Retain; respect the configured authentication mode | Account | login/logout; api/auth/status; api/2fa/* |
| AI configuration, chat, sessions, tools | Retain only when registered; keep outside primary configuration editing | Optional assistant | api/ai/*; streamed chat |
| MCP enablement and port | Retain only when registered; explain loopback and authentication | Optional integrations | api/ai/config; MCP registration capability |
| Language, theme, sidebar preferences | Retain English; simplify persistent preferences | Account and navigation | presentation preferences and existing locale dictionaries |
| 404, 500, empty/loading states | Replace with consistent recovery actions | Shared states | HTTP status and operation result contracts |

Remove duplicate navigation entry points, decorative dashboard clutter, and automatic editor launches.
Do not remove capabilities, backend APIs, advanced JSON editing, or optional integration controls.

Known defects: the settings link can be obscured; a Monaco worker locale asset returns 404.
Fix both independently of the selected framework.

## Concrete delivery options

All options use the same administration APIs and proposed workflow inventory.
Relative effort is an engineering estimate, not a delivery commitment.

| Option | Framework and components | Navigation in prototype | Migration | Relative effort and main tradeoff |
|---|---|---|---|---|
| A — Modular Razor | Existing Razor, Bootstrap, native controls, extracted CSS/JS modules | Grouped sidebar, table, editor pane | Replace presentation in place; retain current URLs | Lowest; preserves current tooling but keeps JavaScript state management |
| B — Blazor | .NET 10 Blazor Web App, interactive server, Fluent UI Blazor | Compact rail, command bar, editor pane | New optional presentation package; old Razor available during migration | Medium/high; C# components, additional server circuit and reconnect behavior |
| C — React | React, TypeScript, Vite, accessible native controls initially | Top-level tabs, split workspace | Static client package against the same APIs; old Razor fallback | Highest; independent frontend tooling and client authentication/state work |

Recommendation: A when completing the current work quickly is the priority.
Choose B when reusable C# components and a Microsoft-style interface justify a broader rewrite.
Choose C when frontend independence is an explicit product requirement.
The existing UI's complexity comes from its layout and coupling. Razor does not require that layout.

Blazor hosting requires an explicit render-mode choice. This proposal uses interactive server, not an implicit default.
See [Microsoft's hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0).
Fluent components provide a consistent visual language; the library has its own maintenance lifecycle.
See the [Fluent UI Blazor repository](https://github.com/microsoft/fluentui-blazor).
React's standalone path requires selecting routing, data, and build tools.
See [React's build guidance](https://react.dev/learn/build-a-react-app-from-scratch).

## Shared measurable requirements

- Reach routes, clusters, logs, and history within two navigation actions.
- Keep settings visible at 1280×720 and available through mobile navigation at 390×844.
- Support keyboard navigation, visible focus, Escape to close dialogs, and focus restoration.
- Associate labels and validation messages with every editable field.
- Meet WCAG 2.2 AA contrast requirements; never communicate status through color alone.
- Keep all workflows usable at 200% zoom. Limit horizontal scrolling to data tables and JSON editors.
- Preserve drafts on validation, conflict, and storage failures.
- Distinguish active revision from stored revision when activation fails.
- Show realtime connection state and prevent duplicate subscriptions after reconnecting.
- Translate navigation, actions, errors, empty states, and accessibility labels into English.
- Keep runtime logic outside presentation. UI services use administration contracts only.

## Decision record

Framework/component option: **React, TypeScript, Vite, Tailwind CSS, and shadcn/ui**.
User instruction: “Option 3. Make it modern, use chancn and other modern expected setup bits.”
Implementation uses React Router, TanStack Query, React Hook Form, Zod, and Lucide icons.
Navigation and wireframes: option C workspace tabs; responsive navigation and a restrained modern visual design.
Workflow scope: proposed retain/replace inventory above; no capability removal proposed.
Migration approach: optional React presentation. The user subsequently requested complete Razor removal under UI-08.
Acceptance requirements: proposed shared requirements above.

UI-04 through UI-07 now proceed with this recorded decision.
