#!/usr/bin/env python3
"""Verify the external runner exits after headers when a response body never completes."""
import http.server
import subprocess
import threading
import time

class Stalled(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Length', '100')
        self.end_headers()
        self.wfile.flush()
        stopped.wait(30)
    def log_message(self, *args):
        pass

stopped = threading.Event()
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Stalled)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
started = time.monotonic()
try:
    result = subprocess.run(['dotnet', 'tests/WafIsh.Performance/Runner/bin/Release/net10.0/Runner.dll',
        '--external-url', f'http://127.0.0.1:{server.server_port}', '--path', '/',
        '--duration', '1', '--warmup', '0', '--concurrency', '1', '--repetitions', '1'],
        timeout=20, capture_output=True, text=True)
    elapsed = time.monotonic() - started
    assert result.returncode == 1, result.stdout + result.stderr
    assert elapsed < 15, elapsed
    print(f'PASS: stalled body exits with code 1 after {elapsed:.2f}s')
finally:
    stopped.set()
    server.shutdown()
    server.server_close()
