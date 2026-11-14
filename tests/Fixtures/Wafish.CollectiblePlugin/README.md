# Collectible plugin regression fixture

This assembly contains asynchronous and synchronous plugin cleanup fixtures.
The main regression runner builds this project without referencing its output assembly.
Tests copy its DLL into private plugin directories and load it through collectible contexts.
Do not reference fixture types from the runner. Such references can prevent collection.

Build through `dotnet build tests/Wafish.Regression -c Release`.
Run `dotnet run --project tests/Wafish.Regression -c Release --no-build -- --lifetime-stress`.
