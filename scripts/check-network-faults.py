#!/usr/bin/env python3
"""Exercise an owned local Aspire fault scenario, then verify the default resource graph."""
import base64
import http.client
import json
import pathlib
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
APPHOST = ROOT / 'Wafish.AppHost/Wafish.AppHost.csproj'


def command(*args):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, timeout=180)
    if result.returncode:
        # Aspire output can contain dashboard tokens. Never print it here.
        raise RuntimeError(f'{args[0]} {args[1]} failed (exit {result.returncode}).')
    return result.stdout


def document(text):
    decoder = json.JSONDecoder()
    for index, character in enumerate(text):
        if character in '[{':
            try:
                return decoder.raw_decode(text[index:])[0]
            except ValueError:
                pass
    raise RuntimeError('Aspire did not return JSON.')


def aspire(*args):
    return command('aspire', *args, '--apphost', str(APPHOST), '--non-interactive')


def until(check):
    deadline = time.monotonic() + 10
    while not check():
        if time.monotonic() > deadline:
            raise AssertionError('Scenario did not reach its expected state.')
        time.sleep(0.05)


def main():
    running = document(command('aspire', 'ps', '--format', 'Json', '--non-interactive'))
    if any(pathlib.Path(item.get('appHostPath', '')).resolve() == APPHOST for item in running):
        raise RuntimeError('The target AppHost is already running. This check requires an unused target.')
    topology = 'fault-' + uuid.uuid4().hex[:12]
    parameter = 'Parameters:' + topology + '-control-plane-password'
    secret_id = ET.parse(APPHOST).findtext('.//UserSecretsId')
    secrets = pathlib.Path.home() / '.microsoft/usersecrets' / secret_id / 'secrets.json'
    owned = False
    with tempfile.TemporaryDirectory(prefix='wafish-network-faults-') as directory:
        def start(enabled):
            nonlocal owned
            owned = True
            command('aspire', 'start', '--apphost', str(APPHOST), '--non-interactive', '--format', 'Json', '--',
                    '--TopologyId=' + topology, '--GatewayDataDirectory=' + directory,
                    '--EnableNetworkFaults=' + str(enabled).lower(), '--EnableRedis=false', '--EnableSecondGateway=false',
                    '--EnableExternalLogin=false', '--EnableSonarQube=false', '--EnableServiceDiscovery=false')
            names = ['gateway', 'backend-one', 'backend-two'] + (['fault-backend'] if enabled else [])
            for name in names:
                aspire('wait', name)
            return document(aspire('describe', '--format', 'Json'))['resources']

        def origin(resources, name, path):
            resource = next(item for item in resources if item['displayName'] == name)
            url = next(item['url'] for item in resource['urls'] if urllib.parse.urlsplit(item['url']).path == path)
            parsed = urllib.parse.urlsplit(url)
            assert parsed.scheme == 'http' and parsed.hostname in ('localhost', '127.0.0.1', '::1'), 'Expected direct loopback HTTP.'
            return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, '', '', ''))

        try:
            resources = start(True)
            gateway = origin(resources, 'gateway', '/console')
            backend = origin(resources, 'fault-backend', '/benchmark/manifest')
            password = json.loads(secrets.read_text(encoding='utf-8-sig'))[parameter]
            authorization = 'Basic ' + base64.b64encode(('admin:' + password).encode()).decode()

            def request(address, path, payload=None, method='GET', admin=False):
                headers = {'Authorization': authorization} if admin else {}
                if payload is not None:
                    headers['Content-Type'] = 'application/json'
                req = urllib.request.Request(address + path, data=None if payload is None else json.dumps(payload).encode(), headers=headers, method=method)
                try:
                    response = urllib.request.urlopen(req, timeout=8)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    return response.status, response.read(), response.headers

            def counters():
                status, body, _ = request(backend, '/api/perf/control/counters')
                assert status == 200
                return json.loads(body)

            def reset():
                until(lambda: counters()['active'] == 0)
                assert request(backend, '/api/perf/control/reset', method='POST')[0] == 204

            def mutate(path, value, method='PUT'):
                status, body, _ = request(gateway, '/wafish/api/' + path, value, method, admin=True)
                assert status in (200, 201), f'Scenario mutation failed: {path} ({status}).'
                result = json.loads(body)
                assert result.get('success', True), f'Scenario activation failed: {path}.'
                if 'activeVersion' in result:
                    assert result['activeVersion'] == result['persistedVersion'], 'Scenario configuration did not activate.'

            def binding(scope, target, plugin, config):
                mutate('plugin-bindings', {'pluginId': plugin, 'scope': scope, 'scopeId': target, 'enabled': True,
                                          'schemaVersion': 1, 'configJson': json.dumps(config)}, method='POST')

            status, body, _ = request(backend, '/benchmark/manifest')
            assert status == 200 and b'"transportFaults":true' in body, 'Transport faults were not enabled.'
            for name in ('retry', 'circuit', 'stall', 'truncate'):
                cluster = 'fault-demo-' + name
                mutate('config/clusters/' + cluster, {'destinations': {'one': {'address': backend + '/'}}})
                target = {'retry': '/api/perf/flaky/2', 'circuit': '/api/perf/flaky/2',
                          'stall': '/api/perf/fault/stall/30000', 'truncate': '/api/perf/fault/truncate/1024'}[name]
                mutate('config/routes/' + cluster, {'clusterId': cluster, 'match': {'path': '/fault-demo/' + name},
                                                    'transforms': [{'PathSet': target}]})
            binding('Route', 'fault-demo-retry', 'request-retry', {'enabled': True, 'maxRetries': 2, 'backoffBaseMs': 0,
                    'backoffJitterMs': 0, 'timeoutSeconds': 2, 'totalTimeoutSeconds': 5})
            binding('Route', 'fault-demo-stall', 'request-retry', {'enabled': True, 'maxRetries': 2,
                    'timeoutSeconds': 1, 'totalTimeoutSeconds': 1})
            binding('Cluster', 'fault-demo-circuit', 'circuit-breaker', {'enabled': True, 'failureThreshold': 2,
                    'minimumThroughput': 2, 'failureRatio': 1, 'recoveryTimeoutSeconds': 2, 'recoveryJitterSeconds': 0,
                    'halfOpenMaxAttempts': 1, 'samplingDurationSeconds': 30})
            reset()
            status, body, headers = request(gateway, '/fault-demo/retry')
            assert status == 200 and body == b'OK' and headers.get('X-Retry-Count') == '2', 'Transient failures did not recover through retries.'
            assert counters()['flakyAttempts'] == 3, 'Retry allowance changed.'
            print('PASS: two transient failures recover on the third attempt.')
            reset()
            assert request(gateway, '/fault-demo/circuit')[0] == 503
            assert request(gateway, '/fault-demo/circuit')[0] == 503
            assert request(gateway, '/fault-demo/circuit')[0] == 503
            assert counters()['flakyAttempts'] == 2, 'Open circuit forwarded another attempt.'
            until(lambda: request(gateway, '/fault-demo/circuit')[0] == 200)
            assert counters()['flakyAttempts'] == 3, 'Recovery probe did not close the circuit.'
            assert request(gateway, '/fault-demo/circuit')[0] == 200
            print('PASS: open circuit blocks traffic, then a successful probe restores forwarding.')
            for name in ('stall', 'truncate'):
                reset()
                started = time.monotonic()
                try:
                    status, _, _ = request(gateway, '/fault-demo/' + name)
                    assert status in (502, 504), 'Fault was reported as a complete successful response.'
                except (http.client.IncompleteRead, http.client.RemoteDisconnected, ConnectionResetError):
                    pass
                assert time.monotonic() - started < 8, 'Fault handling exceeded its bound.'
                until(lambda: counters()['active'] == 0)
                assert counters()['total'] == 1, 'Transport fault unexpectedly replayed work.'
                if name == 'stall':
                    assert counters()['cancelled'] == 1, 'Deadline did not cancel the stalled producer.'
                else:
                    assert counters()['aborted'] == 1, 'Truncation did not abort the transport.'
                assert request(backend, '/api/perf/plain')[:2] == (200, b'OK'), 'Fixture did not recover after the fault.'
                print('PASS: ' + name + ' interrupts traffic, drains work, and permits subsequent healthy requests.')
            for name in ('retry', 'circuit', 'stall', 'truncate'):
                for kind in ('routes', 'clusters'):
                    status, _, _ = request(gateway, '/wafish/api/config/' + kind + '/fault-demo-' + name, method='DELETE', admin=True)
                    assert status == 200, 'Scenario configuration cleanup failed.'
            aspire('stop')
            owned = False
            defaults = start(False)
            assert not any(item['displayName'] == 'fault-backend' for item in defaults), 'Fault resource leaked into the default topology.'
            print('PASS: disabling the profile restores the default three-service topology.')
        finally:
            if owned:
                aspire('stop')
            command('dotnet', 'user-secrets', 'remove', parameter, '--project', str(APPHOST))


if __name__ == '__main__':
    main()
