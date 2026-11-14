# Sample deployment evidence

Completed priority 12 on 2026-11-14. Changes remain uncommitted.
Release solution build: zero warnings and errors. Three deployment regression cases pass.
The previous complete suite passed 225 cases and 35 administration cases.
Final combined verification follows with priority 13.

The controlled Aspire script passes gateway, backend-one, backend-two, and complete AppHost restarts.
Both registered instances recover. Generated credentials and the stored marker route identity remain unchanged.
Topology checks verify separate directories, parameter names, volume names, exclusive leases, and explicit legacy compatibility.
Optional Redis and SonarQube model construction passes `aspire publish --list-steps` without starting containers or publishing resources.

Sonar readiness cases cover UP, startup, DOWN, migration-needed, missing/numeric status, non-object JSON, malformed JSON,
HTTP failure, unavailable transport, timeout, and missing endpoint. Only UP is healthy.

Standalone and non-root Podman container checks pass with a custom HTTPS certificate and UI prefix.
They verify trust validation, readiness, anonymous rejection, login, secure HttpOnly cookies, authenticated APIs,
proxy forwarding, and the effective startup operator URL.
The first image build exposed missing Node.js in the SDK stage. The Dockerfile now supplies it for asset verification.
Node.js remains absent from the final runtime stage.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --sample-deployment
python scripts/check-topology.py
python scripts/check-sample-https.py
podman build -f samples/SampleGateway/Dockerfile -t localhost/wafish:smoke .
python scripts/check-sample-https.py --image localhost/wafish:smoke
aspire publish --list-steps --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableSonarQube=true --EnableRedis=true --TopologyId=model-check --GatewayDataDirectory=/tmp/wafish-model-check
```

Coverage combines the deployment cases, standalone HTTPS recipe, real topology restart script, and optional model construction.
See `coverage.txt` and `changed-lines.json` for source-line counts. Browser-launch code remains intentionally unexecuted in automation.
Docker socket access was unavailable. The installed rootless Podman runtime completed the image checks.
No existing containers, volumes, databases, or AppHosts were removed.

Final combined verification passed all 230 cases: [priority 13 evidence](../sample-contracts/README.md).
Changed production coverage for priority 12 is 58/63 executable lines, 92.06%.
