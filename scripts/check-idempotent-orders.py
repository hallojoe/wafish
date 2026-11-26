#!/usr/bin/env python3
"""Verify shared synthetic order idempotency across an owned local Aspire topology."""
import concurrent.futures
import importlib.util
import json
import pathlib
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import uuid

spec = importlib.util.spec_from_file_location("topology", pathlib.Path(__file__).with_name("check-topology.py"))
topology = importlib.util.module_from_spec(spec)
spec.loader.exec_module(topology)


def main():
    running = topology.document(topology.command("aspire", "ps", "--format", "Json", "--non-interactive"))
    if any(pathlib.Path(item.get("appHostPath", "")).resolve() == topology.APPHOST for item in running):
        raise RuntimeError("The target AppHost is already running. This check requires an unused target.")
    identifier = "orders-" + uuid.uuid4().hex[:12]
    parameter = "Parameters:" + identifier + "-control-plane-password"
    owned = False
    with tempfile.TemporaryDirectory(prefix="wafish-orders-topology-") as directory:
        try:
            owned = True
            topology.command("aspire", "start", "--apphost", str(topology.APPHOST), "--non-interactive", "--format", "Json", "--",
                             "--TopologyId=" + identifier, "--GatewayDataDirectory=" + directory)
            for resource in ("gateway", "backend-one", "backend-two"):
                topology.aspire("wait", resource)

            def addresses():
                resources = topology.document(topology.aspire("describe", "--format", "Json"))["resources"]
                result = {}
                for resource in resources:
                    name = resource["displayName"]
                    if name not in ("gateway", "backend-one", "backend-two"):
                        continue
                    path = "/console" if name == "gateway" else "/api/local-service/instance"
                    url = next(item["url"] for item in resource["urls"] if urllib.parse.urlsplit(item["url"]).path == path)
                    parsed = urllib.parse.urlsplit(url)
                    result[name] = urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, "", "", ""))
                return result

            urls = addresses()

            def order(name, key, quantity=2):
                trace = uuid.uuid4().hex
                request = urllib.request.Request(urls[name] + "/api/local-service/orders",
                    data=json.dumps({"productId": "demo-product", "quantity": quantity}).encode(),
                    headers={"Content-Type": "application/json", "Idempotency-Key": key,
                             "traceparent": "00-" + trace + "-" + uuid.uuid4().hex[:16] + "-01"}, method="POST")
                try:
                    with urllib.request.urlopen(request, timeout=10) as response:
                        body = json.load(response)
                        assert response.headers["X-Backend-Instance"] == body["instance"]
                        assert response.headers["Cache-Control"] == "no-store"
                        assert body["traceId"] == trace
                        return response.status, body
                except urllib.error.HTTPError as error:
                    if error.code != 409:
                        raise
                    return error.code, json.load(error)

            status, first = order("backend-one", "restart-key")
            assert status == 201
            status, second = order("backend-two", "restart-key")
            assert status == 200 and first["order"] == second["order"]
            assert first["instance"] == "backend-one" and second["instance"] == "backend-two"
            assert order("backend-two", "restart-key", 3)[0] == 409
            observed = set()
            for _ in range(32):
                status, body = order("gateway", "restart-key")
                assert status == 200 and body["order"] == first["order"]
                observed.add(body["instance"])
                if len(observed) == 2:
                    break
            assert observed == {"backend-one", "backend-two"}
            with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
                results = list(pool.map(lambda index: order("backend-one" if index % 2 == 0 else "backend-two", "concurrent-key"), range(32)))
            assert sum(status == 201 for status, _ in results) == 1
            assert all(status in (200, 201) for status, _ in results)
            assert len({body["order"]["id"] for _, body in results}) == 1
            topology.aspire("resource", "backend-one", "restart")
            topology.aspire("wait", "backend-one")
            urls = addresses()
            status, restored = order("backend-one", "restart-key")
            assert status == 200 and restored["order"] == first["order"]
            print("PASS: direct and gateway replays share one order across both backend processes.")
            print("PASS: concurrent cross-process writes create one order; conflicting payloads return HTTP 409.")
            print("PASS: backend restart preserves the original order and idempotency record.")
        finally:
            if owned:
                topology.aspire("stop")
            topology.command("dotnet", "user-secrets", "remove", parameter, "--project", str(topology.APPHOST))


if __name__ == "__main__":
    main()
