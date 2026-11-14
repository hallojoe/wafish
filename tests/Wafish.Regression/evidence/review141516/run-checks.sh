#!/usr/bin/env bash
set -euo pipefail
export WAFISH_TEST_REPORT=/tmp/wafish141516-main.json
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
export WAFISH_TEST_REPORT=/tmp/wafish141516-administration.json
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
export WAFISH_TEST_REPORT=/tmp/wafish141516-storage.json
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --storage
