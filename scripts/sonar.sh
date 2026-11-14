#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
: "${SONAR_HOST_URL:?Set SONAR_HOST_URL to the SonarQube HTTP endpoint.}"
: "${SONAR_TOKEN:?Set SONAR_TOKEN to a project analysis token.}"

dotnet tool restore
mkdir -p artifacts/sonar
rm -f artifacts/sonar/coverage.xml

dotnet sonarscanner begin \
  /k:"${SONAR_PROJECT_KEY:-wafish}" \
  /d:sonar.host.url="$SONAR_HOST_URL" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.cs.vscoveragexml.reportsPaths="artifacts/sonar/coverage.xml" \
  /d:sonar.javascript.lcov.reportPaths="src/Wafish.ReactUi/ClientApp/coverage/lcov.info" \
  /d:sonar.exclusions="**/bin/**,**/obj/**,**/node_modules/**,**/wwwroot/**,tests/**" \
  /d:sonar.issue.ignore.multicriteria="tailwindScope,tailwindDeclarations" \
  /d:sonar.issue.ignore.multicriteria.tailwindScope.ruleKey="css:S8776" \
  /d:sonar.issue.ignore.multicriteria.tailwindScope.resourceKey="src/Wafish.ReactUi/ClientApp/src/components/ui/shadcn.css" \
  /d:sonar.issue.ignore.multicriteria.tailwindDeclarations.ruleKey="css:S8767" \
  /d:sonar.issue.ignore.multicriteria.tailwindDeclarations.resourceKey="src/Wafish.ReactUi/ClientApp/src/components/ui/shadcn.css" \
  /d:sonar.qualitygate.wait=true \
  /d:sonar.qualitygate.timeout=300

dotnet build Wafish.slnx -c Release --no-incremental --disable-build-servers
npm --prefix src/Wafish.ReactUi/ClientApp exec -- playwright install chromium
dotnet coverage collect "bash scripts/sonar-regressions.sh" \
  -f xml -o artifacts/sonar/coverage.xml
test -s artifacts/sonar/coverage.xml
npm --prefix src/Wafish.ReactUi/ClientApp run test:coverage
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
