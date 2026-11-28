#!/usr/bin/env python3
"""Verify an owned HTTP JSON discovery profile and restore the default resource graph."""
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
    deadline = time.monotonic() + 60
    while not check():
        if time.monotonic() > deadline:
            raise AssertionError('Scenario did not reach its expected state.')
        time.sleep(0.2)


def main():
    running = document(command('aspire', 'ps', '--format', 'Json', '--non-interactive'))
    if any(pathlib.Path(item.get('appHostPath', '')).resolve() == APPHOST for item in running):
        raise RuntimeError('The target AppHost is already running. This check requires an unused target.')
    topology = 'discovery-' + uuid.uuid4().hex[:12]
    parameter = 'Parameters:' + topology + '-control-plane-password'
    secret_id = ET.parse(APPHOST).findtext('.//UserSecretsId')
    secrets = pathlib.Path.home() / '.microsoft/usersecrets' / secret_id / 'secrets.json'
    owned = False
    with tempfile.TemporaryDirectory(prefix='wafish-discovery-') as directory:
        def start(enabled):
            nonlocal owned
            owned = True
            command('aspire', 'start', '--apphost', str(APPHOST), '--non-interactive', '--format', 'Json', '--',
                    '--TopologyId=' + topology, '--GatewayDataDirectory=' + directory,
                    '--EnableServiceDiscovery=' + str(enabled).lower(), '--EnableNetworkFaults=false', '--EnableRedis=false',
                    '--EnableSecondGateway=false', '--EnableExternalLogin=false', '--EnableSonarQube=false')
            for name in ['gateway', 'backend-one', 'backend-two'] + (['discovery-catalog'] if enabled else []):
                aspire('wait', name)
            return document(aspire('describe', '--format', 'Json'))['resources']

        def origin(resources, name, path):
            resource = next(item for item in resources if item['displayName'] == name)
            url = next(item['url'] for item in resource['urls'] if urllib.parse.urlsplit(item['url']).path == path)
            parsed = urllib.parse.urlsplit(url)
            assert parsed.scheme == 'http' and parsed.hostname in ('localhost', '127.0.0.1', '::1')
            return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, '', '', ''))

        try:
            resources = start(True)
            gateway = origin(resources, 'gateway', '/console')
            catalog = origin(resources, 'discovery-catalog', '/catalog')
            one = origin(resources, 'backend-one', '/api/local-service/instance')
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
                    return response.status, response.read()

            def read(path):
                status, body = request(gateway, '/wafish/api/' + path, admin=True)
                assert status == 200, 'Administration read failed: ' + path
                return json.loads(body)

            def mutate(path, value, method='PUT'):
                status, body = request(gateway, '/wafish/api/' + path, value, method, admin=True)
                assert status in (200, 201), f'Scenario mutation failed: {path} ({status}).'
                result = json.loads(body)
                assert result.get('success', True), 'Scenario mutation failed to activate.'
                return result

            def selection(names, mode='healthy'):
                assert request(catalog, '/control', {'mode': mode, 'backends': names}, 'PUT')[0] == 204

            def destinations():
                cluster = next(item for item in read('clusters')['data'] if item['clusterId'] == 'discovery-demo')
                return {item['name']: item['address'] for item in cluster['destinations']}

            def statistics():
                return read('plugin-resources/service-discovery')['customStatistics']

            def proxy():
                return request(gateway, '/discovery-demo/api/local-service/instance')

            mutate('config/clusters/discovery-demo', {'loadBalancingPolicy': 'RoundRobin', 'destinations': {'seed': {'address': one + '/'}}})
            mutate('config/routes/discovery-demo', {'clusterId': 'discovery-demo', 'match': {'path': '/discovery-demo/{**rest}'},
                    'transforms': [{'PathRemovePrefix': '/discovery-demo'}]})
            mutate('plugin-bindings', {'pluginId': 'service-discovery', 'scope': 'Cluster', 'scopeId': 'discovery-demo',
                    'enabled': True, 'schemaVersion': 1, 'configJson': json.dumps({'enabled': True, 'mode': 'HttpJson',
                    'endpoint': catalog + '/catalog', 'refreshSeconds': 5, 'requestTimeoutSeconds': 1,
                    'maxStaleSeconds': 15, 'stalePolicy': 'RejectNewTraffic'})}, method='POST')
            until(lambda: len(destinations()) == 2 and 'seed' not in destinations())
            original = destinations()
            seen = set()
            for _ in range(8):
                status, body = proxy()
                assert status == 200
                seen.add(json.loads(body)['instance'])
            assert seen == {'backend-one', 'backend-two'}, 'Discovered destinations did not both receive traffic.'
            print('PASS: HTTP JSON discovery publishes both sample backends with stable destination identities.')
            selection(['backend-one'])
            until(lambda: len(destinations()) == 1)
            retained = destinations()
            assert all(original.get(key) == value for key, value in retained.items()), 'Removal changed the remaining destination identity.'
            assert all(json.loads(proxy()[1])['instance'] == 'backend-one' for _ in range(4))
            selection(['backend-two', 'backend-one'])
            until(lambda: destinations() == original)
            print('PASS: destination removal and re-addition preserve stable identities and routing.')
            for mode in ('malformed', 'unavailable'):
                selection(['backend-one', 'backend-two'], mode)
                until(lambda: statistics().get('failedBindings', 0) == 1)
                assert proxy()[0] == 200, 'Discovery discarded fresh retained destinations immediately.'
                until(lambda: statistics().get('rejectedBindings', 0) == 1)
                assert proxy()[0] == 503, 'Expired discovery continued forwarding new traffic.'
                selection(['backend-one', 'backend-two'])
                until(lambda: destinations() == original and statistics().get('failedBindings', 0) == 0 and proxy()[0] == 200)
                assert statistics().get('staleBindings', 0) == 0, 'Recovery retained stale health.'
                print('PASS: ' + mode + ' discovery reports failure, retains fresh destinations, rejects expired traffic, and recovers.')
            selection([])
            until(lambda: destinations() == {} and proxy()[0] == 503)
            assert statistics().get('failedBindings', 0) == 0, 'Healthy empty catalog was treated as discovery failure.'
            selection(['backend-one', 'backend-two'])
            until(lambda: destinations() == original and proxy()[0] == 200)
            print('PASS: healthy empty discovery removes destinations and later restores their original identities.')
            # Original gRPC registration remains independent from this demonstration cluster.
            status, body = request(gateway, '/api/local-service/instance')
            assert status == 200 and json.loads(body)['instance'] in ('backend-one', 'backend-two')
            for kind in ('routes', 'clusters'):
                assert request(gateway, '/wafish/api/config/' + kind + '/discovery-demo', method='DELETE', admin=True)[0] == 200
            aspire('stop')
            owned = False
            defaults = start(False)
            assert not any(item['displayName'] == 'discovery-catalog' for item in defaults), 'Catalog resource leaked into the default topology.'
            print('PASS: original gRPC routes remain independent and disabling the profile restores the default topology.')
        finally:
            if owned:
                aspire('stop')
            command('dotnet', 'user-secrets', 'remove', parameter, '--project', str(APPHOST))


if __name__ == '__main__':
    main()
