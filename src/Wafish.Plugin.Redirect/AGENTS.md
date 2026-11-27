# Wafish.Plugin.Redirect: agent notes

Read README.md and the root PLAN.md before changing behavior.
Follow the root plugin, build, test, and package skills.

- Compile templates before persistence and activation. Keep compiled state immutable.
- Use the same evaluator for preview and execution.
- Never fetch redirect targets or interpret raw forwarded headers.
- Validate expanded hosts and URL components before setting Location.
- Preserve route identity, request leases, and backend-free operation.
- Do not add required members to existing plugin interfaces.

Run a Release solution build and both executable regression suites after runtime changes.
Use `--group redirect` for focused core cases. Run frontend coverage and browser checks after editor changes.
Maintain at least 80% coverage for changed executable code.
Update public API baselines only for intentional compatible additions.
