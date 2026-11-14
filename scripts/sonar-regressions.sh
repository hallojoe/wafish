#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
dotnet run --project tests/WafIsh.Regression -c Release --no-build
dotnet run --project tests/WafIsh.Administration.Regression -c Release --no-build
UI_TEST_NO_BUILD=true npm --prefix src/WafIsh.ReactUi/ClientApp run test:e2e
