# Idempotent synthetic orders

The sample backend accepts bounded synthetic orders at `POST /api/local-service/orders`.
An `Idempotency-Key` identifies one request within one backend process and this operation.
Concurrent requests with the same key and product/quantity return the original order.
A conflicting product or quantity returns HTTP 409 without changing the retained order.

```http
POST /api/local-service/orders
Content-Type: application/json
Idempotency-Key: sample-order-00000001

{"product":"sample-widget","quantity":2}
```

Use a fresh random key for each intended operation. Keep that key when retrying the same operation.
Keys contain 16–64 ASCII letters, digits, hyphens, or underscores. Matching is case-sensitive.
Supply exactly one header value. Keys are not authentication credentials.

Only `sample-widget` and `sample-gadget` are accepted. Quantity must be an integer from 1 through 100.
Bodies are limited to 4 KiB. Unknown JSON properties are rejected.
The sample accepts no customer, payment, delivery, or other production data.
It performs no external writes, payments, or fulfillment.

## Results and retention

| Result | HTTP behavior |
| --- | --- |
| First valid request | `201`, `Idempotency-Replayed: false`, and a relative `Location` for the order. |
| Same key and validated payload | Original `201` body and location, with `Idempotency-Replayed: true`. |
| Same retained key with different product or quantity | `409` Problem Details. |
| Invalid key or payload | `400` Problem Details. |
| Oversized body | `413` Problem Details. |
| Capacity reached for a new key | `503` Problem Details with `Retry-After`. Existing keys still replay. |
| Read retained order | `GET /api/local-service/orders/{id}` returns `200`. |
| Read unknown or expired order | `404` Problem Details. |

Successful writes and reads use `Cache-Control: no-store`.
Each result includes its order ID, product, quantity, backend instance, creation time, and expiry time.
Comparison uses validated product and quantity, not JSON whitespace or property order.

Each process retains at most 1,024 orders for five minutes from their first successful creation.
Replays do not extend retention. Unexpired entries are never evicted to admit a new key.
Expired entries are removed during subsequent reads or writes. Idle retained memory stays bounded.
Expiry uses monotonic elapsed time. Displayed UTC timestamps can differ after a system clock adjustment.
After expiry or process restart, the same key can create a new order.
A cancelled request can have committed before its response was lost. Retry with its original key within retention.

## Route to one backend

The default Aspire route balances requests across two independently registered backends.
Those processes have separate stores. The same key can create one order on each backend.
For this demonstration, call one backend directly or create a dedicated gateway cluster with one destination.

Use the assigned **Backend instance** URL for `backend-one`, removing `/api/local-service/instance` to obtain its origin.
Create a separate cluster containing that origin and a route matching `/api/local-service/orders/{**rest}`.
Set its route order below the ordinary sample route, for example `-100`.
Keep the request path unchanged so relative order locations remain valid.
Do not add failover destinations or attach service discovery to this dedicated cluster.
Remove only the demonstration route and cluster afterward.

The optional retry binding below demonstrates recovery from a bodyless upstream failure:

```json
{
  "enabled": true,
  "maxRetries": 1,
  "retryNonIdempotent": true,
  "useDifferentDestination": false,
  "backoffBaseMs": 100,
  "backoffJitterMs": 0
}
```

Enable this policy only on the dedicated order route. Keep its single backend process alive during retries.
Use a complete known-length body. Existing [retry eligibility limits](../src/Wafish.Plugin.Retry/README.md#review-safety-limits) still apply.
The regression fixture commits an order, loses its first response with a bodyless 503, and retries through the gateway.
The second attempt returns the committed order, with one stored result.

This is application-level, process-local idempotency. It does not provide gateway-wide or durable exactly-once execution.
There is no tenant or account isolation in this synthetic sample.
A production service needs authenticated key scope, shared durable receipts, and atomic persistence with its business changes.

## Inspection and verification

The sample exposes `/openapi/v1.json` in Development and the orchestrated sample profile.
The [HTTP examples](../samples/SampleLocalService/SampleLocalService.http) include creation, replay, conflicts, reads, and invalid payloads.
Replace `baseUrl` with one backend origin or the dedicated gateway route origin.

```bash
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-orders
```

Checks cover concurrency, capacity, monotonic expiry, cancellation, body limits, HTTP metadata, OpenAPI, and a real gateway retry.
See [service implementation](../samples/SampleLocalService/Services/SampleOrderService.cs) and [controller](../samples/SampleLocalService/Controllers/SampleOrdersController.cs).
