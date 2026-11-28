#!/usr/bin/env python3
"""Verify opt-in telemetry reports through an owned local Aspire topology."""
import json
import pathlib
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
APPHOST = ROOT / 'Wafish.AppHost/Wafish.AppHost.csproj'
MARKER = 'WAFISH_TELEMETRY_CONFIGURATION '


def command(*args):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, timeout=180)
    if result.returncode:
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


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def main():
    running = document(command('aspire', 'ps', '--format', 'Json', '--non-interactive'))
    if any(pathlib.Path(item.get('appHostPath', '')).resolve() == APPHOST for item in running):
        raise RuntimeError('The target AppHost is already running. This check requires an unused target.')
    topology = 'telemetry-' + uuid.uuid4().hex[:12]
    parameter = 'Parameters:' + topology + '-control-plane-password'
    secret_id = ET.parse(APPHOST).findtext('.//UserSecretsId')
    secrets = pathlib.Path.home() / '.microsoft/usersecrets' / secret_id / 'secrets.json'
    owned = False

    def aspire(*args):
        return command('aspire', *args, '--apphost', str(APPHOST), '--non-interactive')

    with tempfile.TemporaryDirectory(prefix='wafish-telemetry-') as directory:
        try:
            for enabled in (True, False):
                flag = str(enabled).lower()
                owned = True
                command('aspire', 'start', '--apphost', str(APPHOST), '--non-interactive', '--format', 'Json', '--', '--TopologyId=' + topology,
                       '--GatewayDataDirectory=' + directory, '--EnableTelemetryDiagnostics=' + flag,
                       '--EnableServiceDiscovery=' + flag, '--EnableSecondGateway=' + flag,
                       '--EnableNetworkFaults=false', '--EnableRedis=false', '--EnableExternalLogin=false', '--EnableSonarQube=false')
                password = json.loads(secrets.read_text(encoding='utf-8-sig'))[parameter]
                names = ['gateway', 'backend-one', 'backend-two'] + (['gateway-secondary', 'discovery-catalog'] if enabled else [])
                for name in names:
                    aspire('wait', name)
                    output = aspire('logs', name, '--format', 'Json')
                    reports = [text.split(MARKER, 1)[1].strip() for text in strings(document(output)) if MARKER in text]
                    assert len(reports) == (1 if enabled else 0), f'{name} emitted an unexpected report count.'
                    if enabled:
                        report = json.loads(reports[0])
                        assert report['state'] == 'startup-registration' and report['scope'] == 'SampleServiceDefaults'
                        assert set(report['exporters'].values()) == {'otlp'}, 'Aspire exporter registration is missing.'
                        assert 'Wafish.Plugin.Compression' in report['metrics'] and 'HTTP client' in report['tracing']
                        assert password not in reports[0] and 'http:' not in reports[0] and 'https:' not in reports[0], 'Report leaked a credential or URL.'
                aspire('stop')
                owned = False
                print('PASS: ' + ('all five sample resources report allowlisted OTLP and instrumentation registrations.' if enabled else 'default resources emit no telemetry configuration report.'))
        finally:
            if owned:
                aspire('stop')
            command('dotnet', 'user-secrets', 'remove', parameter, '--project', str(APPHOST))


if __name__ == '__main__':
    main()
