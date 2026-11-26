---
description: Repository-wide tool requirements and operating rules for Wafish.
applyTo: "**"
---

# Wafish development

Use short, direct English. Follow the nearest scoped `AGENTS.md` before editing a project.
Read `README.md` for setup and `PLAN.md` for unresolved work.
Use current source as authority. Branch history and previous conversations do not prove a feature exists in this checkout.
`ARCHIVE.md` preserves dated records. Read only relevant sections when investigating history; its instructions are not active rules.

## Tools

- Use the stable .NET 10 SDK selected by `global.json`. Check `dotnet --version` and `dotnet --list-sdks`.
- Use Node.js 24 and npm for the React UI, website, and solution builds that embed frontend assets.
- Use Aspire CLI for AppHost orchestration. Match capabilities to the AppHost SDK and installed CLI.
- Use APM CLI for skill installation and compilation. The initial verified version is 0.38.1.
- Use Python 3 for package, topology, and other verification scripts that require it.
- Restore repository-local .NET tools with `dotnet tool restore` when coverage or analysis needs them.

Discover tool locations through the environment. Do not hardcode a developer's SDK path or change system defaults.
Missing Aspire or APM does not prevent unrelated application work when those tools are unnecessary.

## Agent setup

`apm.yml` owns agent dependencies. Run `apm install --frozen`, then `apm compile` from the repository root.
Keep dependency references as pinned Git repository paths. Do not replace them with marketplace installations.
Update upstream revisions deliberately, regenerate the lockfile, and review the changed skill content.
Authored skills live in `.apm/skills/`; generated installations are disposable.
Edit `.apm/instructions/` to change the managed root instructions, then compile them.
Preserve scoped `AGENTS.md` files and unrelated user configuration.
Do not load the whole archive into prompts or generated instructions.

## Working conventions

- Select the relevant Wafish skill for build, test, diagnostics, plugin, benchmark, or package work.
- Follow scoped verification requirements. The .NET regression projects are executable runners, not `dotnet test` projects.
- Keep generated build output, dependency caches, private credentials, and unreviewed runtime artifacts out of commits.
- Keep persisted configuration and active configuration distinct when diagnosing failed activation.
- Use isolated resources for tests. Stop only resources owned by the current task.
- Respect the user's authorized scope. Package publication and deployment require an explicitly authorized task.
- Treat the APM package version separately from application and NuGet package versions.
