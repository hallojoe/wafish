#!/usr/bin/env python3
"""Demonstrate health-checked blue-green cutover on an isolated SampleGateway."""
import copy
import http.server
import json
import os
import pathlib
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]


def request(url, payload=None, key=None, receipt=None):
    headers = {}
    if key:
        headers['X-Api-Key'] = key
    if receipt:
        headers['Idempotency-Key'] = receipt
    if payload is not None:
        headers['Content-Type'] = 'application/json'
    req = urllib.request.Request(url, None if payload is None else json.dumps(payload).encode(), headers)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)


class Backend(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        healthy = self.server.healthy or self.path != '/health'
        body = json.dumps({'version': self.server.version, 'healthy': healthy}).encode()
        self.send_response(200 if healthy else 503)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


def run():
    servers = []
    process = None
    with tempfile.TemporaryDirectory(prefix='wafish-blue-green-') as directory:
        log = open(pathlib.Path(directory) / 'gateway.log', 'w+')
        try:
            for version in ('blue', 'green'):
                server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
                server.version, server.healthy = version, version == 'blue'
                threading.Thread(target=server.serve_forever, daemon=True).start()
                servers.append(server)
            addresses = {s.version: f'http://127.0.0.1:{s.server_port}' for s in servers}
            # Reserve distinct ports until all choices are complete.
            with socket.socket() as gateway, socket.socket() as grpc:
                gateway.bind(('127.0.0.1', 0)); grpc.bind(('127.0.0.1', 0))
                port, grpc_port = gateway.getsockname()[1], grpc.getsockname()[1]
            origin = f'http://127.0.0.1:{port}'
            api = origin + '/wafish/api/config/changes'
            key = secrets.token_urlsafe(48)
            settings = {
                'ASPNETCORE_ENVIRONMENT': 'Production', 'ASPNETCORE_AUTO_BROWSER': '0',
                'Kestrel__Endpoints__Gateway__Url': origin, 'Kestrel__Endpoints__Gateway__Protocols': 'Http1',
                'Kestrel__Endpoints__Grpc__Url': f'http://127.0.0.1:{grpc_port}',
                'Kestrel__Endpoints__Grpc__Protocols': 'Http2',
                'Gateway__Dashboard__Auth__AuthMode': 'ApiKey', 'Gateway__Dashboard__Auth__ApiKey': key,
                'Gateway__Storage__Sqlite__ConnectionString': f'Data Source={directory}/gateway.db;Pooling=False',
                'Gateway__Registration__Enabled': 'false',
            }
            process = subprocess.Popen(['dotnet', str(ROOT / 'samples/SampleGateway/bin/Release/net10.0/SampleGateway.dll')],
                cwd=ROOT / 'samples/SampleGateway', env=dict(os.environ, **settings), stdout=log, stderr=log)
            deadline = time.monotonic() + 45
            while True:
                try:
                    # The preview returns the current persisted version, even for a stale candidate.
                    status, state = request(api + '/preview', {'expectedVersion': -1, 'routes': [], 'clusters': []}, key)
                    if status == 409:
                        break
                except (urllib.error.URLError, TimeoutError):
                    pass
                if time.monotonic() >= deadline or process.poll() is not None:
                    raise RuntimeError('Private gateway did not become ready.')
                time.sleep(.1)
            candidate = {
                'expectedVersion': state['persistedVersion'],
                'routes': [{'routeId': 'blue-green', 'clusterId': 'blue', 'match': {'path': '/deployment'}}],
                'clusters': [{'clusterId': v, 'destinations': {'primary': {'address': a + '/'}}} for v, a in addresses.items()],
            }
            checks = []

            def activate(version):
                nonlocal candidate
                status, health = request(addresses[version] + '/health')
                if status != 200 or health['version'] != version or not health['healthy']:
                    return False
                proposed = copy.deepcopy(candidate)
                proposed['routes'][0]['clusterId'] = version
                status, preview = request(api + '/preview', proposed, key)
                assert status == 200 and preview['valid'], (status, preview)
                status, result = request(api, proposed, key, secrets.token_hex(16))
                assert status == 200, (status, result)
                candidate = proposed
                candidate['expectedVersion'] = result['persistedVersion']
                deadline = time.monotonic() + 10
                while True:
                    status, response = request(origin + '/deployment')
                    if status == 200 and response['version'] == version:
                        break
                    assert time.monotonic() < deadline, 'Cutover did not become visible.'
                    time.sleep(.05)
                status, exported = request(origin + '/wafish/api/config/export', key=key)
                assert status == 200
                routes = exported['data']['ReverseProxy']['Routes']
                assert set(routes) == {'blue-green'}, 'Route identity changed.'
                assert routes['blue-green'].get('ClusterId', routes['blue-green'].get('clusterId')) == version
                checks.append({'stage': version, 'routeId': 'blue-green', 'persistedVersion': candidate['expectedVersion']})
                return True

            assert activate('blue')
            original = copy.deepcopy(candidate)
            assert not activate('green'), 'Unhealthy green was activated.'
            assert candidate == original
            assert request(origin + '/deployment')[1]['version'] == 'blue'
            checks.append({'stage': 'unhealthy-green-rejected'})
            servers[1].healthy = True
            assert activate('green')
            status, _ = request(api, original, key, secrets.token_hex(16))
            assert status == 409, 'Stale writer replaced the cutover.'
            checks.append({'stage': 'stale-update-rejected'})
            assert activate('blue')
            print(json.dumps({'result': 'passed', 'checks': checks}, indent=2))
        finally:
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=5)
            for server in servers:
                server.shutdown(); server.server_close()
            log.close()


if __name__ == '__main__':
    run()
