#!/usr/bin/env python3
"""Run an isolated optional Aspire discovery profile and verify live refresh, stale rejection, and recovery."""
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
    identity = 'discovery-' + uuid.uuid4().hex[:12]
    parameter = 'Parameters:' + identity + '-control-plane-password'
    secret_id = ET.parse(topology.APPHOST).findtext('.//UserSecretsId')
    secrets = pathlib.Path.home() / '.microsoft/usersecrets' / secret_id / 'secrets.json'
    owned = False
    with tempfile.TemporaryDirectory(prefix='wafish-discovery-') as directory:
        try:
            owned = True
            topology.command('aspire', 'start', '--apphost', str(topology.APPHOST), '--non-interactive', '--format', 'Json', '--',
                             '--TopologyId=' + identity, '--GatewayDataDirectory=' + directory, '--EnableDiscoveryDemo=true')
            for resource in ('gateway', 'backend-one', 'backend-two', 'discovery-catalog'):
                topology.aspire('wait', resource)
            resources = topology.document(topology.aspire('describe', '--format', 'Json'))['resources']
            def origin(name):
                resource = next(item for item in resources if item['displayName'] == name)
                url = next(url['url'] for url in resource['urls'] if url['name'] == 'http')
                parts = urllib.parse.urlsplit(url)
                return urllib.parse.urlunsplit((parts.scheme, parts.netloc, '', '', ''))
            gateway, catalog = origin('gateway'), origin('discovery-catalog')
            key = json.loads(secrets.read_text(encoding='utf-8-sig'))[parameter]
            authorization = 'Basic ' + base64.b64encode(('admin:' + key).encode()).decode()
            def request(origin, path, payload=None, method=None):
                headers = {'Authorization': authorization} if origin == gateway else {'X-Catalog-Key': key}
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
            def select(instances, unavailable=False):
                return ok(catalog, '/__catalog', {'instances': instances, 'unavailable': unavailable})
            def until(check, label, seconds=50):
                deadline = time.monotonic() + seconds
                while True:
                    result = check()
                    if result:
                        return result
                    if time.monotonic() >= deadline:
                        raise AssertionError('Timed out: ' + label)
                    time.sleep(.25)
            def destinations():
                clusters = ok(gateway, '/wafish/api/clusters')['data']
                cluster = next(item for item in clusters if item['clusterId'] == 'discovery-demo')
                return {item['address']: item['name'] for item in cluster['destinations']}
            def statistics():
                return ok(gateway, '/wafish/api/plugin-resources/service-discovery')['customStatistics']
            one, two = origin('backend-one') + '/', origin('backend-two') + '/'
            ok(gateway, '/wafish/api/config/routes/discovery-demo', {'clusterId': 'discovery-demo',
                'match': {'path': '/api/local-service/{**catch-all}'}, 'order': -100,
                'destinations': {'initial': one}}, 'PUT')
            ok(gateway, '/wafish/api/plugin-bindings', {'pluginId': 'service-discovery', 'scope': 'Cluster',
                'scopeId': 'discovery-demo', 'enabled': True, 'configJson': json.dumps({'enabled': True,
                    'mode': 'HttpJson', 'endpoint': catalog + '/catalog', 'refreshSeconds': 5,
                    'requestTimeoutSeconds': 2, 'maxStaleSeconds': 10, 'stalePolicy': 'RejectNewTraffic'})})
            first = until(lambda: d if set(d := destinations()) == {one} and d[one].startswith('discovery-') else None, 'initial discovery')
            path = '/api/local-service/instance'
            assert ok(gateway, path)['instance'] == 'backend-one'
            select(['backend-two', 'backend-one'])
            both = until(lambda: d if set(d := destinations()) == {one, two} else None, 'endpoint addition')
            assert both[one] == first[one], 'Adding a peer changed destination identity.'
            select(['backend-two'])
            removed = until(lambda: d if set(d := destinations()) == {two} else None, 'endpoint removal')
            assert removed[two] == both[two] and ok(gateway, path)['instance'] == 'backend-two'
            select(['backend-two'], True)
            assert ok(gateway, path)['instance'] == 'backend-two', 'Last-known endpoint was not retained before expiry.'
            until(lambda: statistics().get('failedBindings', 0) == 1, 'visible refresh failure')
            until(lambda: request(gateway, path)[0] == 503, 'stale traffic rejection')
            until(lambda: statistics().get('rejectedBindings', 0) == 1, 'visible stale rejection')
            select(['backend-one', 'backend-two'])
            restored = until(lambda: d if set(d := destinations()) == {one, two} else None, 'catalog recovery')
            assert restored == both, 'Recovery changed stable destination identities.'
            until(lambda: statistics().get('failedBindings', -1) == 0 and statistics().get('rejectedBindings', -1) == 0, 'health recovery')
            assert ok(gateway, path)['instance'] in ('backend-one', 'backend-two')
            print(json.dumps({'addition': True, 'removal': True, 'stableIdentities': True,
                'refreshFailureVisible': True, 'staleTrafficRejected': True, 'recovery': True}, indent=2))
        finally:
            if owned:
                topology.aspire('stop')
            topology.command('dotnet', 'user-secrets', 'remove', parameter, '--project', str(topology.APPHOST))


if __name__ == '__main__':
    run()
