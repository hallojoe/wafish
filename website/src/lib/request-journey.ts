// An explanatory example, not a running gateway or a universal plugin order.
export const journeyStages = [
  {
    name: "Match route",
    role: "ROUTING",
    short: "/catalog/*",
    doc: "configuration/",
  },
  {
    name: "Inspect request",
    role: "WAF",
    short: "Check rules",
    doc: "plugins/waf/",
  },
  {
    name: "Check limit",
    role: "RATE LIMIT",
    short: "Check allowance",
    doc: "plugins/rate-limit/",
  },
  {
    name: "Check cache",
    role: "RESPONSE CACHE",
    short: "Find public response",
    doc: "plugins/cache/",
  },
  {
    name: "Forward",
    role: "YARP",
    short: "Select destination",
    doc: "architecture/",
  },
  {
    name: "Return response",
    role: "CLIENT",
    short: "Complete request",
    doc: "operations/",
  },
] as const;

export const journeyCases = {
  forwarded: {
    label: "Forward to service",
    stop: 4,
    status: "200 OK",
    source: "Catalog service",
    result: "The service handles the request.",
  },
  blocked: {
    label: "WAF blocks",
    stop: 1,
    status: "403 Forbidden",
    source: "WAF policy",
    result: "The gateway rejects the request.",
  },
  limited: {
    label: "Limit reached",
    stop: 2,
    status: "429 Too Many Requests",
    source: "Rate-limit policy",
    result: "The gateway rejects excess traffic.",
  },
  cached: {
    label: "Cache hit",
    stop: 3,
    status: "200 OK",
    source: "Local response cache",
    result: "The gateway returns a stored response.",
  },
} as const;

export type JourneyCase = keyof typeof journeyCases;

export function journeyDetail(kind: JourneyCase, stage: number) {
  const example = journeyCases[kind];
  switch (stage) {
    case 0:
      return {
        title: "The route gives the request its context.",
        text: "The request matches /catalog/*. This example route targets the catalog cluster and has WAF, rate-limit, and cache bindings.",
        decision: "Route matched → use its configured policies.",
        next: "Inspect the request against the configured WAF rules.",
      };
    case 1:
      return kind === "blocked"
        ? {
            title: "A blocking rule ends the request here.",
            text: "In this example, the client IP matches a deny rule. The WAF returns 403 before forwarding.",
            decision: "Blocked → skip the remaining request stages.",
            next: "Return the rejection to the client. The service receives no request.",
          }
        : {
            title: "Inspect the request before it can continue.",
            text: "The WAF checks configured request inputs. This request passes the enabled rules.",
            decision: "Allowed → continue to the next policy.",
            next: "Check whether the client has a rate-limit allowance.",
          };
    case 2:
      return kind === "limited"
        ? {
            title: "No allowance remains for this client.",
            text: "The configured limit is exhausted. With no queue space, this example returns 429.",
            decision: "Rejected → skip cache lookup and forwarding.",
            next: "Return the rejection. The client must wait before trying again.",
          }
        : {
            title: "The request uses an available allowance.",
            text: "The rate-limit policy checks its configured partition. This example limits traffic by client IP.",
            decision: "Permit acquired → continue to the cache.",
            next: "Look for a fresh, eligible public response in the local cache.",
          };
    case 3:
      return kind === "cached"
        ? {
            title: "A cache hit avoids an upstream request.",
            text: "This public GET matches a fresh cache entry. Wafish serves the stored response from this gateway process.",
            decision: "Cache hit → skip YARP forwarding.",
            next: "Return the cached response. The service receives no request.",
          }
        : {
            title: "A cache miss continues to the service.",
            text: "No fresh entry matches this public GET. The request continues upstream. Only an eligible response can fill the cache.",
            decision: "Cache miss → forward the request.",
            next: "Select an eligible destination from the catalog cluster.",
          };
    case 4:
      return {
        title: "YARP forwards to an eligible destination.",
        text: "YARP applies the configured destination selection. In this example, catalog-01 handles the request and returns 200.",
        decision: "One upstream request → receive the service response.",
        next: "Return through the pipeline. Store the response only if cache rules permit it.",
      };
    default:
      return {
        title: `${example.status}. Back to the client.`,
        text:
          kind === "forwarded"
            ? "The service response returns through the pipeline. Enabled logging and metrics capture what their pipeline stages observe."
            : `${example.source} supplies the response. Later request stages are skipped, and the service receives no request.`,
        decision: `${example.source} → client. ${kind === "forwarded" ? "One upstream request." : "Zero upstream requests."}`,
        next: "Choose another outcome to compare where the request stops.",
      };
  }
}
