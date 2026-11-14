#!/usr/bin/env python3
"""Run the sample HTTPS recipe against a private process or an existing container image."""
import argparse
import json
import os
import pathlib
import secrets
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def run(*args, **kwargs):
    result = subprocess.run(args, capture_output=True, text=True, timeout=120, **kwargs)
    if result.returncode:
        raise RuntimeError(f"Command failed: {args[0]} {args[1]} (exit {result.returncode})")
    return result.stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", help="Use this prebuilt image instead of Release process output.")
    parser.add_argument("--engine", default="podman", choices=["podman", "docker"])
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="wafish-https-") as directory:
        state = pathlib.Path(directory)
        password, signing_key, certificate_password = (secrets.token_urlsafe(48) for _ in range(3))
        certificate_environment = dict(os.environ, WAFISH_CERT_PASSWORD=certificate_password)
        run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=localhost",
            "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1", "-keyout", str(state / "key.pem"), "-out", str(state / "cert.pem"))
        run("openssl", "pkcs12", "-export", "-inkey", str(state / "key.pem"), "-in", str(state / "cert.pem"),
            "-out", str(state / "cert.pfx"), "-passout", "env:WAFISH_CERT_PASSWORD", env=certificate_environment)
        listener, upstream = port(), port()
        while upstream == listener:
            upstream = port()
        data_path = "/state" if args.image else directory
        https_port, http_port = (8443, 8081) if args.image else (listener, upstream)
        settings = {
            "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_AUTO_BROWSER": "0",
            "Kestrel__Endpoints__Gateway__Url": f"https://{'0.0.0.0' if args.image else '127.0.0.1'}:{https_port}",
            "Kestrel__Endpoints__Gateway__Protocols": "Http1AndHttp2",
            "Kestrel__Endpoints__Gateway__Certificate__Path": data_path + "/cert.pfx",
            "Kestrel__Endpoints__Gateway__Certificate__Password": certificate_password,
            "Kestrel__Endpoints__Grpc__Url": f"http://127.0.0.1:{http_port}",
            "Kestrel__Endpoints__Grpc__Protocols": "Http1",
            "Gateway__ReactUi__RoutePrefix": "secure/console",
            "Gateway__Dashboard__RoutePrefix": "secure/admin",
            "Gateway__Dashboard__Auth__AuthMode": "DefaultJwt",
            "Gateway__Dashboard__Auth__JwtPassword": password,
            "Gateway__Dashboard__Auth__JwtSecret": signing_key,
            "Gateway__Dashboard__Auth__TwoFactorStatePath": data_path + "/twofactor.json",
            "Gateway__Deployment__RequireLoopbackForDashboard": "false",
            "Gateway__Storage__Sqlite__ConnectionString": "Data Source=" + data_path + "/gateway.db;Pooling=False",
            "Gateway__Registration__Enabled": "false",
        }
        environment_file = state / "environment"
        environment_file.write_text("".join(f"{key}={value}\n" for key, value in settings.items()))
        environment_file.chmod(0o600)
        context = ssl.create_default_context(cafile=str(state / "cert.pem"))
        address = f"https://localhost:{listener}"
        name = "wafish-https-" + uuid.uuid4().hex[:12]
        process = None
        started = False
        log = (state / "host.log").open("w+")

        def request(path, payload=None, token=None, method=None):
            headers = {} if token is None else {"Authorization": "Bearer " + token}
            body = None if payload is None else json.dumps(payload).encode()
            if body is not None:
                headers["Content-Type"] = "application/json"
            req = urllib.request.Request(address + path, data=body, headers=headers, method=method)
            return urllib.request.urlopen(req, context=context, timeout=5)

        try:
            if args.image:
                uid = run(args.engine, "image", "inspect", args.image, "--format", "{{.Config.User}}").strip()
                assert uid and uid not in ("0", "root"), "Container image runs as root."
                options = [args.engine, "run", "--detach", "--name", name, "--publish", f"127.0.0.1:{listener}:8443",
                           "--env-file", str(environment_file), "--volume", directory + ":/state:Z"]
                if args.engine == "podman":
                    options += ["--userns=keep-id:uid=1654,gid=1654"]
                else:
                    options += ["--user", f"{os.getuid()}:{os.getgid()}"]
                started = True
                run(*options, args.image)
                assert run(args.engine, "exec", name, "id", "-u").strip() != "0", "Container process runs as root."
            else:
                process = subprocess.Popen(["dotnet", str(ROOT / "samples/SampleGateway/bin/Release/net10.0/SampleGateway.dll")],
                                           cwd=ROOT / "samples/SampleGateway", env=dict(os.environ, **settings), stdout=log, stderr=log)
            deadline = time.monotonic() + 45
            while True:
                try:
                    with request("/ready") as response:
                        assert response.status == 200
                    break
                except (urllib.error.URLError, TimeoutError):
                    if time.monotonic() >= deadline or (process is not None and process.poll() is not None):
                        raise RuntimeError("Private HTTPS host did not become ready.")
                    time.sleep(.1)
            try:
                request("/secure/admin/api/info")
                raise AssertionError("Anonymous administration succeeded.")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            with request("/secure/admin/login", {"username": "admin", "password": password}) as response:
                cookie = response.headers.get("Set-Cookie", "").lower()
                assert "; secure" in cookie and "; httponly" in cookie, "Session cookie lacks required protections."
                token = json.load(response)["token"]
            with request("/secure/admin/api/info", token=token) as response:
                assert json.load(response)["code"] == 200
            with request("/secure/console/") as response:
                assert b"secure/console" in response.read(), "Custom UI prefix is missing."
            with request("/secure/admin/api/config/routes/smoke-proxy", {
                "clusterId": "smoke", "match": {"path": "/smoke-proxy"},
                "destinations": {"one": f"http://127.0.0.1:{http_port}"}, "transforms": [{"PathSet": "/live"}]}, token, "PUT") as response:
                assert response.status == 200
            with request("/smoke-proxy") as response:
                assert response.status == 200 and response.read(), "Proxy smoke response was empty."
            output = run(args.engine, "logs", name) if args.image else (log.flush() or (state / "host.log").read_text())
            expected = f"https://{'localhost' if args.image else '127.0.0.1'}:{https_port}/secure/console/"
            assert expected in output, "Startup operator link ignored effective HTTPS or UI prefix."
            print("PASS: trusted HTTPS, secure login cookie, custom UI prefix, authenticated administration, proxy route, and operator URL" + (" in a non-root container." if args.image else "."))
        finally:
            if started:
                run(args.engine, "rm", "--force", name)
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            log.close()


if __name__ == "__main__":
    main()
