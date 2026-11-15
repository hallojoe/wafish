export type Slide = {
  title: string;
  kicker: string;
  subtitle?: string;
  points?: string[];
  scene?: string;
  image?: string;
  code?: string;
  type?: string;
  time: number;
  notes: string;
};
export const slides: Slide[] = [
  {
    title: "Your gateway.\nUnder control.",
    kicker: "WAFISH / A TECHNICAL FIELD GUIDE",
    subtitle: "Route. Protect. Observe. Operate.",
    type: "title",
    time: 35,
    notes:
      "Welcome. This is a tour of Wafish, a .NET gateway built on Microsoft YARP. The focus is the work around forwarding: deciding policy, changing configuration, and understanding what actually became active. The green requests in this presentation are an illustrative model. They are not live traffic or benchmark results. The talk takes about 27 minutes, including the demonstrations. Sound is optional. Every important state also has a visible label.",
  },
  {
    title: "Forwarding is\nonly the beginning.",
    kicker: "01 / THE OPERATING PROBLEM",
    points: [
      "Who owns the route?",
      "Which policy handled this request?",
      "Did the saved change become active?",
    ],
    time: 70,
    notes:
      "Begin with an ordinary incident. A service is healthy, but clients report intermittent failures. Someone changed a route, another person changed a binding, and the gateway has several destinations. A forwarding engine alone cannot answer every operational question. We need to connect the configuration we intended with the behavior we observed. Ask the room which of these questions usually takes longest to answer. The purpose of Wafish is to put these workflows together. It does not remove the need for application ownership, capacity planning, or deployment-specific validation.",
  },
  {
    title: "YARP at the core.\nOperations around it.",
    kicker: "02 / THE BOUNDARY",
    points: [
      "YARP: forwarding, destination selection, transforms, configuration providers.",
      "Wafish: persistent management, policy lifecycle, registration, and an operator workspace.",
      "ASP.NET Core remains the host.",
    ],
    time: 60,
    notes:
      "Give YARP explicit credit. YARP already supports dynamic configuration through providers. It already integrates with framework logging, tracing, and metrics. It has middleware and policy extension points. Wafish does not invent those capabilities. Wafish packages management, persistence, operational policies, and a user interface around that foundation. This matters when choosing the abstraction: a small custom proxy may only need YARP, while an operated gateway may benefit from the workflows shown here. All the following scenes preserve this boundary.",
  },
  {
    title: "Compose the gateway\nyou actually need.",
    kicker: "03 / PACKAGE BOUNDARIES",
    points: [
      "Runtime + SQLite: a small forwarding host.",
      "Administration: authenticated management workflows.",
      "React UI: an optional operator workspace.",
    ],
    type: "layers",
    time: 75,
    notes:
      "Walk from the bottom of the stack upward. Storage supplies the persistence implementation. Runtime hosts the pipeline and built-in plugins. Administration adds authenticated APIs and operational workflows. React UI presents them in a workspace. Applications choose their composition. The runtime-only sample has no dashboard or AI requirement. Separate control-plane and proxy deployment options exist, but administrators must still configure access and topology deliberately. Point out that package composition and deployment roles are related choices, not interchangeable claims about automatic distributed consistency.",
  },
  {
    title: "Follow one request.",
    kicker: "04 / REQUEST PATH",
    scene: "request",
    time: 90,
    notes:
      "Use Step through four times. First identify the ingress request. Then identify the policy gate and the route-scoped bindings. The diagram compresses several middleware concerns into one gate; actual ordering comes from plugin manifests and dependencies. Next point to YARP. It selects an eligible destination and forwards. Finally explain the response path and the metrics observed along it. Pause motion if the room needs to examine the structure. Ask attendees to distinguish the control plane, which changes configuration, from this data path, which handles requests. Replay restores the starting state.",
  },
  {
    title: "A plugin needs\nmore than middleware.",
    kicker: "05 / POLICY LIFECYCLE",
    points: [
      "11 first-party plugins with scoped bindings.",
      "Declared ordering and host requirements.",
      "Owned startup, health, draining, and disposal.",
    ],
    time: 60,
    notes:
      "A plugin can include more than a request delegate. Discovery owns background refresh work. A Redis-backed policy owns a connection lifecycle. External plugins may own resources that must stop cleanly. Wafish uses explicit contracts for these responsibilities. Registration in dependency injection does not start a resource. Retired generations wait for active request leases. Cancellation does not magically stop arbitrary third-party code, so cooperative resource ownership remains important. The operator can inspect drain diagnostics instead of guessing whether a retired generation is still in use.",
  },
  {
    title: "Make the policy\ndecision visible.",
    kicker: "06 / WAF",
    scene: "policy",
    time: 90,
    notes:
      "Start with an allowed request. Advance to the blocking case and show that the request does not reach an upstream destination. Advance again to detection-only behavior. A configured rule match is recorded without blocking this request. Clarify that hard request limits, decoding failures, and scanner failures still block. Exclusions are scoped to exact fields and supported rules; they are not a general bypass switch. Give a practical rollout example: observe a noisy rule on a selected route, inspect the evidence, then choose an explicit configuration. Do not describe this as a complete application security guarantee.",
  },
  {
    title: "Spend resources\ndeliberately.",
    kicker: "07 / ADMISSION & RESPONSE POLICY",
    points: [
      "Local or Redis-backed rate limits.",
      "Bounded request and response processing.",
      "Compression follows response negotiation.",
    ],
    time: 50,
    notes:
      "Connect three resource concerns. Admission decides whether new work is permitted. Bounded processing limits the work the gateway owns. Compression can reduce transfer size when response negotiation permits it. Local limits apply within one process; Redis-backed limits coordinate through Redis and need an explicit outage policy. Do not imply that every rate-limit algorithm has identical burst behavior. Do not promise that compression helps every response. Small responses, content already compressed, and client encoding preferences all matter.",
  },
  {
    title: "One public response.\nLess repeated work.",
    kicker: "08 / CACHE",
    scene: "cache",
    time: 100,
    notes:
      "Advance slowly through miss, hit, shared fill, and purge. A miss reaches the upstream and can create an eligible public cache entry. A hit uses a fresh entry without another upstream attempt. Eligible identical misses can share one fill within this process, with bounded waiting and admission. A purge removes selected completed entries and fences pending fills. Explain that credentials, private responses, headers, and HTTP directives still affect eligibility. This is not a distributed cache or a claim about production throughput. Pause on the cache-hit route and ask where the upstream work disappeared.",
  },
  {
    title: "Recovery needs\na budget.",
    kicker: "09 / RETRY & CIRCUIT BREAKING",
    scene: "resilience",
    time: 90,
    notes:
      "The example request is a safely replayable GET. Show the first destination failing. An eligible retry can select a different destination, but only within attempt, total-time, and shared cluster retry budgets. Then show an open circuit excluding the failed destination. Recovery uses bounded half-open probes after the deadline. The final state restores eligibility after a successful probe. Stress that retries do not provide exactly-once semantics. A non-idempotent operation can have side effects before a lost response. Also distinguish retry policy from circuit policy: they cooperate, but they solve different problems.",
  },
  {
    title: "Observe attempts.\nExplain outcomes.",
    kicker: "10 / OPERATIONS",
    points: [
      "Route and cluster latency distributions.",
      "Stored request metadata and audit records.",
      "OpenTelemetry instruments and bounded diagnostics.",
    ],
    time: 70,
    notes:
      "Metrics describe attempts that reach the metrics middleware. Depending on pipeline placement, retries can therefore affect observed counts and latency. They are not automatically a measure of unique end-user operations. Request logs provide bounded metadata with configured redaction. Audit records connect management changes with an authenticated actor and revisions. Diagnostic bundles collect operational state without configuration bodies or credentials. These are tools for investigation, not proof that every integration is healthy. Operators should correlate them with backend traces and deployment telemetry.",
  },
  {
    title: "A workspace for\nthe person on call.",
    kicker: "11 / ADMINISTRATION",
    image: "console-routes.webp",
    time: 80,
    notes:
      "This screenshot comes from the actual React administration workspace using an isolated demo fixture. It is not a proposed UI or a production customer deployment. Point out routes, clusters, and the available workflows. Configuration can use forms or the YARP schema. The same workspace also includes policies, logs, audit, and history. Explain the optional package boundary: users can include the workspace with administration or build a runtime-only application. The source documentation describes prefix handling and authentication configuration. Do not suggest that a screenshot demonstrates live traffic.",
  },
  {
    title: "Saved and active\nare different facts.",
    kicker: "12 / CONFIGURATION CHANGE SETS",
    scene: "configuration",
    time: 120,
    notes:
      "Spend time on this distinction. Preview validates the complete desired configuration without writing storage or activating runtime resources. Commit can atomically persist configuration, bindings, and a receipt in SQLite. Runtime activation follows and has its own outcome. Show persisted revision five while active revision four remains visible. Then show successful activation. Finally show the failure example: six persisted, five active. The user interface and API must report that difference. A saved response is not enough evidence to claim the whole change is active. This distinction avoids a class of misleading success messages during incidents.",
  },
  {
    title: "Make repeated\nrequests deliberate.",
    kicker: "13 / CHANGE CONTRACTS",
    points: [
      "Expected revisions detect stale writers.",
      "Idempotency receipts identify repeated operations.",
      "Conflicts and activation failures remain explicit.",
    ],
    time: 70,
    notes:
      "Preview does not reserve the revision. Another administrator can write before apply. Expected versions therefore matter even after a successful preview. When a reply is uncertain, retry the identical operation with the same idempotency key within its retention period. Reusing a key with a changed payload is a conflict. A receipt reports the original acknowledgement, not a guarantee that no later operation changed the system. Complete change sets replace their listed configuration; omitted entries can be removed. These contracts belong in deployment tooling and operator training, not only in API reference pages.",
  },
  {
    title: "Let services\nannounce themselves.",
    kicker: "14 / REGISTRATION & DISCOVERY",
    scene: "lifecycle",
    time: 90,
    notes:
      "Start with registration and renewal. A client can register an instance through authenticated REST or gRPC workflows. Renewable ownership requires the appropriate instance and lease configuration. Registration readiness can report whether local ownership remains valid. Separately, discovery providers refresh destinations from registries such as Consul, Nacos, Eureka, Kubernetes, HTTP JSON, or static configuration. Do not conflate registration ownership with registry discovery. The same gateway can obtain destinations through different supported workflows. EndpointSlice handling has controlled fixture coverage; deployment-specific Kubernetes validation remains necessary.",
  },
  {
    title: "Leave without\ndropping the context.",
    kicker: "15 / DRAINING & OWNERSHIP",
    scene: "lifecycle",
    time: 110,
    notes:
      "Advance to the drain state. The destination stops receiving new traffic while existing work can finish. Then show removal. Explain the ownership tradeoff for ordered registration endpoints: if an earlier gateway cannot confirm release, failover waits. A lost reply can leave ownership uncertain. Lease expiry alone cannot prove that an outstanding renewal will never commit. This design favors exclusive ownership over availability during that uncertainty. Separately, destination maintenance windows are process-local and disappear on restart. Pause here and invite an operational question before moving to adoption.",
  },
  {
    title: "Choose the\ndeployment shape.",
    kicker: "16 / HOSTING",
    points: [
      "Runtime-only, combined, or split roles.",
      "Linux, Windows, and macOS with .NET 10.",
      "Aspire for local orchestration and observation.",
    ],
    time: 60,
    notes:
      "Return to composition. Teams can run a small proxy-only host or include administration and the workspace. Deployment roles should match their access boundary and operational topology. The candidate passed hosted package and storage checks on Linux, Windows, and macOS. This statement does not mean every optional registry, AI provider, or production infrastructure combination was tested. Aspire supplies a convenient local environment with services and telemetry. It is not required by the runtime-only sample. Explain which local setup you use for your own talk, if applicable.",
  },
  {
    title: "Keep the state.\nKnow the boundary.",
    kicker: "17 / STORAGE & SECURITY",
    points: [
      "SQLite persistence, optional encryption, consistent backups.",
      "Authenticated management and operation permissions.",
      "Administration access is not tenant isolation.",
    ],
    time: 60,
    notes:
      "Storage capabilities can be checked before startup requirements are accepted. SQLite supports consistent backups, including committed WAL data, and optional encryption. Encrypted backups still require the correct key to restore. Administration authentication and operation permissions restrict management workflows. They do not create route ownership or tenant isolation by themselves. Production access, trusted proxy configuration, logging choices, and secret handling remain deployment responsibilities. This is a useful point to link the security guide rather than imply that one product removes the need for security design.",
  },
  {
    title: "An assistant.\nWith explicit access.",
    kicker: "18 / OPTIONAL AI",
    points: [
      "Chat-based administration tools.",
      "Separate read and write permissions.",
      "External providers require configuration and credentials.",
    ],
    time: 60,
    notes:
      "AI is optional. It is another way to reach administration tools, not an independent bypass around access policy. Explain the read and write permission split and the need to configure the provider. Avoid claiming that model output is inherently correct or that every generated management action is safe. Operators still need to inspect intended changes, outcomes, and audit records. There is no live model call in this deck, so a network connection or external provider failure cannot interrupt the talk. The product interface provides the real integration workflow.",
  },
  {
    title: "Start with\na small host.",
    kicker: "19 / FIRST REQUEST",
    type: "code",
    code: "git clone https://github.com/hallojoe/wafish.git\ncd wafish\ndotnet run --project samples/MinimalGateway",
    subtitle:
      "Start a backend on port 5090. Follow the guide for the path transform and first request.",
    time: 70,
    notes:
      "The candidate packages are not published yet. Use the source onboarding guide rather than an installation command that cannot resolve. The minimal sample forwards to a backend on port 5090 and listens on 5080. Start the backend separately. The guide explains the path transform required to reach a generated ASP.NET Core backend root. The sample health endpoint reports host readiness, not backend availability. For the full workspace and multiple services, use the Aspire guide. Keep all Wafish package versions aligned once public packages become available.",
  },
  {
    title: "A candidate.\nWith clear limits.",
    kicker: "20 / RELEASE STATUS",
    points: [
      "1.0.0-rc.1 · package publication pending.",
      "Reviewed initial API contracts and hosted platform checks.",
      "Measure capacity in your own deployment.",
    ],
    time: 60,
    notes:
      "State the release status plainly. This is the first public release candidate, with publication pending. Reviewed API snapshots define the initial contract until publication. It is not a claim of compatibility with an older supported public release. Hosted checks cover defined package, storage, and consumer scenarios. Optional external integrations need deployment-specific credentials and validation. A local benchmark does not establish production capacity or a universal performance budget. Point attendees to the release notes for limits that matter to adoption. Credibility comes from accurate boundaries as much as from the feature list.",
  },
  {
    title: "Make it\nyour gateway.",
    kicker: "21 / DISCUSS, EXPLORE, BUILD",
    subtitle: "hallojoe.github.io/wafish",
    type: "closing",
    points: [
      "Read the documentation.",
      "Explore the source.",
      "Bring your operational questions.",
    ],
    time: 50,
    notes:
      "Close by returning to the operational questions from the opening. Wafish connects route configuration, policy lifecycle, and visible outcomes around YARP. Invite questions about the request path, saved versus active state, and service ownership. The website contains this deck, an offline copy, a PDF handout, and the documentation. Four appendix slides follow for deeper discussion. They do not belong to the timed 27-minute main talk. Thank the room and leave the documentation address visible.",
  },
  {
    title: "A small,\nexplicit composition.",
    kicker: "APPENDIX A / HOST CODE",
    type: "code",
    code: "builder.Services.AddWafishStorage();\nbuilder.Services.AddWafishRuntime();\n\napp.UseWafishRuntime();\napp.MapWafishProxy();",
    subtitle:
      "Wafish.Runtime + Wafish.Storage.Sqlite. Add administration and the UI separately.",
    time: 0,
    notes:
      "These calls come from the runtime-only sample. The complete source also creates the builder, builds the application, maps its local health endpoint, and starts the host. Storage is chosen by the application. Adding the administration API and optional UI requires the corresponding packages and their setup. Read the architecture guide for current extension methods and migration notes.",
  },
  {
    title: "One change set.\nTwo observations.",
    kicker: "APPENDIX B / MUTATION OUTCOMES",
    points: [
      "Preview: no persistence and no activation.",
      "Storage failure: the transaction rolls back.",
      "Activation failure: persisted state remains; report active state separately.",
      "Stale revision: re-read before preparing a new request.",
    ],
    time: 0,
    notes:
      "Use this slide when discussing deployment automation. The response must distinguish validation, revision conflict, storage failure, and postcommit activation failure. Exact endpoints, HTTP status mappings, and receipt retention are documented in configuration changes, mutation responses, and change-set contracts. Do not build a client around a single boolean success value.",
  },
  {
    title: "Useful controls.\nExplicit limits.",
    kicker: "APPENDIX C / OPERATIONAL BOUNDARIES",
    points: [
      "Cache sharing and retry budgets are process-local.",
      "Maintenance windows clear on restart.",
      "Uncertain ownership can delay registration failover.",
      "External plugins must cooperate with cancellation.",
    ],
    time: 0,
    notes:
      "These boundaries affect topology and incident response. A multi-replica deployment must account for local policies and state. Maintenance is not durable configuration. Ownership uncertainty is a deliberate failover tradeoff. A plugin contract cannot force an independently rooted thread to stop. Link the security and lifecycle documentation when evaluating a specific deployment.",
  },
  {
    title: "Why not\njust YARP?",
    kicker: "APPENDIX D / CHOOSING THE LAYER",
    points: [
      "Use YARP directly when its extension points fit your scope.",
      "Use Wafish when persistent management and packaged operational workflows help.",
      "The forwarding foundation stays YARP.",
    ],
    time: 0,
    notes:
      "There is no need to diminish YARP to explain Wafish. YARP already includes powerful forwarding, configuration, policy, transform, and diagnostic integration points. Wafish supplies an opinionated operational layer and management experience. Evaluate the additional dependencies and workflows against your actual needs. The website comparison and architecture guide state this division explicitly.",
  },
];
