#!/usr/bin/env bash
set -euo pipefail
export WAFISH_TEST_REPORT=/tmp/wafish141516-main.json
dotnet tests/WafIsh.Regression/bin/Release/net10.0/WafIsh.Regression.dll
export WAFISH_TEST_REPORT=/tmp/wafish141516-administration.json
dotnet tests/WafIsh.Administration.Regression/bin/Release/net10.0/WafIsh.Administration.Regression.dll
export WAFISH_TEST_REPORT=/tmp/wafish141516-storage.json
dotnet tests/WafIsh.Regression/bin/Release/net10.0/WafIsh.Regression.dll --storage
