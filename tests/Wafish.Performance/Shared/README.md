# Shared benchmark helpers

These BCL-only sources are linked into benchmark executables and regression checks.
NativeYarp remains independent of Wafish packages.

`BoundedLatencyHistogram` retains a fixed array, independent of request count.
It reports upper bounds for exact nearest-rank percentiles after all writers complete.
Values above one microsecond have at most 1% relative bucket error, apart from floating-point rounding.
Smaller values have at most one microsecond absolute error.
Values above 24 hours and nonfinite or negative values fail explicitly; they are never silently clamped.
The measurement strategy has no sampling loss or random rank error.

`BenchmarkOptions` accepts explicit loopback origins and both argument-value forms.
