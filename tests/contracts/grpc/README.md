# Registration wire fixtures

These bytes use the field numbers in `wafish.v1.GatewayRegistry`.
The fixtures were encoded independently using UTF-8, protobuf varints, and length-delimited fields.
They are fixed compatibility inputs. Do not regenerate them from current generated message classes.

The legacy request omits instance identity. The legacy response omits outcome and revisions.
Current fields include explicit zero revisions and present empty optional strings.
Field 199 simulates a future additive extension. Readers must retain it when messages are forwarded.

Run `dotnet run --project tests/Wafish.Regression -c Release -- --contracts`.
