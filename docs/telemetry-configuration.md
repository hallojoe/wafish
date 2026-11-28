# Local telemetry configuration report

Enable the report when investigating sample telemetry setup:

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableTelemetryDiagnostics=true
aspire logs gateway --apphost Wafish.AppHost/Wafish.AppHost.csproj --search WAFISH_TELEMETRY_CONFIGURATION --non-interactive
```

The gateway and each backend write one `WAFISH_TELEMETRY_CONFIGURATION` JSON line during startup.
The optional second gateway and discovery catalog use the same switch.
The Aspire console log view also shows the line. The switch defaults to false.
No HTTP diagnostic endpoint is registered.

Standalone sample hosts can set `Sample:TelemetryDiagnostics=true` through normal host configuration.
The report requires Development or the existing `Sample:Orchestrated=true` sample profile.
Production hosts outside that explicit sample profile suppress the report, even when requested.

## Read the report

| Field | Meaning |
| --- | --- |
| `schema` | Report format version, currently `1`. |
| `scope` | `SampleServiceDefaults`, the registration code described by this report. |
| `state` | `startup-registration`; this is a startup snapshot. |
| `exporters` | `otlp` or `none` for logs, metrics, and traces. |
| `logging` | Whether formatted messages and scopes are included in OpenTelemetry logs. |
| `metrics` | Instrumentation and meter subscriptions registered by sample defaults. |
| `tracing` | Server, HTTP client, application, and YARP activity subscriptions. |
| `healthRequestTracing` | Exact configured health paths are excluded from server tracing. |

A nonempty `OTEL_EXPORTER_OTLP_ENDPOINT` selects OTLP registration in sample defaults.
Aspire normally supplies that endpoint. Without it, each exporter field is `none`.
Per-signal endpoint settings alone do not select an exporter in these defaults.
The existing [exporter-free behavior](../samples/SampleServiceDefaults/README.md#telemetry-without-a-collector) remains unchanged.

The report records the registration decision when service defaults run.
Later configuration edits do not rewrite that decision. Restart after changing telemetry settings.
It does not prove collector connectivity, successful export, sampling, or receipt of a particular signal.
Additional instrumentation, exporters, or provider overrides registered elsewhere are outside this report's scope.

## Redaction boundary

The report contains fixed labels and registration flags. It never serializes configuration values.
It omits collector URLs, headers, credentials, application names, resource attributes, probe paths, and payload data.
It writes directly to process console output, without sending the report through the OpenTelemetry logging provider.
Aspire and other process log collectors can retain console output.
This report does not change redaction or content capture settings for normal application telemetry.

## Verify locally

With the target AppHost stopped:

```bash
python3 scripts/check-telemetry-configuration.py
```

The check owns an isolated topology, temporary database directory, and generated credential.
It checks all five eligible resources with diagnostics enabled, then the default topology with diagnostics disabled.
It stops its resources and removes its generated credential and temporary data.

Focused checks cover exporter-free startup, environment restrictions, secret redaction, and stable registration snapshots:

```bash
dotnet run --project tests/Wafish.Regression -c Release -- --group telemetry-configuration
```

See [report implementation](../samples/SampleServiceDefaults/TelemetryConfigurationReport.cs) and [service registration](../samples/SampleServiceDefaults/Extensions.cs).
