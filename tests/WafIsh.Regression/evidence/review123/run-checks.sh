#!/usr/bin/env bash
set -euo pipefail
dotnet tests/WafIsh.Regression/bin/Release/net10.0/WafIsh.Regression.dll
dotnet tests/WafIsh.Administration.Regression/bin/Release/net10.0/WafIsh.Administration.Regression.dll
