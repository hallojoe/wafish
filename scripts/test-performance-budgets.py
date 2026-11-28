#!/usr/bin/env python3
"""Deterministic checks for performance gates; these are not performance measurements."""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/check-performance-budgets.py"
spec = importlib.util.spec_from_file_location("budgets", SCRIPT)
budgets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budgets)
POLICY = json.loads((ROOT / "tests/Wafish.Performance/budgets.json").read_text())


def report():
    return {
        "schemaVersion": 5,
        "environment": {"runtime": ".NET 10", "operatingSystem": "test-linux", "processArchitecture": "X64",
                        "osArchitecture": "X64", "logicalProcessors": 4, "cpuModel": "test-cpu", "serverGc": False,
                        "sourceRevision": "a" * 40, "workingTreeDirty": False, "capturedUtc": "test"},
        "settings": {"suite": "plugins", "processMeasurementsAvailable": True, "repetitions": 5,
                     "durationSeconds": 10, "warmupSeconds": 3, "concurrency": 16, "requestTimeoutSeconds": 10},
        "streaming": None, "pluginSets": [{"name": "none", "plugins": []}], "acceptEncoding": "gzip",
        "arrivals": {"mode": "closed-loop", "ratePerSecond": 0, "queueCapacity": 128, "drainDeadlineSeconds": 12},
        "transport": {"mode": "http1"}, "latency": {"relativeError": .01},
        "hosts": [{"name": name, "manifest": {"schemaVersion": 1, "configurationHash": "a" * 64,
                   "configuration": {"mode": name}}} for name in ("Backend", "Plugins: none")],
        "results": [{"gateway": "Plugins: none", "scenario": "cacheable-json", "repetition": i,
                     "configurationHash": "a" * 64, "requestsPerSecond": 1000., "p95Ms": 2., "p99Ms": 4.,
                     "requests": 10000, "failures": 0, "transportFailures": 0, "policyRejections": 0,
                     "completedResponses": 10000, "errorRatePercent": 0,
                     "arrivals": {"offered": 10000, "started": 10000, "dropped": 0, "cancelledBeforeStart": 0}}
                    for i in range(1, 6)]}


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.baseline, self.candidate = report(), report()

    def evaluate(self):
        return budgets.evaluate(self.baseline, self.candidate, POLICY)

    def set_metric(self, metric, values):
        for row, value in zip(self.candidate["results"], values):
            row[metric] = value

    def test_identical_and_improved_measurements(self):
        self.assertEqual("passed", self.evaluate()["status"])
        self.set_metric("requestsPerSecond", [1100] * 5)
        self.set_metric("p99Ms", [3] * 5)
        self.assertEqual("passed", self.evaluate()["status"])

    def test_exact_budget_boundary_passes(self):
        for metric, value in (("requestsPerSecond", 900), ("p95Ms", 2.4), ("p99Ms", 5)):
            with self.subTest(metric=metric):
                self.set_metric(metric, [value] * 5)
                self.assertEqual("passed", self.evaluate()["status"])

    def test_each_metric_rejects_clear_regression(self):
        for metric, value in (("requestsPerSecond", 800), ("p95Ms", 3), ("p99Ms", 6)):
            with self.subTest(metric=metric):
                self.candidate = report()
                self.set_metric(metric, [value] * 5)
                result = self.evaluate()
                self.assertEqual("regression", result["status"])
                self.assertTrue(any(row["metric"] == metric and row["status"] == "regression" for row in result["measurements"]))

    def test_spread_and_overlapping_ranges_are_inconclusive(self):
        for values in ([500, 1000, 1000, 1000, 1500], [880, 890, 895, 905, 910]):
            self.set_metric("requestsPerSecond", values)
            self.assertEqual("noisy", self.evaluate()["status"])
        self.candidate = report()
        self.set_metric("p95Ms", [2.3, 2.4, 2.5, 2.5, 2.6])
        self.assertEqual("noisy", self.evaluate()["status"])

    def test_noisy_baseline_is_not_a_valid_gate(self):
        self.baseline["results"][0]["requestsPerSecond"] = 100
        self.assertEqual("noisy", self.evaluate()["status"])

    def test_failure_counters_take_priority_over_fast_measurements(self):
        for field in ("failures", "transportFailures", "policyRejections", "errorRatePercent"):
            self.candidate = report()
            self.candidate["results"][0][field] = 1
            self.assertEqual("request_failure", self.evaluate()["status"])
        self.candidate = report()
        self.candidate["results"][0]["completedResponses"] = 9999
        self.assertEqual("request_failure", self.evaluate()["status"])
        self.baseline["results"][0]["failures"] = 1
        self.assertEqual("request_failure", self.evaluate()["status"])

    def test_zero_requests_and_scheduled_drops_fail(self):
        row = self.candidate["results"][0]
        row.update(requests=0, completedResponses=0)
        row["arrivals"].update(offered=0, started=0)
        self.assertEqual("request_failure", self.evaluate()["status"])
        for counter in ("dropped", "cancelledBeforeStart"):
            self.candidate = report()
            self.candidate["arrivals"]["mode"] = "scheduled"
            self.candidate["results"][0]["arrivals"].update({counter: 1, "offered": 10001})
            self.assertEqual("request_failure", self.evaluate()["status"])

    def test_environment_and_workload_changes_are_incompatible(self):
        for section, key, value in (("environment", "cpuModel", "other"), ("environment", "logicalProcessors", 8),
                                    ("environment", "runtime", ".NET other"), ("settings", "concurrency", 32),
                                    ("transport", "mode", "https2"), ("arrivals", "ratePerSecond", 100),
                                    ("latency", "relativeError", .02)):
            self.candidate = report()
            self.candidate[section][key] = value
            self.assertEqual("incompatible", self.evaluate()["status"])
        for key, value in (("streaming", {"kind": "events"}), ("pluginSets", []), ("acceptEncoding", "identity")):
            self.candidate = report()
            self.candidate[key] = value
            self.assertEqual("incompatible", self.evaluate()["status"])

    def test_revision_and_timestamp_are_not_comparison_dimensions(self):
        self.candidate["environment"].update(sourceRevision="b" * 40, capturedUtc="later")
        self.assertEqual("passed", self.evaluate()["status"])

    def test_manifest_and_matrix_changes_are_incompatible(self):
        self.candidate["hosts"][0]["manifest"]["configuration"]["payloadMode"] = "generated"
        self.assertEqual("incompatible", self.evaluate()["status"])
        self.candidate = report()
        for row in self.candidate["results"]:
            row["scenario"] = "changed"
        self.assertIn("resultMatrix", self.evaluate()["incompatibleFields"])

    def test_invalid_reports_reject_missing_duplicates_and_bad_values(self):
        changes = [lambda r: r.update(schemaVersion=4), lambda r: r.update(results=[]),
                   lambda r: r["environment"].update(workingTreeDirty=True),
                   lambda r: r["environment"].update(sourceRevision="unknown"),
                   lambda r: r["environment"].update(cpuModel="unavailable"),
                   lambda r: r["settings"].update(suite="first"),
                   lambda r: r["settings"].update(repetitions=1),
                   lambda r: r["settings"].update(durationSeconds=1),
                   lambda r: r["settings"].update(warmupSeconds=0),
                   lambda r: r["hosts"].append(deepcopy(r["hosts"][0])),
                   lambda r: r["hosts"].pop(0), lambda r: r["results"].pop(),
                   lambda r: r["results"][0].update(repetition=2),
                   lambda r: r["results"][0].update(configurationHash="b" * 64),
                   lambda r: r["results"][0]["arrivals"].update(started=9999),
                   lambda r: r["results"][0].update(failures=-1),
                   lambda r: r["results"][0].update(requestsPerSecond=float("nan")),
                   lambda r: r["results"][0].update(p99Ms=True)]
        for change in changes:
            with self.subTest(change=change):
                self.candidate = report()
                change(self.candidate)
                with self.assertRaises(ValueError):
                    self.evaluate()

    def test_zero_medians_and_invalid_policies(self):
        self.set_metric("p95Ms", [0] * 5)
        with self.assertRaises(ValueError):
            self.evaluate()
        for key, value in (("minimumRepetitions", 1), ("schemaVersion", 2), ("metrics", {})):
            policy = deepcopy(POLICY)
            policy[key] = value
            with self.assertRaises(ValueError):
                budgets.validate_policy(policy)
        policy = deepcopy(POLICY)
        policy["metrics"]["p99Ms"]["direction"] = "decrease"
        with self.assertRaises(ValueError):
            budgets.validate_policy(policy)

    def test_baseline_qualification(self):
        self.assertEqual("baseline_ready", budgets.evaluate(None, self.candidate, POLICY)["status"])

    def test_cli_outputs_evidence_and_distinct_exit_codes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline, candidate, output = (root / name for name in ("baseline.json", "performance-current.json", "decision.json"))
            baseline.write_text(json.dumps(self.baseline))
            candidate.write_text(json.dumps(self.candidate))
            arguments = ["--baseline", str(baseline), "--candidate", str(root), "--output", str(output)]
            self.assertEqual(0, budgets.main(arguments))
            result = json.loads(output.read_text())
            self.assertEqual(64, len(result["inputs"]["baseline"]["sha256"]))
            for expected, change in ((1, lambda: self.set_metric("p99Ms", [6] * 5)),
                                     (3, lambda: self.candidate["environment"].update(cpuModel="other")),
                                     (4, lambda: self.candidate["results"][0].update(failures=1)),
                                     (5, lambda: self.set_metric("p99Ms", [1, 4, 4, 4, 10]))):
                self.candidate = report()
                change()
                candidate.write_text(json.dumps(self.candidate))
                self.assertEqual(expected, budgets.main(arguments))
            for malformed in ('{}', '{', '{"a":1,"a":2}', '{"x":NaN}', '[]'):
                candidate.write_text(malformed)
                self.assertEqual(2, budgets.main(arguments))
            candidate.write_text(json.dumps(report()))
            self.assertEqual(0, budgets.main(["--candidate", str(candidate), "--output", str(output)]))
            self.assertEqual(2, budgets.main(["--candidate", str(candidate), "--output", str(candidate)]))
            self.assertEqual(report(), json.loads(candidate.read_text()))
            (root / "performance-other.json").write_text('{}')
            self.assertEqual(2, budgets.main(arguments))
            self.assertEqual(2, budgets.main(["--candidate", str(root / "missing"), "--output", str(output)]))
            candidate.write_text(' ' * (16 * 1024 * 1024 + 1))
            self.assertEqual(2, budgets.main(["--candidate", str(candidate), "--output", str(output)]))

    def test_process_exit_code(self):
        with tempfile.TemporaryDirectory() as directory:
            completed = subprocess.run([sys.executable, str(SCRIPT), "--candidate", directory,
                                        "--output", str(Path(directory) / "error.json")], capture_output=True)
            self.assertEqual(2, completed.returncode)


if __name__ == "__main__":
    unittest.main()
