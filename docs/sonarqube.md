# SonarQube analysis

The optional Aspire resources run SonarQube Community Build with a dedicated PostgreSQL database.
The gateway does not depend on SonarQube. The default topology still requires no containers.

## Start the server

Install .NET 10, Node.js 24, the Aspire CLI, and Docker or Podman.
Check the Linux container host before startup:

```bash
sysctl vm.max_map_count fs.file-max
ulimit -n
ulimit -u
```

Minimum values are `vm.max_map_count=524288`, `fs.file-max=131072`, `nofile=131072`, and `nproc=8192`.
The container runtime must also meet these limits. Shell limits do not configure its service.
See [SonarSource host requirements](https://docs.sonarsource.com/sonarqube-community-build/server-installation/pre-installation/linux).

Run from the repository root:

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableSonarQube=true
aspire wait sonarqube --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
aspire describe --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

For Podman, prefix the start command with `ASPIRE_CONTAINER_RUNTIME=podman`.
Use the reported SonarQube HTTP URL. Aspire assigns the host port to avoid conflicts with other installations.
The readiness check requires `/api/system/status` to report `UP`. An HTTP 200 response alone is insufficient.

Open that URL. Sign in with `admin` / `admin`, then change the password.
Create a local project with key `wafish` and generate a project analysis token.

## Run analysis

Use Bash, including Git Bash or WSL on Windows:

```bash
export SONAR_HOST_URL='http://localhost:PORT'
read -rsp 'SonarQube project token: ' SONAR_TOKEN
echo
export SONAR_TOKEN
bash scripts/sonar.sh
unset SONAR_TOKEN
```

Replace `PORT` with the reported host port. Set `SONAR_PROJECT_KEY` if the project uses another key.
Do not run multiple scans concurrently in one checkout.

The script restores pinned tools from `.config/dotnet-tools.json` and starts analysis.
It builds the solution and collects coverage from both executable .NET regression suites.
Browser tests also run inside coverage collection, covering real administration requests.
The script installs Playwright Chromium in its local cache. Linux hosts also need its browser system dependencies.
It also runs frontend tests with V8 coverage, then uploads both reports.
The .NET suites use `dotnet run`, not `dotnet test`.
Browser tests start isolated test hosts on ports 5219–5221. Keep these ports free.
Failures stop the script. A failed quality gate or a 300-second gate timeout returns a failing exit code.
After a failed build or test, fix the failure and rerun the complete script.

The .NET report is `artifacts/sonar/coverage.xml`.
The frontend report is `src/Wafish.ReactUi/ClientApp/coverage/lcov.info`.
Generated frontend assets and test harnesses are excluded from source analysis.
Source TypeScript remains eligible for analysis.

Frontend tests enforce these minimums for the shared API client:
90% statements, lines, and functions; 80% branches.
Run `npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage` to check them locally.
Other frontend files remain in the report, including uncovered files.

Two CSS rules are excluded only for `src/components/ui/shadcn.css`.
Tailwind directives use declarations and scopes that these standard-CSS rules reject.
Other CSS rules still apply. See [Tailwind directives](https://tailwindcss.com/docs/functions-and-directives).

Targeted C# suppressions state their reasons beside the affected code.
They cover fixed SQL structure with bound values, RFC 6238 TOTP compatibility,
statistical log sampling, bounded pointer APIs, and public API compatibility.
They do not exclude whole source files from analysis.
The scanner passes the token as an argument because its .NET interface requires `sonar.token`.
Do not enable shell command tracing. Scanner output and coverage artifacts are ignored by Git.

See [SonarSource .NET coverage guidance](https://docs.sonarsource.com/sonarqube-community-build/analyzing-source-code/test-coverage/dotnet-test-coverage).

## Continuous integration

The `SonarQube` workflow runs on default-branch pushes or manual dispatch from that branch.
It remains inactive until the repository variable `SONAR_HOST_URL` is configured.

Configure these GitHub repository settings:

| Setting | Type | Value |
| --- | --- | --- |
| `SONAR_HOST_URL` | Variable | SonarQube HTTPS URL reachable from GitHub-hosted runners |
| `SONAR_PROJECT_KEY` | Variable | Optional project key; defaults to `wafish` |
| `SONAR_TOKEN` | Secret | Project analysis token |

A local Aspire URL is not reachable from GitHub-hosted runners.
Use a reachable server for CI. The workflow does not expose your local server or provision remote infrastructure.
Community Build analysis uses the main branch. Pull requests continue through the existing `Verify` workflow.
Quality-gate failures fail the analysis job. This post-push check does not itself prevent merges.
The workflow uploads coverage but never uploads the scanner working directory.

## Data and shutdown

Aspire generates `<TopologyId>-sonar-db-password` and persists it in the AppHost user-secrets store.
Retain this secret while retaining the PostgreSQL volume.
Changing the parameter does not change the password inside an existing database.

The named volumes are `wafish-<TopologyId>-sonar-postgres-data`, `wafish-<TopologyId>-sonarqube-data`,
`wafish-<TopologyId>-sonarqube-extensions`, and `wafish-<TopologyId>-sonarqube-logs`.
`UseLegacyTopologyState=true` retains the previous names without the topology identifier.
They are separate from Besitos volumes and survive normal shutdown.
Do not run multiple Wafish SonarQube instances against these volumes.

```bash
aspire stop --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

Do not use `--force` for normal shutdown.
