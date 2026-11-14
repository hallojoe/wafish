#!/usr/bin/env python3
"""Verify owned Aspire restarts. No existing AppHost or volume is stopped or removed."""
import base64
import json
import pathlib
import subprocess
import tempfile
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
APPHOST = ROOT / "Wafish.AppHost/Wafish.AppHost.csproj"


def command(*args):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, timeout=180)
    if result.returncode:
        # CLI output can contain dashboard tokens. Keep it out of diagnostics.
        raise RuntimeError(f"Command failed: {args[0]} {args[1]} (exit {result.returncode})")
    return result.stdout


def document(text):
    decoder = json.JSONDecoder()
    for index, character in enumerate(text):
        if character in "[{":
            try:
                return decoder.raw_decode(text[index:])[0]
            except ValueError:
                pass
    raise RuntimeError("CLI did not return JSON.")


def aspire(*args):
    return command("aspire", *args, "--apphost", str(APPHOST), "--non-interactive")


def main():
    running = document(command("aspire", "ps", "--format", "Json", "--non-interactive"))
    if any(pathlib.Path(item.get("appHostPath", "")).resolve() == APPHOST for item in running):
        raise RuntimeError("The target AppHost is already running. This check requires an unused target.")
    topology = "verify-" + uuid.uuid4().hex[:12]
    parameter = "Parameters:" + topology + "-control-plane-password"
    secret_id = ET.parse(APPHOST).findtext(".//UserSecretsId")
    secrets = pathlib.Path.home() / ".microsoft/usersecrets" / secret_id / "secrets.json"
    owned = False
    with tempfile.TemporaryDirectory(prefix="wafish-topology-") as directory:
        def start():
            nonlocal owned
            owned = True
            command("aspire", "start", "--apphost", str(APPHOST), "--non-interactive", "--format", "Json", "--",
                    "--TopologyId=" + topology, "--GatewayDataDirectory=" + directory)
            for resource in ("gateway", "backend-one", "backend-two"):
                aspire("wait", resource)

        def password():
            return json.loads(secrets.read_text(encoding="utf-8-sig"))[parameter]

        def endpoint():
            resources = document(aspire("describe", "--format", "Json"))["resources"]
            gateway = next(resource for resource in resources if resource["displayName"] == "gateway")
            return next(url["url"] for url in gateway["urls"] if url["name"] == "http")

        def request(path, payload=None, method=None, trace_id=None, with_headers=False):
            headers = {}
            if trace_id:
                headers["traceparent"] = "00-" + trace_id + "-" + uuid.uuid4().hex[:16] + "-01"
            if path.startswith("/aneiang/"):
                headers["Authorization"] = "Basic " + base64.b64encode(("admin:" + password()).encode()).decode()
            data = None if payload is None else json.dumps(payload).encode()
            if data is not None:
                headers["Content-Type"] = "application/json"
            req = urllib.request.Request(address + path, data=data, headers=headers, method=method or ("GET" if data is None else "PUT"))
            with urllib.request.urlopen(req, timeout=10) as response:
                body = json.load(response)
                return (body, response.headers) if with_headers else body

        def backends():
            observed = set()
            for _ in range(80):
                observed.add(request("/api/local-service/instance")["instance"])
                if observed == {"backend-one", "backend-two"}:
                    return
                time.sleep(.1)
            raise AssertionError("Both backend registrations did not recover.")

        try:
            start()
            address = endpoint()
            original_password = password()
            backends()
            request("/aneiang/api/config/routes/restart-marker", {
                "clusterId": "marker-cluster", "match": {"path": "/restart-marker"},
                "destinations": {"one": "http://127.0.0.1:9/"}})
            routes = request("/aneiang/api/routes")["data"]
            identity = next(route["routeUid"] for route in routes if route["routeId"] == "restart-marker")
            for resource in ("gateway", "backend-one", "backend-two"):
                aspire("resource", resource, "restart")
                aspire("wait", resource)
                address = endpoint()
                backends()
                assert password() == original_password, "Resource restart changed credentials."
            aspire("stop")
            owned = False
            start()
            address = endpoint()
            backends()
            assert password() == original_password, "AppHost restart changed persisted credentials."
            restored = next(route for route in request("/aneiang/api/routes")["data"] if route["routeId"] == "restart-marker")
            assert restored["routeUid"] == identity, "AppHost restart changed persisted route identity."
            print("PASS: gateway, both backends, and AppHost restart with stable credentials and persisted route identity.")
        finally:
            if owned:
                aspire("stop")
            command("dotnet", "user-secrets", "remove", parameter, "--project", str(APPHOST))


if __name__ == "__main__":
    main()
