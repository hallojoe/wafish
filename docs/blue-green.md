# Blue-green cutover and rollback

This walkthrough switches one route between two backend versions without restarting the gateway.
It preserves the route ID, route UID, transforms, metadata, and plugin bindings.
Rollback restores the saved route configuration. It does not replace unrelated gateway configuration.

The [Aspire sample](aspire.md) supplies `backend-one` and `backend-two`.
They run the same application binary. Use their instance names to demonstrate version selection.
For real releases, deploy separate compatible application versions and verify their version endpoints.
This walkthrough does not deploy applications or migrate their databases.

## Prepare

Start the local topology through the [Aspire guide](aspire.md).
Use Bash, curl, and jq for these commands.
Copy the gateway HTTP origin and both backend HTTP origins from the Aspire dashboard.
Do not use the gRPC endpoint or append `/console` to `GATEWAY`.
Replace each `PORT` below. Do not include trailing slashes.

```bash
set -euo pipefail
GATEWAY='http://localhost:PORT'
BLUE='http://localhost:PORT'
GREEN='http://localhost:PORT'
ADMIN="$GATEWAY/wafish/api"
DEMO_DIR=$(mktemp -d)
chmod 700 "$DEMO_DIR"
AUTH_FILE="$DEMO_DIR/curl-auth"
touch "$AUTH_FILE"
chmod 600 "$AUTH_FILE"
printf 'Demo files: %s\n' "$DEMO_DIR"
```

Edit `$AUTH_FILE` locally with this curl configuration:

```text
user = "admin:<generated-password>"
```

Use the generated password described in the Aspire guide. Do not put credentials in command arguments or source control.
Escape quotes and backslashes using curl configuration syntax if your password contains them.
For remote administration, use HTTPS with certificate validation.
The following function sends credentials only to the administration origin.

```bash
admin() { curl --silent --show-error --fail-with-body --max-time 15 --config "$AUTH_FILE" "$@"; }
put() {
  admin -X PUT -H 'Content-Type: application/json' --data-binary "@$2" "$ADMIN/config/$1" |
    jq -e '.success == true and .activeVersion != null and .activeVersion == .persistedVersion'
}
probe() {
  curl --silent --show-error --fail --max-time 5 "$1/health" >/dev/null
  curl --silent --show-error --fail --max-time 5 "$1/api/local-service/instance" |
    jq -e --arg expected "$2" '.instance == $expected'
}
route() {
  admin "$ADMIN/routes" | jq -e '.data[] | select(.routeId == "demo-blue-green")'
}
verify() {
  local expected="$1" cluster="$2"
  route | jq -e --arg uid "$ORIGINAL_UID" --arg cluster "$cluster" \
    '.routeUid == $uid and .clusterId == $cluster'
  for attempt in {1..20}; do
    if curl --silent --show-error --fail --max-time 5 \
      "$GATEWAY/blue-green/api/local-service/instance" |
      jq -e --arg expected "$expected" '.instance == $expected'; then return 0; fi
    sleep 0.5
  done
  return 1
}
```

Reserve the names `demo-blue-green`, `demo-blue`, and `demo-green` for this walkthrough.
Stop if these names already exist. Do not overwrite another operator's configuration.
Use one operator during the walkthrough. Single-route PUT does not provide an expected-version guard.

```bash
admin "$ADMIN/config/export" > "$DEMO_DIR/before.json"
jq -e '.data.ReverseProxy |
  (.Routes | has("demo-blue-green") | not) and
  (.Clusters | has("demo-blue") or has("demo-green") | not)' "$DEMO_DIR/before.json"
probe "$BLUE" backend-one
probe "$GREEN" backend-two
```

## Create the isolated demo route

The two clusters contain separate destinations and active health probes.
The existing `sample-service` registration remains independent.

```bash
for color in blue green; do
  if [ "$color" = blue ]; then address="$BLUE/"; else address="$GREEN/"; fi
  jq -n --arg address "$address" '{
    Destinations: {backend: {Address: $address}},
    HealthCheck: {Active: {Enabled: true, Interval: "00:00:01", Timeout: "00:00:01",
      Policy: "ConsecutiveFailures", Path: "/health"}}
  }' > "$DEMO_DIR/$color-cluster.json"
  put "clusters/demo-$color" "$DEMO_DIR/$color-cluster.json"
done
cat > "$DEMO_DIR/blue-route.json" <<'JSON'
{
  "ClusterId": "demo-blue",
  "Match": {"Path": "/blue-green/{**rest}"},
  "Transforms": [{"PathRemovePrefix": "/blue-green"}],
  "Metadata": {"demo": "blue-green"}
}
JSON
put routes/demo-blue-green "$DEMO_DIR/blue-route.json"
ORIGINAL_UID=$(route | jq -er '.routeUid | select(length > 0)')
verify backend-one demo-blue
```

The proxy removes `/blue-green` before forwarding requests.
Both versions must serve the same downstream paths.
Active health probes exclude unhealthy destinations; they do not automatically select the other cluster.

## Save rollback data and switch to green

Export the full route before the switch. Preserve its original file for rollback.
Do not delete or recreate the route. Updating its cluster preserves its stable identity and route bindings.

```bash
admin "$ADMIN/config/export" |
  jq -e '.data.ReverseProxy.Routes["demo-blue-green"]' > "$DEMO_DIR/rollback-route.json"
jq '.ClusterId = "demo-green"' "$DEMO_DIR/rollback-route.json" > "$DEMO_DIR/green-route.json"
probe "$GREEN" backend-two
put routes/demo-blue-green "$DEMO_DIR/green-route.json"
verify backend-two demo-green
```

A failed health or identity probe stops the switch before any route update.
After an uncertain mutation response, inspect the route and proxy response before retrying.
If green verification fails after activation, run the rollback commands below.
The API response must report equal active and persisted versions.
The verification checks both stable route identity and the backend reached through the gateway.

## Roll back to blue

Keep blue running throughout the validation period.
Use a new shell with restored variables if `set -e` terminated your previous shell.

```bash
probe "$BLUE" backend-one
put routes/demo-blue-green "$DEMO_DIR/rollback-route.json"
verify backend-one demo-blue
```

This restores the route's saved configuration, including transforms and metadata.
It does not reverse application writes, schema changes, or other configuration updates.
Existing requests can finish on their original backend. New requests use the activated route configuration.
Keep both versions compatible with shared data during the rollout and rollback period.
A health check is a point-in-time signal, not a guarantee that later requests will succeed.

## Remove demo configuration

After verification, remove only the resources created here.
Keep rollback files until you no longer need them. The export can contain sensitive configuration.

```bash
admin -X DELETE "$ADMIN/config/routes/demo-blue-green" | jq -e '.success'
admin -X DELETE "$ADMIN/config/clusters/demo-green" | jq -e '.success'
admin -X DELETE "$ADMIN/config/clusters/demo-blue" | jq -e '.success'
rm -rf -- "$DEMO_DIR"
```

The [blue-green regression check](../tests/Wafish.Regression/BlueGreenChecks.cs) exercises these APIs with isolated HTTP backends.
It verifies failed-candidate gating, cutover, rollback, stable route UID, saved metadata, and unrelated route preservation.
Run it with `dotnet run --project tests/Wafish.Regression -c Release -- --group blue-green`.
