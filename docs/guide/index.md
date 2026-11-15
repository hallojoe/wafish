# Wafish documentation

Wafish adds gateway operations to Microsoft YARP.
Use it to configure routes, attach policies, register services, and inspect runtime behavior.

## Choose your starting point

- [Run your first gateway](getting-started.md): start from source and forward a real request.
- [Understand the architecture](../architecture.md): select runtime, storage, administration, and UI packages.
- [Explore the plugins](plugins.md): choose policies for routes and clusters.
- [Operate a gateway](operations.md): preview changes, inspect revisions, and handle failures.
- [Secure administration](../security.md): configure access before exposing management endpoints.

## Release status

The first public candidate is `1.0.0-rc.1`. Package publication is pending.
Linux, Windows, and macOS passed the hosted package and storage checks with .NET 10.
Use matching versions of all Wafish packages.

The [release notes](../release-notes-1.0.0-rc.1.md) describe support boundaries and completed verification.
Do not use a local performance run as a production capacity estimate.

## Where YARP ends and Wafish begins

YARP provides forwarding, destination selection, configuration providers, transforms, and extensibility.
It also participates in ASP.NET Core logging, tracing, and metrics.

Wafish adds persistent management workflows, a React workspace, plugin lifecycle management, registration, and operational policies.
See [hosting options](../architecture.md) before selecting packages.

## Documentation conventions

Configuration examples identify their target scope. Secrets use placeholders.
Administration API paths are relative to the configured administration prefix unless stated otherwise.
Illustrations show behavior. They do not represent measured throughput or a live gateway.
