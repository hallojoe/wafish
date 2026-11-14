# Legacy storage fixtures

These databases contain synthetic test data only.
They were created using `SQLitePCLRaw.lib.e_sqlcipher` 2.1.11, SQLCipher 4.5.2, and SQLite 3.39.2.

- `plain.db`: ordinary SQLite, without a key.
- `sqlcipher4.db`: default SQLCipher 4 format, with the public test password `compatibility-test-key`.
- `provenance.json`: native library version and SHA-256 hashes.

The regression suite copies each fixture before opening it with the production storage factory.
It applies migrations, checks existing values, writes additional values, reopens, and verifies integrity and encryption state.
The retired native library is not a project dependency or deployment artifact.

To regenerate fixtures, copy `generate.py` into an empty directory and pass the original library path.
The script refuses to overwrite existing files.
Never use these public test credentials for real data.
