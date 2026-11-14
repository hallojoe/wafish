#!/usr/bin/env bash
set -euo pipefail
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll
dotnet tests/Wafish.Administration.Regression/bin/Release/net10.0/Wafish.Administration.Regression.dll
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --storage
dotnet tests/Wafish.Regression/bin/Release/net10.0/Wafish.Regression.dll --redis
