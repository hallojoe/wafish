# Aspire dashboard browser links

Verified: 2026-11-14.

## Failure

All three sample resources were healthy. Their displayed root URLs returned HTTP 404.
The gateway also displayed its HTTP/2-only gRPC endpoint as a browser link.
A browser request to that endpoint returned HTTP 400.
The gateway console and backend instance routes already returned HTTP 200.

## Change

- Link gateway HTTP endpoints to `/console` with the label `Gateway console`.
- Link backend HTTP endpoints to `/api/local-service/instance` with the label `Backend instance`.
- Show gRPC only in resource details, labelled for HTTP/2 clients.
- Preserve endpoint references, dynamic ports, registration, and health checks.
- Extract the HTTP origin from dashboard URLs in the topology verification script.

## Verification

- `dotnet build Wafish.AppHost/Wafish.AppHost.csproj -c Release`: zero warnings and errors.
- `dotnet build Wafish.slnx -c Release`: zero warnings and errors.
- `python3 scripts/check-topology.py`: resource and AppHost restart checks pass.
- Browser links return HTTP 200 before and after AppHost restart.
- Final live checks confirm one browser link per resource and a details-only gRPC URL.
- Backend replies identify the corresponding resource.
- Proxied endpoint contracts, W3C trace IDs, credentials, and persisted route identities remain correct.

The topology check uses isolated data and removes only its own parameter and temporary directory.
The original default topology was restarted after verification.
