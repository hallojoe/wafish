#!/usr/bin/env python3
"""Compare managed schema-5 benchmark reports without hiding failures or noise."""
import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import statistics


EXIT_CODES = {"passed": 0, "baseline_ready": 0, "regression": 1, "invalid": 2,
              "incompatible": 3, "request_failure": 4, "noisy": 5}
ENVIRONMENT = ("runtime", "operatingSystem", "processArchitecture", "osArchitecture",
               "logicalProcessors", "cpuModel", "serverGc")
METRICS = {"requestsPerSecond": "decrease", "p95Ms": "increase", "p99Ms": "increase"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value, minimum=0):
    return type(value) in (int, float) and math.isfinite(value) and value >= minimum


def read_json(path):
    require(path.stat().st_size <= 16 * 1024 * 1024, "JSON input exceeds 16 MiB")
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON property: " + key)
            result[key] = value
        return result
    def constant(value):
        raise ValueError("Non-finite JSON number: " + value)
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=pairs, parse_constant=constant)


def validate_policy(policy):
    require(policy["schemaVersion"] == 1, "Unsupported budget policy schema")
    for key, lower, upper in (("minimumRepetitions", 3, 100), ("minimumDurationSeconds", 1, 3600),
                              ("minimumWarmupSeconds", 1, 3600)):
        require(type(policy[key]) is int and lower <= policy[key] <= upper, "Invalid policy: " + key)
    require(set(policy["metrics"]) == set(METRICS), "Policy must cover throughput, P95, and P99")
    for name, direction in METRICS.items():
        rule = policy["metrics"][name]
        require(rule["direction"] == direction, "Incorrect metric direction: " + name)
        for key in ("regressionPercent", "maximumSpreadPercent"):
            require(number(rule[key]) and 0 < rule[key] < 100, "Invalid metric tolerance: " + name)


def validate_report(report, policy):
    require(report["schemaVersion"] == 5, "Only managed schema-5 reports are supported")
    environment, settings = report["environment"], report["settings"]
    require(settings["suite"] in ("baseline", "plugins") and settings["processMeasurementsAvailable"] is True,
            "Only managed baseline and plugin suites support budgets")
    require(environment["workingTreeDirty"] is False, "Budget reports require a clean source checkout")
    revision = environment["sourceRevision"]
    require(isinstance(revision, str) and len(revision) == 40 and all(c in "0123456789abcdef" for c in revision),
            "A full source revision is required")
    for key in ENVIRONMENT:
        require(key in environment and environment[key] not in (None, "", "unavailable"), "Missing environment identity: " + key)
    require(type(environment["logicalProcessors"]) is int and environment["logicalProcessors"] > 0,
            "Invalid processor count")
    require(type(environment["serverGc"]) is bool, "Invalid GC identity")
    for key, policy_key in (("repetitions", "minimumRepetitions"), ("durationSeconds", "minimumDurationSeconds"),
                            ("warmupSeconds", "minimumWarmupSeconds")):
        require(type(settings[key]) is int and settings[key] >= policy[policy_key], "Insufficient " + key)
    require(settings["repetitions"] <= 100, "Too many repetitions")
    require(type(settings["concurrency"]) is int and 1 <= settings["concurrency"] <= 4096, "Invalid concurrency")
    for key in ("streaming", "pluginSets", "acceptEncoding", "arrivals", "transport", "latency"):
        require(key in report, "Missing comparison identity: " + key)
    require(report["arrivals"]["mode"] in ("closed-loop", "scheduled"), "Invalid arrival model")
    hosts = {}
    for host in report["hosts"]:
        name, manifest = host["name"], host["manifest"]
        require(name not in hosts, "Duplicate host manifest")
        require(manifest["schemaVersion"] == 1 and isinstance(manifest["configuration"], dict), "Invalid host manifest")
        digest = manifest["configurationHash"]
        require(isinstance(digest, str) and len(digest) == 64 and all(c in "0123456789abcdef" for c in digest), "Invalid configuration hash")
        hosts[name] = {key: manifest[key] for key in ("schemaVersion", "configurationHash", "configuration")}
    require("Backend" in hosts, "Backend manifest is required")
    rows = report["results"]
    require(isinstance(rows, list) and 0 < len(rows) <= 10000, "Missing or oversized result matrix")
    groups = defaultdict(list)
    failures = []
    for row in rows:
        key = (row["gateway"], row["scenario"])
        require(all(isinstance(part, str) and part for part in key), "Invalid result identity")
        require(key[0] in hosts and row["configurationHash"] == hosts[key[0]]["configurationHash"], "Result configuration differs from its host")
        require(type(row["repetition"]) is int, "Invalid repetition index")
        for metric in METRICS:
            require(number(row[metric]) and (row[metric] == 0 or 1e-9 <= row[metric] <= 1e12), "Invalid measurement: " + metric)
        for counter in ("requests", "failures", "transportFailures", "policyRejections", "completedResponses"):
            require(type(row[counter]) is int and row[counter] >= 0, "Invalid request counter: " + counter)
        arrival = row["arrivals"]
        for counter in ("offered", "started", "dropped", "cancelledBeforeStart"):
            require(type(arrival[counter]) is int and arrival[counter] >= 0, "Invalid arrival counter")
        require(arrival["offered"] == arrival["started"] + arrival["dropped"] + arrival["cancelledBeforeStart"], "Arrival accounting mismatch")
        require(arrival["started"] == row["requests"], "Started and finished counts differ")
        require(number(row["errorRatePercent"]) and row["errorRatePercent"] <= 100, "Invalid error percentage")
        if (row["requests"] == 0 or row["completedResponses"] != row["requests"] or
                any(row[c] != 0 for c in ("failures", "transportFailures", "policyRejections", "errorRatePercent")) or
                arrival["dropped"] or arrival["cancelledBeforeStart"]):
            failures.append({"gateway": key[0], "scenario": key[1], "repetition": row["repetition"]})
        groups[key].append(row)
    expected = list(range(1, settings["repetitions"] + 1))
    for rows in groups.values():
        require(sorted(row["repetition"] for row in rows) == expected, "Missing or duplicate repetitions")
    identity = {key: report[key] for key in ("settings", "streaming", "pluginSets", "acceptEncoding", "arrivals", "transport", "latency")}
    identity["environment"] = {key: value for key, value in environment.items()
                               if key not in ("capturedUtc", "sourceRevision", "workingTreeDirty")}
    identity["hosts"] = hosts
    return identity, groups, failures


def spread(values):
    median = statistics.median(values)
    return 0 if max(values) == min(values) else 100 * (max(values) - min(values)) / median if median else math.inf


def evaluate(baseline, candidate, policy):
    validate_policy(policy)
    current_identity, current, failures = validate_report(candidate, policy)
    previous = None
    mismatches = []
    if baseline is not None:
        prior_identity, previous, old_failures = validate_report(baseline, policy)
        failures = [{**row, "report": "candidate"} for row in failures] + [{**row, "report": "baseline"} for row in old_failures]
        mismatches = [key for key in current_identity if current_identity[key] != prior_identity[key]]
        if set(current) != set(previous):
            mismatches.append("resultMatrix")
    if failures:
        return {"status": "request_failure", "failures": failures, "incompatibleFields": mismatches}
    if mismatches:
        return {"status": "incompatible", "incompatibleFields": mismatches}
    measurements = []
    for key, rows in sorted(current.items()):
        for metric, rule in policy["metrics"].items():
            values = [row[metric] for row in rows]
            old = [row[metric] for row in previous[key]] if previous is not None else values
            current_median, baseline_median = statistics.median(values), statistics.median(old)
            # Zero durations cannot establish meaningful relative latency budgets.
            require(current_median > 0 and baseline_median > 0, "Budget measurements must have positive medians")
            direction = -1 if rule["direction"] == "decrease" else 1
            change = direction * 100 * (current_median / baseline_median - 1)
            old_spread, new_spread = spread(old), spread(values)
            status = "passed"
            if max(old_spread, new_spread) > rule["maximumSpreadPercent"]:
                status = "noisy"
            elif previous is not None and change - rule["regressionPercent"] > 1e-9:
                # Require the complete observed ranges to exceed the tolerance.
                # An overlapping range is inconclusive, not proof of a regression.
                separated = (max(values) < min(old) * (1 - rule["regressionPercent"] / 100)
                             if direction == -1 else min(values) > max(old) * (1 + rule["regressionPercent"] / 100))
                status = "regression" if separated else "noisy"
            measurements.append({"gateway": key[0], "scenario": key[1], "metric": metric,
                                 "baselineMedian": baseline_median, "candidateMedian": current_median,
                                 "regressionPercent": change, "budgetPercent": rule["regressionPercent"],
                                 "baselineSpreadPercent": old_spread, "candidateSpreadPercent": new_spread, "status": status})
    statuses = {row["status"] for row in measurements}
    status = "regression" if "regression" in statuses else "noisy" if "noisy" in statuses else "passed" if baseline else "baseline_ready"
    return {"status": status, "measurements": measurements}


def report_path(value):
    path = Path(value)
    if path.is_dir():
        matches = list(path.glob("performance-*.json"))
        require(len(matches) == 1, "Report directory must contain exactly one performance JSON report")
        return matches[0]
    return path


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", help="Earlier report file or single-report directory; omit only to qualify a baseline")
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--policy", default=str(Path(__file__).resolve().parents[1] / "tests/Wafish.Performance/budgets.json"))
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    output = Path(args.output)
    protected = []
    for value in (args.baseline, args.candidate, args.policy):
        if value:
            path = Path(value)
            protected.append(path.resolve())
            if path.is_dir():
                protected.extend(p.resolve() for p in path.glob("performance-*.json"))
    if output.resolve() in protected:
        print("invalid: output cannot replace an input file")
        return EXIT_CODES["invalid"]
    inputs = {}
    try:
        for name in ("baseline", "candidate", "policy"):
            value = getattr(args, name)
            if value:
                path = report_path(value)
                require(output.resolve() != path.resolve(), "Output cannot replace an input file")
                inputs[name] = path
        data = {key: read_json(path) for key, path in inputs.items()}
        result = evaluate(data.get("baseline"), data["candidate"], data["policy"])
        result["inputs"] = {key: {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                            for key, path in inputs.items()}
        result["candidateRevision"] = data["candidate"]["environment"]["sourceRevision"]
        result["baselineRevision"] = data.get("baseline", {}).get("environment", {}).get("sourceRevision")
    except (ValueError, KeyError, TypeError, OSError, AttributeError, OverflowError, RecursionError) as error:
        result = {"status": "invalid", "error": str(error)}
    # Even malformed input must not let an output path overwrite a report or policy.
    protected += [path.resolve() for path in inputs.values()]
    if output.resolve() in protected:
        print("invalid: output cannot replace an input file")
        return EXIT_CODES["invalid"]
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps({"schemaVersion": 1, **result}, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    except OSError as error:
        print("invalid: cannot write decision: " + str(error))
        return EXIT_CODES["invalid"]
    print(result["status"] + ": " + str(output))
    for row in result.get("measurements", []):
        if row["status"] != "passed":
            print(f"  {row['gateway']} / {row['scenario']} / {row['metric']}: {row['status']}; "
                  f"change {row['regressionPercent']:.2f}%, budget {row['budgetPercent']}%; "
                  f"spread {row['baselineSpreadPercent']:.2f}% / {row['candidateSpreadPercent']:.2f}%")
    return EXIT_CODES[result["status"]]


if __name__ == "__main__":
    raise SystemExit(main())
