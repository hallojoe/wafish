# Sample identity and telemetry evidence

Completed priority 13 on 2026-11-14. Execution pauses here at the user's request.
Release solution build: zero warnings and errors. Main suite: all 230 cases pass.
The unchanged administration suite passed all 35 cases during priority 11.
Three isolated package consumers passed after the storage optimization.
Changed priority 13 production source coverage: 33/33 executable lines, 100%.

One SampleInstanceIdentity now supplies registration, all controller response identities, instance headers, and trace tags.
Configured identities remain stable. Unconfigured standalone processes receive unique identities.
The sample uses gRPC for per-instance registration, as its existing configuration specifies.
Ping, query echo, default echo, POST echo, and instance contracts remain distinguishable through the gateway.

The real Aspire check validates both registered backend instances before and after complete AppHost restart.
It verifies response headers against registered destination identities and preserves supplied W3C trace IDs.
Gateway, backend, and complete AppHost restarts retain credentials and stored marker route identity.
All owned AppHosts are stopped after verification.

Telemetry checks verify application and YARP activity sources plus ASP.NET Core, HTTP client, runtime, and YARP meters.
Default and custom health paths do not produce server spans.
Normal API requests, including `/health/reports`, retain traces.
An absent OTLP endpoint registers no OTLP exporter. Instrumentation and application behavior remain usable.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --sample-contracts
dotnet run --project tests/Wafish.Regression -c Release --no-build
python scripts/check-topology.py
```

Coverage combines the complete main suite and the real topology script.
Use `changed-coverage.py` with their merged dotnet-coverage XML.
Standalone registration settings and exporter-free behavior are documented in the sample READMEs.
The final documentation check found no broken local links in the 39 affected project and deployment Markdown files.
