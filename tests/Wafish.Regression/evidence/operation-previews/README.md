# Operation permissions and previews: R15–R18

Completed on 2026-11-14. Base commit: `533ed7b`.

- R15 adds read, configuration-write, plugin-management, and credential-management permissions.
- R16 adds durable idempotency receipts for the new configuration apply workflow.
- R17 adds configuration validation, normalization, and identity-level differences without publication.
- R18 adds candidate plugin health and resource reports without activation.

Implementation commits: `bcd7ff5`, `526eced`, and `21be9fb`.

## Verification

- Release solution build: zero warnings and errors.
- Main regression suite: 252 passed.
- Administration regression suite: 40 passed.
- Browser checks: 30 passed.
- Three isolated package consumers: runtime, administration, and React UI.
- API snapshots retain the previous release declarations and include the new public contracts.
- Changed executable-line coverage: 289/293, or 98.63%.

See [case results](checks.json) and [coverage totals](changed-line-coverage.json).

The full main suite preceded the final audit-order correction.
All seven affected configuration checks passed afterward, including audit retention after acknowledgement failure.
Coverage uses the final focused run for the changed configuration service.
The final administration run covers the permission-binding correction and current permission source lines.
Browser checks verify existing administrator behavior. Restricted access is covered by HTTP, tool, MCP, and realtime tests.

```bash
dotnet build Wafish.slnx -c Release
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --group configuration-changes
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --group plugin-preview
python3 scripts/verify-packages.py
npm --prefix src/Wafish.ReactUi/ClientApp run test:e2e
```

The package gate reused freshly built packages after updating the new permission property's nullable API declaration.
No previously released declaration changed.

## Compatibility and limits

Existing hosts retain all permissions by default. Configured permission arrays replace that default.
Claims and custom operation rules can restrict users. These rules do not provide tenant or route ownership isolation.
Direct in-process callers and plugin code remain trusted.

Idempotency applies to the new complete-configuration workflow, not existing CRUD endpoints.
SQLite migration 022 reserves receipts atomically with configuration revisions.
Receipts last 24 hours and are limited to 10,000 unexpired entries.
Lost activation acknowledgements remain explicitly unknown; retries do not create another revision.
Configuration previews do not reserve revisions or change plugin bindings.
Plugin previews depend on cooperative, read-only health probes and lifecycle-compliant plugin constructors.

Verification ran on Linux. No package was published or pushed.
R19–R55 remain deferred: 37 tasks.
See [consumer workflow](../../../../docs/configuration-changes.md).
