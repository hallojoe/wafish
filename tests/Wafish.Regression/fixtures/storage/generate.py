"""Generate synthetic compatibility fixtures with the retired native library.
Usage: python3 generate.py /path/to/2.1.11/libe_sqlcipher.so
The public password below is test data, never a deployment credential.
"""
import ctypes
import hashlib
import json
import pathlib
import sys

library = pathlib.Path(sys.argv[1]).resolve()
root = pathlib.Path(__file__).parent
native = ctypes.CDLL(str(library))
native.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
native.sqlite3_key.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
native.sqlite3_close.argtypes = [ctypes.c_void_p]
callback_type = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_int,
                               ctypes.POINTER(ctypes.c_char_p), ctypes.POINTER(ctypes.c_char_p))
native.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, callback_type,
                               ctypes.c_void_p, ctypes.POINTER(ctypes.c_char_p)]
versions = {}
for name, password in [('plain.db', b''), ('sqlcipher4.db', b'compatibility-test-key')]:
    target = root / name
    if target.exists():
        raise SystemExit('Refusing to overwrite an existing fixture: ' + str(target))
    database = ctypes.c_void_p()
    assert native.sqlite3_open(str(target).encode(), ctypes.byref(database)) == 0
    try:
        if password:
            assert native.sqlite3_key(database, password, len(password)) == 0
        rows = []
        @callback_type
        def row(_, count, values, columns):
            rows.append([values[i].decode() if values[i] else None for i in range(count)])
            return 0
        error = ctypes.c_char_p()
        sql = b"PRAGMA cipher_version; SELECT sqlite_version(); CREATE TABLE compatibility_probe (value TEXT NOT NULL); INSERT INTO compatibility_probe VALUES ('legacy-data-retained'); PRAGMA user_version=73;"
        assert native.sqlite3_exec(database, sql, row, None, ctypes.byref(error)) == 0, error.value
        versions[name] = {'cipher': rows[0][0], 'sqlite': rows[1][0]}
    finally:
        assert native.sqlite3_close(database) == 0
    versions[name]['sha256'] = hashlib.sha256(target.read_bytes()).hexdigest()
versions['native_sha256'] = hashlib.sha256(library.read_bytes()).hexdigest()
(root / 'provenance.json').write_text(json.dumps(versions, indent=2) + '\n')
print(json.dumps(versions, indent=2))
