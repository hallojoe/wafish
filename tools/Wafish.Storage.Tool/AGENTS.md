# Storage tool instructions

- Keep source backups unchanged. Perform all database operations on a private copy.
- Accept encryption keys through the environment, never command arguments or logs.
- Keep error output free of database content and credentials.
- Preserve cancellation and remove temporary files after failure.
- Build with `dotnet build tools/Wafish.Storage.Tool -c Release`.
- Run `dotnet run --project tests/Wafish.Regression -c Release -- --storage-reliability` after relevant changes.
- Maintain at least 80% changed production-line coverage.
- Update README.md when command behavior changes.
