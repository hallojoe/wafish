# Wafish storage tool

Inspect standalone SQLite gateway backups without changing the original file.
The tool creates a private copy and removes it after completion or failure.

```bash
dotnet run --project tools/Wafish.Storage.Tool -c Release -- inspect /path/to/backup.db
dotnet run --project tools/Wafish.Storage.Tool -c Release -- verify /path/to/backup.db
```

Set `WAFISH_BACKUP_PASSWORD` in the process environment for encrypted backups.
Do not place keys in command arguments or committed files.

`inspect` checks integrity and reports applied and pending migrations.
`verify` also applies pending migrations to the private copy and reads configuration records and JSON fields.
Results contain format, migration IDs, counts, and safe error identifiers. They contain no keys or configuration values.

Exit codes: 0 for success, 1 for failed verification, 2 for invalid arguments, and 130 for cancellation.
Use a closed, standalone backup. Files with WAL or rollback-journal companions are rejected.
Export a consistent backup before inspecting a database that is in use.
Unknown migration IDs are rejected. Plaintext and SQLCipher-compatible encrypted backups are supported.

Build: `dotnet build tools/Wafish.Storage.Tool -c Release`.
Verification: `dotnet run --project tests/Wafish.Regression -c Release -- --storage-reliability`.
