// Presentation models illustrate documented contracts. They do not execute gateway requests.
export const scenarios = {
  request: {
    label: "Request path",
    description:
      "A request enters a policy gate. YARP selects an eligible destination and forwards the request.",
    steps: [
      {
        text: "A request arrives. Its route selects the configured policy bindings.",
        destination: 0,
        gate: "pass",
        upstream: true,
      },
      {
        text: "Configured plugins evaluate the request. The illustrated order is an example.",
        destination: 1,
        gate: "pass",
        upstream: true,
      },
      {
        text: "YARP selects an eligible destination and forwards the request.",
        destination: 2,
        gate: "pass",
        upstream: true,
      },
      {
        text: "The response returns through the pipeline. Metrics describe the attempts observed.",
        destination: 2,
        gate: "pass",
        upstream: true,
      },
    ],
  },
  policy: {
    label: "Policy gate",
    description:
      "Compare a permitted request, a WAF rejection, and a detection-only match.",
    steps: [
      {
        text: "Allowed: the request passes the configured WAF rules.",
        gate: "pass",
        destination: 0,
        upstream: true,
      },
      {
        text: "Blocked: a matching blocking rule stops this request before forwarding.",
        gate: "block",
        destination: 0,
        upstream: false,
      },
      {
        text: "Detection only: this rule records a match and permits forwarding. Hard resource limits still apply.",
        gate: "detect",
        destination: 1,
        upstream: true,
      },
    ],
  },
  cache: {
    label: "Public response cache",
    description:
      "An eligible public response is filled, reused, shared by waiting requests, and invalidated.",
    steps: [
      {
        text: "MISS: an eligible GET reaches the upstream and fills the local public cache.",
        gate: "pass",
        destination: 0,
        upstream: true,
        cache: "fill",
      },
      {
        text: "HIT: a fresh public entry serves the response without an upstream attempt.",
        gate: "pass",
        destination: 0,
        upstream: false,
        cache: "hit",
      },
      {
        text: "SHARED FILL: eligible identical misses share one fill per process. Waits remain bounded.",
        gate: "pass",
        destination: 0,
        upstream: true,
        cache: "shared",
      },
      {
        text: "PURGE: matching entries are removed. Pending fills are fenced against stale insertion.",
        gate: "pass",
        destination: 0,
        upstream: false,
        cache: "purge",
      },
    ],
  },
  resilience: {
    label: "Bounded recovery",
    description:
      "A failed destination is excluded. Eligible retries use a budget, and half-open probes control recovery.",
    steps: [
      {
        text: "Destination A fails. This example uses a safely replayable GET.",
        gate: "pass",
        destination: 0,
        upstream: true,
        failed: 0,
      },
      {
        text: "An eligible retry selects B. Attempts, total time, and the cluster retry budget remain bounded.",
        gate: "pass",
        destination: 1,
        upstream: true,
        failed: 0,
      },
      {
        text: "OPEN: destination A is excluded. New eligible traffic uses another destination.",
        gate: "pass",
        destination: 2,
        upstream: true,
        failed: 0,
      },
      {
        text: "HALF-OPEN: a limited probe can test A after the recovery deadline.",
        gate: "detect",
        destination: 0,
        upstream: true,
        probe: true,
      },
      {
        text: "Recovery succeeds. A becomes eligible again under the configured policy.",
        gate: "pass",
        destination: 0,
        upstream: true,
      },
    ],
  },
  configuration: {
    label: "Configuration revisions",
    description:
      "Preview a change, persist it atomically, and observe runtime activation separately.",
    steps: [
      {
        text: "PREVIEW: validate the desired configuration. No storage changes or runtime activation occur.",
        gate: "pass",
        destination: 0,
        upstream: true,
        persisted: 4,
        active: 4,
      },
      {
        text: "COMMIT: SQLite stores configuration, bindings, and the receipt in one transaction.",
        gate: "detect",
        destination: 0,
        upstream: true,
        persisted: 5,
        active: 4,
      },
      {
        text: "ACTIVATE: runtime publication is reported separately from persistence.",
        gate: "pass",
        destination: 1,
        upstream: true,
        persisted: 5,
        active: 5,
      },
      {
        text: "FAILURE EXAMPLE: revision 6 persists, but activation fails. Operators see the distinct active revision.",
        gate: "detect",
        destination: 1,
        upstream: true,
        persisted: 6,
        active: 5,
      },
    ],
  },
  lifecycle: {
    label: "Service lifecycle",
    description:
      "Registration adds an instance. Leases track ownership. Draining excludes new traffic while existing work finishes.",
    steps: [
      {
        text: "REGISTER: a service instance joins through an authenticated registration endpoint.",
        gate: "pass",
        destination: 0,
        upstream: true,
      },
      {
        text: "RENEW: heartbeats renew the lease. Registration readiness reflects local ownership state.",
        gate: "pass",
        destination: 1,
        upstream: true,
      },
      {
        text: "DRAIN: A stops receiving new traffic. Existing work can finish.",
        gate: "pass",
        destination: 2,
        upstream: true,
        draining: 0,
      },
      {
        text: "REMOVE: A is removed. Uncertain ownership release delays endpoint failover.",
        gate: "pass",
        destination: 1,
        upstream: true,
        removed: 0,
      },
    ],
  },
};
export function scenarioStep(kind, index) {
  const scenario = scenarios[kind];
  if (!scenario) throw new Error(`Unknown scene: ${kind}`);
  return scenario.steps[
    ((index % scenario.steps.length) + scenario.steps.length) %
      scenario.steps.length
  ];
}
