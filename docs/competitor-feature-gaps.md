# Gateway feature comparison

Reviewed: 2026-11-27. Wafish source baseline: `29fab3a`.

This record compares the requested READMEs with Wafish source, including the redirect plugin.
Linked official documentation clarifies broad README claims. Competitor applications were not executed.
Missing means no supported Wafish implementation or configuration was found in this checkout.
Custom host code can implement some gaps. This does not make them built-in Wafish features.

Sources:

- [VeritasSoftware/AspNetCore.ApiGateway README](https://github.com/VeritasSoftware/AspNetCore.ApiGateway#readme).
- [AneiangSoft/Aneiang.Yarp README](https://github.com/AneiangSoft/Aneiang.Yarp#readme).
- [ThreeMammals/Ocelot README](https://github.com/ThreeMammals/Ocelot#readme).

## Confirmed findings

### G01: Request and response aggregation

Ocelot combines multiple downstream routes into one response, including dependent calls and custom aggregators.
Veritas documents aggregation through a custom route implementation, rather than an equivalent declarative engine.
Wafish forwards through YARP and has no aggregate-route model or response-composition plugin.

Evidence: [Ocelot aggregation](https://ocelot.readthedocs.io/en/latest/features/aggregation.html),
[Veritas customizations](https://github.com/VeritasSoftware/AspNetCore.ApiGateway/blob/HEAD/Docs/README_Customizations.md).
Wafish entry points: [proxy mapping](../src/Wafish.Runtime/GatewayRuntimeExtensions.cs),
[route model](../src/Wafish/Models/DynamicRouteConfig.cs).

### G02: Gateway OpenAPI and Swagger explorer

Veritas documents Swagger access to its gateway facade endpoints.
Wafish has no OpenAPI or Swagger integration for its public proxy surface.
Its configuration editor does not supply API schemas or an interactive request explorer.
The Veritas README does not establish automatic merging of downstream OpenAPI documents.

Evidence: [Veritas implementation](https://github.com/VeritasSoftware/AspNetCore.ApiGateway#implementation).
Wafish entry points: [proxy mapping](../src/Wafish.Runtime/GatewayRuntimeExtensions.cs),
[administration endpoints](../src/Wafish.Administration/AdministrationEndpointRouteBuilderExtensions.cs).

### G03: API consumer SDKs for .NET and TypeScript

Veritas publishes clients for calling HTTP and hub routes through its gateway facade.
Wafish.Client registers backend services and manages their gateway registrations.
It is not a public API consumption SDK. The React administration client is an internal application module.

Evidence: [Veritas clients](https://github.com/VeritasSoftware/AspNetCore.ApiGateway#clients).
Wafish entry points: [registration client](../src/Wafish.Client/Services/GatewayAutoRegistrationClient.cs),
[React API client](../src/Wafish.ReactUi/ClientApp/src/api/client.ts).

### G04: SignalR message routing

Veritas provides a gateway hub that invokes downstream hub methods and relays notifications.
It supports broadcast, group, and individual subscriptions.
Wafish provides administration hubs and transparent YARP forwarding, without this application-level message-routing facade.
Ordinary WebSocket proxying is not a missing feature.

Evidence: [Veritas WebSockets](https://github.com/VeritasSoftware/AspNetCore.ApiGateway/blob/HEAD/Docs/README_WebSockets.md).
Wafish entry points: [traffic hub](../src/Wafish.Administration/Infrastructure/Realtime/TrafficHub.cs),
[proxy mapping](../src/Wafish.Runtime/GatewayRuntimeExtensions.cs).

### G05: Azure Functions hosting adapter

Veritas supplies a dedicated Azure Functions facade package and integration instructions.
Wafish provides ASP.NET Core hosting packages, without an Azure Functions worker adapter.
Running an ASP.NET Core host on Azure is a different capability.

Evidence: [Veritas Azure Functions](https://github.com/VeritasSoftware/AspNetCore.ApiGateway/blob/HEAD/Docs/README_AzureFunctions.md).
Wafish entry point: [hosting extensions](../src/Wafish.Runtime/GatewayRuntimeExtensions.cs).

### G06: Declarative claims transformations

Ocelot maps authenticated claims into other claims, headers, query parameters, and downstream paths.
Wafish retains native YARP transforms and supports custom middleware.
It has no equivalent configurable claims-mapping policy.

Evidence: [Ocelot claims transformations](https://ocelot.readthedocs.io/en/latest/features/claimstransformation.html).
Wafish entry points: [route parsing](../src/Wafish/Services/YarpConfigParser.Routes.cs),
[built-in transforms](../src/Wafish.Runtime/Infrastructure/Yarp/BuiltinTransformMiddleware.cs).

### G07: Chinese interface and documentation

Aneiang documents English and Chinese READMEs and both `en-US` and `zh-CN` dashboard language packs.
Wafish currently fixes its React and administration locale to English.
This is an intentional product-scope difference, not a missing routing capability.

Evidence: [Aneiang README](https://github.com/AneiangSoft/Aneiang.Yarp#readme),
[contribution language requirements](https://github.com/AneiangSoft/Aneiang.Yarp#contributing).
Wafish entry points: [React locale](../src/Wafish.ReactUi/ClientApp/src/locales/i18n.tsx),
[administration locale](../src/Wafish.Administration/Infrastructure/I18n/DashboardI18n.cs).

### G08: Older .NET target support

Aneiang advertises .NET 8 and 9. Ocelot advertises .NET 8, 9, and 10.
Wafish intentionally targets .NET 10 only.
Restoring older targets would add dependency, API, packaging, and maintenance work.
These README claims do not establish current Microsoft lifecycle support or package availability.

Evidence: [Aneiang package structure](https://github.com/AneiangSoft/Aneiang.Yarp#package-structure),
[Ocelot installation](https://github.com/ThreeMammals/Ocelot#install).
Wafish entry point: [core project target](../src/Wafish/Wafish.csproj).

### G09: Shared configuration through Consul

Ocelot can store and retrieve gateway configuration through Consul KV.
Wafish supports Consul service discovery, but has no first-party Consul configuration store.
Its SQLite repositories and configuration publisher do not provide equivalent cross-instance configuration synchronization.
This capability alone does not constitute a complete remote control plane.

Evidence: [Ocelot Consul configuration](https://ocelot.readthedocs.io/en/latest/features/configuration.html#store-in-consul).
Wafish entry points: [configuration publisher](../src/Wafish/Services/DynamicConfigPublisher.cs),
[configuration storage](../src/Wafish.Storage.Sqlite/SqliteConfigurationSnapshotRepository.cs).

### G10: Service discovery routing from request paths

Ocelot can select a discovered service from the first request-path segment, without defining each static route.
Its documented mode cannot combine static and dynamic routes.
Wafish supports dynamic route updates, but discovery requires a configured cluster and service name.
It does not select arbitrary discovered services from request paths.

Evidence: [Ocelot dynamic routing](https://ocelot.readthedocs.io/en/latest/features/servicediscovery.html#dynamic-routing).
Wafish entry points: [discovery configuration](../src/Wafish.Plugin.ServiceDiscovery/HttpServiceDiscoveryPlugin.cs),
[discovery refresh](../src/Wafish.Plugin.ServiceDiscovery/ServiceDiscoveryRefreshService.cs).

### G11: Native Service Fabric discovery

Ocelot integrates with Service Fabric naming and partition routing.
Wafish has no Service Fabric discovery provider.
Ocelot marks this integration as frozen, which reduces its value as a roadmap signal.

Evidence: [Ocelot Service Fabric](https://ocelot.readthedocs.io/en/latest/features/servicefabric.html),
[integration status](https://github.com/ThreeMammals/Ocelot#readme).
Wafish entry point: [discovery provider schema](../src/Wafish.Plugin.ServiceDiscovery/HttpServiceDiscoveryPlugin.cs).

### G12: Replaceable response-cache provider

Ocelot exposes a custom response-cache interface and a CacheManager integration.
Wafish's cache middleware depends on its concrete, sealed memory store.
It has no equivalent supported cache-provider extension point.
This finding does not establish a first-party Redis response cache in Ocelot.

Evidence: [Ocelot custom caching](https://ocelot.readthedocs.io/en/latest/features/caching.html#custom-caching).
Wafish entry points: [cache middleware](../src/Wafish.Plugin.Cache/ResponseCacheMiddleware.cs),
[cache store](../src/Wafish.Plugin.Cache/ResponseCacheStore.cs).

### G13: Configurable route claims and scope requirements

Ocelot exposes allowed scopes and required claim values in route configuration.
Wafish supports named ASP.NET Core authorization policies through YARP.
The gap is direct operator configuration of these requirements, without implementing named host policies.
Authentication and authorization themselves are already supported.

Evidence: [Ocelot scopes](https://ocelot.readthedocs.io/en/latest/features/authentication.html#allowed-scopes),
[claim requirements](https://ocelot.readthedocs.io/en/latest/features/authorization.html).
Wafish entry points: [route authorization parsing](../src/Wafish/Services/YarpConfigParser.Routes.cs),
[security pipeline](../src/Wafish.Runtime/GatewayPipelineExtensions.cs).

### G14: Client-header rate partitions and exemptions

Ocelot supports a client-ID header and a client exemption list.
Its limiter does not authenticate that header. Trusted identity validation must precede it.
Wafish's local limiter supports IP, user, route, and global partitions; its Redis limiter uses route and IP.
Neither plugin exposes equivalent header-based partitions and client exemptions.

Evidence: [Ocelot client partitions](https://ocelot.readthedocs.io/en/latest/features/ratelimiting.html#by-client-s-header).
Wafish entry points: [local limiter](../src/Wafish.Plugin.RateLimit/Middleware/RateLimitMiddleware.cs),
[Redis limiter](../src/Wafish.Plugin.RateLimit.Redis/Middleware/RedisRateLimitMiddleware.cs).

### G15: Configurable rate-limit rejection behavior

Ocelot configures post-quota waiting periods, rejection status, response text, and response-header emission.
Wafish exposes several limiting algorithms and queue limits, but fixes rejection status and response content.
Its queue limit is not an equivalent post-quota waiting period.

Evidence: [Ocelot rate-limit options](https://ocelot.readthedocs.io/en/latest/features/ratelimiting.html).
Wafish entry points: [local limiter](../src/Wafish.Plugin.RateLimit/Middleware/RateLimitMiddleware.cs),
[Redis limiter](../src/Wafish.Plugin.RateLimit.Redis/Middleware/RedisRateLimitMiddleware.cs).

### G16: Published gateway testing utilities

Ocelot provides an Ocelot.Testing package for testing its core and extensions.
Wafish keeps regression runners and fixtures inside its repository.
It has no equivalent published testing package for plugin authors.
This is a developer-tooling difference, not a request-processing feature.

Evidence: [Ocelot repository structure](https://github.com/ThreeMammals/Ocelot#repository-structure),
[testing package project](https://github.com/ThreeMammals/Ocelot/blob/HEAD/testing/Ocelot.Testing.csproj).
Wafish entry points: [regression runner](../tests/Wafish.Regression/Wafish.Regression.csproj),
[plugin fixture](../tests/Fixtures/Wafish.CollectiblePlugin/Wafish.CollectiblePlugin.csproj).

## Value and effort

These scores are judgments for general Wafish adoption, not measured demand or delivery estimates.
Value: 1 means narrow benefit; 10 means broad, substantial benefit.
Effort: 1 means a small change; 10 means major architectural and maintenance work.
Effort includes configuration, administration, validation, tests, documentation, and security requirements.

| ID | Feature | Description | Found in | Value | Effort |
| --- | --- | --- | --- | ---: | ---: |
| G06 | Claims transformations | Map authenticated claims into downstream request data. | Ocelot | 8 | 5 |
| G13 | Route claims and scopes | Configure access requirements directly on routes. | Ocelot | 8 | 6 |
| G01 | Request aggregation | Combine downstream calls into one response. | Ocelot; Veritas customization | 8 | 8 |
| G09 | Shared Consul configuration | Load gateway configuration from a shared Consul store. | Ocelot | 8 | 9 |
| G02 | OpenAPI and Swagger explorer | Document gateway facade endpoints and try requests. | Veritas | 7 | 5 |
| G14 | Client-header quotas | Partition quotas by validated client headers; configure exemptions. | Ocelot | 7 | 5 |
| G03 | API consumer SDKs | Supply .NET and TypeScript clients for gateway calls. | Veritas | 6 | 5 |
| G12 | Replaceable cache providers | Allow response-cache storage implementations through an interface. | Ocelot | 6 | 6 |
| G10 | Discovery routing from paths | Select discovered services using the request path. | Ocelot | 6 | 7 |
| G16 | Testing utilities package | Publish reusable fixtures for gateway extension developers. | Ocelot | 5 | 6 |
| G04 | SignalR message routing | Route hub invocations and subscriptions through a gateway hub. | Veritas | 5 | 8 |
| G15 | Rate-limit rejection options | Configure waiting periods, response text, status, and headers. | Ocelot | 4 | 4 |
| G07 | Chinese localization | Provide Chinese dashboard text and documentation. | Aneiang | 4 | 5 |
| G11 | Service Fabric discovery | Resolve Service Fabric services and partitions. | Ocelot | 3 | 6 |
| G05 | Azure Functions adapter | Provide a gateway facade hosted in Functions workers. | Veritas | 3 | 8 |
| G08 | Older .NET targets | Maintain compatibility with .NET 8 and 9. | Aneiang; Ocelot | 2 | 8 |

Start with claims transformations and configurable route requirements.
OpenAPI exploration is useful, but Wafish must first define which endpoints receive schemas.
Automatic downstream schema aggregation would be a separate, larger feature than the Veritas example.
Treat request aggregation and shared configuration as separate architectural projects.
Older targets, localization, and hosting adapters need demonstrated audience demand before implementation.

## Capabilities excluded from the gap list

- Wafish already supports routing, load balancing, health checks, authentication, authorization, caching, and rate limiting.
- It also supports resilience, transforms, WebSocket forwarding, discovery, diagnostics, administration, and redirects.
- Native YARP and ASP.NET Core capabilities count when Wafish exposes them.
- MVC filters and custom HTTP clients are integration styles, not confirmed missing capabilities.
- A public demo deployment is not a missing runtime feature.
- Ocelot does not advertise GraphQL support as implemented.
- CacheManager integration does not prove a first-party distributed Redis response cache exists.
- Ocelot rate-limit route groups share settings, not necessarily quota counters.

No application features were implemented or added to PLAN.md during this review.
Competitor runtime behavior, package availability, and deployment quality were not independently tested.
