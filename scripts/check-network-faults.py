#!/usr/bin/env python3
"""Run an isolated optional Aspire fault profile and verify real retry/circuit recovery."""
import base64
import importlib.util
import json
import pathlib
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('topology', pathlib.Path(__file__).with_name('check-topology.py'))
topology = importlib.util.module_from_spec(spec)
spec.loader.exec_module(topology)


def run():
    running = topology.document(topology.command('aspire', 'ps', '--format', 'Json', '--non-interactive'))
    if any(pathlib.Path(item.get('appHostPath', '')).resolve() == topology.APPHOST for item in running):
        raise RuntimeError('The target AppHost must be stopped before this isolated check.')
    identity = 'fault-' + uuid.uuid4().hex[:12]
    parameter = 'Parameters:' + identity + '-control-plane-password'
    secret_id = ET.parse(topology.APPHOST).findtext('.//UserSecretsId')
    secrets = pathlib.Path.home() / '.microsoft/usersecrets' / secret_id / 'secrets.json'
    owned = False
    with tempfile.TemporaryDirectory(prefix='wafish-faults-') as directory:
        try:
            owned = True
            topology.command('aspire', 'start', '--apphost', str(topology.APPHOST), '--non-interactive', '--format', 'Json', '--',
                             '--TopologyId=' + identity, '--GatewayDataDirectory=' + directory, '--EnableFaultInjection=true')
            for resource in ('gateway', 'backend-one', 'backend-two', 'fault-proxy'):
                topology.aspire('wait', resource)
            resources = topology.document(topology.aspire('describe', '--format', 'Json'))['resources']
            def origin(name):
                resource = next(item for item in resources if item['displayName'] == name)
                url = next(url['url'] for url in resource['urls'] if url['name'] == 'http')
                parts = urllib.parse.urlsplit(url)
                return urllib.parse.urlunsplit((parts.scheme, parts.netloc, '', '', ''))
            gateway, proxy = origin('gateway'), origin('fault-proxy')
            key = json.loads(secrets.read_text(encoding='utf-8-sig'))[parameter]
            authorization = 'Basic ' + base64.b64encode(('admin:' + key).encode()).decode()
            def request(origin, path, payload=None, method=None):
                headers = {'Authorization': authorization} if origin == gateway else {'X-Fault-Key': key}
                body = None if payload is None else json.dumps(payload).encode()
                if body is not None:
                    headers['Content-Type'] = 'application/json'
                req = urllib.request.Request(origin + path, body, headers, method=method or ('GET' if body is None else 'POST'))
                try:
                    with urllib.request.urlopen(req, timeout=15) as response:
                        data = response.read()
                        return response.status, json.loads(data) if data else None
                except urllib.error.HTTPError as error:
                    error.read()
                    return error.code, None
            def ok(origin, path, payload=None, method=None):
                status, data = request(origin, path, payload, method)
                assert 200 <= status < 300, f'Operation failed: {path} ({status})'
                return data
            def fault(mode, count=0, delay=0):
                return ok(proxy, '/__fault', {'mode': mode, 'count': count, 'delayMilliseconds': delay})
            def binding(plugin, scope, scope_id, config):
                return ok(gateway, '/wafish/api/plugin-bindings', {'pluginId': plugin, 'scope': scope, 'scopeId': scope_id,
                    'enabled': True, 'configJson': json.dumps(config)})
            ok(gateway, '/wafish/api/config/routes/fault-demo', {'clusterId': 'fault-demo', 'match': {'path': '/api/local-service/{**catch-all}'},
                'order': -100, 'destinations': {'proxy': proxy + '/'}}, 'PUT')
            path = '/api/local-service/instance'
            fault('reset', 1)
            status, _ = request(gateway, path)
            assert status in (502, 503, 504), 'Unretried reset unexpectedly succeeded.'
            baseline = ok(proxy, '/__fault')
            assert baseline['attempts'] == 1 and baseline['resets'] == 1, 'Baseline did not use one attempt.'
            binding('request-retry', 'Route', 'fault-demo', {'enabled': True, 'maxRetries': 1, 'backoffBaseMs': 10, 'backoffJitterMs': 0})
            initial = ok(proxy, '/__fault')['attempts']
            fault('reset', 1)
            assert ok(gateway, path)['instance'] == 'backend-one'
            retry = ok(proxy, '/__fault')
            assert retry['resets'] == baseline['resets'] + 1 and retry['attempts'] - initial == 2, 'Retry did not cross the fault proxy twice.'
            binding('circuit-breaker', 'Cluster', 'fault-demo', {'enabled': True, 'failureThreshold': 2,
                'minimumThroughput': 2, 'failureRatio': 0.5, 'samplingDurationSeconds': 30, 'recoveryTimeoutSeconds': 2})
            fault('reset', 100)
            for _ in range(4):
                status, _ = request(gateway, path)
                assert status in (502, 503, 504)
                first = ok(proxy, '/__fault')['attempts']
                request(gateway, path)
                if ok(proxy, '/__fault')['attempts'] == first:
                    break
            else:
                raise AssertionError('Circuit did not block failing traffic.')
            fault('none')
            blocked = ok(proxy, '/__fault')['attempts']
            status, _ = request(gateway, path)
            assert status == 503 and ok(proxy, '/__fault')['attempts'] == blocked, 'Open circuit forwarded traffic.'
            time.sleep(2.2)
            assert ok(gateway, path)['instance'] == 'backend-one', 'Recovery probe did not restore traffic.'
            assert ok(gateway, path)['instance'] == 'backend-one', 'Recovered circuit did not remain closed.'
            fault('delay', 1, 150)
            start = time.monotonic()
            assert ok(gateway, path)['instance'] == 'backend-one' and time.monotonic() - start >= .14
            print(json.dumps({'baselineFailed': True, 'retryAttempts': retry['attempts'] - initial, 'resetRecovered': True,
                'openCircuitBlocked': True, 'halfOpenRecovered': True, 'delayObserved': True}, indent=2))
        finally:
            if owned:
                topology.aspire('stop')
            topology.command('dotnet', 'user-secrets', 'remove', parameter, '--project', str(topology.APPHOST))


if __name__ == '__main__':
    run()
