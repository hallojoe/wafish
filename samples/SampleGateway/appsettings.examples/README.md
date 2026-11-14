# Deployment configuration examples

This directory contains appsettings.json examples for five deployment scenarios.

## Scenarios

| File | Mode | Ports | Description |
|------|------|-------|-------------|
| `appsettings.AllInOne.json` | AllInOne | 5200 | Compatible single-port mode |
| `appsettings.Split.json` | Split | 80 + 5000 | One process: proxy on 80, local dashboard on 5000 |
| `appsettings.SplitWithHealth.json` | Split | 80 + 5000 + 5002 | Separate health-check endpoint |
| `appsettings.ProxyOnly.json` | ProxyOnly | 80 + 5001 | Proxy and local admin API, without dashboard UI |
| `appsettings.DashboardOnly.json` | DashboardOnly | 5000 | Dashboard control plane, without proxy forwarding |

## Use a configuration file

From the repository root, copy the required example to appsettings.json:

```bash
cp samples/SampleGateway/appsettings.examples/appsettings.Split.json samples/SampleGateway/appsettings.json
```

## Use command-line arguments

```bash
# Split mode
dotnet run --project samples/SampleGateway -- \
  --deployment split \
  --proxy-url http://0.0.0.0:80 \
  --dashboard-url http://127.0.0.1:5000

# Include health checks
dotnet run --project samples/SampleGateway -- \
  --deployment split \
  --proxy-url http://0.0.0.0:80 \
  --dashboard-url http://127.0.0.1:5000 \
  --health-url http://0.0.0.0:5002

# ProxyOnly mode
dotnet run --project samples/SampleGateway -- \
  --deployment proxyonly \
  --proxy-url http://0.0.0.0:80 \
  --admin-url http://127.0.0.1:5001
```

## Docker, systemd, and Windows services

The historical deployment templates moved to a separate repository.
These examples show their original usage; the template paths are not included here.
For the current container build, use [the sample Dockerfile](../Dockerfile).

```text
deploy/docker-compose.split.yml
deploy/systemd/wafish.service
deploy/windows/Install-WafishService.ps1
```

Docker Compose:

```bash
docker compose -f deploy/docker-compose.split.yml up -d --build
```

systemd:

```bash
sudo cp deploy/systemd/wafish.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now wafish
```

Windows service, from administrator PowerShell:

```powershell
.\deploy\windows\Install-WafishService.ps1 -AppDirectory C:\Services\Wafish
```

## Use environment variables with Docker or Kubernetes

```bash
export Gateway__Deployment__Mode=Split
export Kestrel__Endpoints__Proxy__Url=http://0.0.0.0:80
export Kestrel__Endpoints__Dashboard__Url=http://127.0.0.1:5000
```

## Health endpoints

After startup:

- `GET /live`: Process liveness; returns 200.
- `GET /ready`: Configuration readiness.
- `GET /health`: Combined health status.

```bash
curl http://localhost:5002/live
curl http://localhost:5002/ready
curl http://localhost:5002/health
```

## Configuration reload

Changes to appsettings.json reload after a 500ms debounce period.
Routes, clusters, policies, limits, and alerts apply automatically.
Kestrel port binding changes require a process restart.
