# SampleFaultProxy

This local development proxy injects bounded network faults into sample backend traffic.
It forwards `/api/local-service/*` to one fixed loopback HTTP origin.

Start the optional resource with Aspire:

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableFaultInjection=true
aspire wait fault-proxy --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

The default topology does not start this resource or change its routes.
The proxy receives `backend-one` as its fixed target. The gateway still starts before backend registration.
The profile does not require containers.

Run the complete, isolated demonstration when this AppHost is stopped:

```bash
python3 scripts/check-network-faults.py
```

The script creates private topology storage and uses generated credentials without printing them.
It installs a separate high-priority route with real retry and circuit-breaker plugin bindings.
A single reset causes a retry. Sustained resets open the circuit and prevent forwarding.
Clearing faults allows a successful recovery probe and subsequent traffic.
A bounded delay then remains visible through the recovered gateway.
The script stops only its own topology and removes its generated credential parameter.

## Controls

GET `/__fault` reads counters. POST `/__fault` applies a fault.
Both require `X-Fault-Key`, supplied by Aspire through the private `Fault:ControlKey` setting.
The control plane uses the topology's generated credential. Do not put it in URLs or shell arguments.

```json
{"mode":"reset","count":1,"delayMilliseconds":0}
```

Modes are `none`, `reset`, and `delay`. `none` clears pending faults.
Reset or delay counts must be 1–100. Delay duration must be 1–5,000 milliseconds.
Faults affect the next matching requests atomically, including gateway retries.
Reset aborts the connection before a response starts. Delay observes client cancellation.
The counters report attempts, active requests, resets, and delays. They contain no request data.
Controls require a private key and bind only to loopback. Requests have a 4 KiB body limit.
The proxy accepts at most 128 concurrent connections.
This is a development fixture, not a production proxy.

## Verification

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-faults
```
