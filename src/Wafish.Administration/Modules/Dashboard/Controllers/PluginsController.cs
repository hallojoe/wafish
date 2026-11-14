using Wafish.Dashboard.Infrastructure.I18n;
using Wafish.Dashboard.Infrastructure.Plugin;
using Wafish.Services;
using Microsoft.AspNetCore.Mvc;
using Wafish.Plugins;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>
/// Plugin management API.
/// </summary>
[Route("api/plugins")]
[ApiController]
[Wafish.Filters.ConfigurationMutation]
public class PluginsController : ControllerBase
{
    private static readonly SemaphoreSlim TransitionGate = new(1, 1);

    private readonly IGatewayPluginManager _manager;
    private readonly IDynamicYarpConfigService _dynamicConfig;
    private readonly IPluginResourceLifecycleCoordinator _resourceLifecycle;
    private readonly IPluginRuntimeDomainManager _runtimeDomains;

    public PluginsController(IGatewayPluginManager manager, IDynamicYarpConfigService dynamicConfig,
        IPluginResourceLifecycleCoordinator resourceLifecycle, IPluginRuntimeDomainManager runtimeDomains)
    {
        _manager = manager;
        _dynamicConfig = dynamicConfig;
        _resourceLifecycle = resourceLifecycle;
        _runtimeDomains = runtimeDomains;
    }

    /// <summary>Return the supported English UI locale.</summary>
    private string ResolveLocale() =>
        DashboardI18n.ResolveLocale(Request);

    /// <summary>Look up a localized value from i18n resources; fall back to the manifest value.</summary>
    private string Localize(string i18nKey, string fallback)
    {
        var dict = DashboardI18n.GetDict(ResolveLocale());
        return dict.TryGetValue(i18nKey, out var localized) ? localized : fallback;
    }

    /// <summary>Get all registered plugins.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPlugins(CancellationToken cancellationToken)
    {
        var states = _manager.GetPluginStates().ToDictionary(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var plugins = new List<object>();

        foreach (var manifest in _manager.GetAllManifests())
        {
            var state = states[manifest.Id];
            var actualResources = _resourceLifecycle.GetRuntimeResources(manifest.Id);
            var healthProbe = await CheckHealthAsync(_manager.GetPlugin(manifest.Id), state.Enabled, cancellationToken);
            plugins.Add(new
            {
                pluginId = manifest.Id,
                displayName = Localize($"plugin.name.{manifest.Id}", manifest.Name),
                version = manifest.Version,
                description = Localize($"plugin.desc.{manifest.Id}", manifest.Description),
                scopes = manifest.Scopes,
                capabilities = manifest.Capabilities,
                order = manifest.Order,
                resources = manifest.Resources,
                declaredResources = GetDeclaredResources(manifest.Resources),
                runtimeResources = actualResources ?? [],
                schemas = manifest.Schemas,
                dependencies = manifest.Dependencies,
                enabled = state.Enabled,
                isBuiltIn = state.IsBuiltIn,
                missingDependencies = state.MissingDependencies,
                bindingTargets = state.BindingTargets,
                bindingCount = state.BindingTargets.Count,
                health = healthProbe?.Status.ToString() ?? state.Health,
                healthProbe,
                registrationStatus = state.RegistrationStatus?.ToString(),
                registrationError = state.RegistrationError,
                unloadSupported = false
            });
        }

        return Ok(new { code = 200, data = plugins });
    }

    private static object[] GetDeclaredResources(PluginResourceRequirements resources)
    {
        var declared = new List<object>();
        if (resources.RequestMiddleware)
        {
            declared.Add(new { type = "RequestMiddleware" });
        }

        if (resources.BackgroundServices)
        {
            declared.Add(new { type = "BackgroundService" });
        }

        if (resources.Database)
        {
            declared.Add(new { type = "Database" });
        }

        if (resources.NetworkConnections)
        {
            declared.Add(new { type = "NetworkConnection" });
        }

        return declared.ToArray();
    }

    private static async Task<PluginHealthProbeResult?> CheckHealthAsync(
        IGatewayPlugin? plugin,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return new PluginHealthProbeResult(PluginHealthStatus.Disabled, null, DateTimeOffset.UtcNow);
        }

        if (plugin is not IPluginHealthProbe probe)
        {
            return null;
        }

        try
        {
            return await probe.CheckHealthAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PluginHealthProbeResult(PluginHealthStatus.Unhealthy, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Get a single plugin by ID.</summary>
    [HttpGet("{pluginId}")]
    public IActionResult GetPlugin(string pluginId)
    {
        var manifest = _manager.GetManifest(pluginId);
        if (manifest == null)
        {
            return NotFound(new { code = 404, message = $"Plugin '{pluginId}' not found" });
        }

        return Ok(new
        {
            code = 200,
            data = new
            {
                pluginId = manifest.Id,
                displayName = Localize($"plugin.name.{manifest.Id}", manifest.Name),
                version = manifest.Version,
                description = Localize($"plugin.desc.{manifest.Id}", manifest.Description),
                scopes = manifest.Scopes,
                capabilities = manifest.Capabilities,
                order = manifest.Order,
                resources = manifest.Resources,
                schemas = manifest.Schemas,
                enabled = _manager.IsPluginEnabled(pluginId)
            }
        });
    }

    /// <summary>Enable or disable a plugin.</summary>
    [HttpPost("{pluginId}/toggle")]
    public async Task<IActionResult> TogglePlugin(
        string pluginId,
        [FromBody] TogglePluginRequest request,
        CancellationToken cancellationToken)
    {
        var manifest = _manager.GetManifest(pluginId);
        if (manifest == null)
        {
            return NotFound(new { code = 404, message = $"Plugin '{pluginId}' not found" });
        }

        await TransitionGate.WaitAsync(cancellationToken);
        try
        {
            var previousEnabled = _manager.IsPluginEnabled(pluginId);
            if (previousEnabled == request.Enabled)
            {
                return Ok(new { code = 200, data = new { pluginId, enabled = previousEnabled, refreshed = false } });
            }

            var validation = _manager.ValidatePluginStateChange(pluginId, request.Enabled);
            if (!validation.Succeeded)
            {
                return MutationFailure(validation);
            }

            var targetEnabled = _manager.GetAllManifests()
                .Where(item => string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase)
                    ? request.Enabled
                    : _manager.IsPluginEnabled(item.Id))
                .Select(item => item.Id)
                .ToArray();
            await using var preparation = await _runtimeDomains.PrepareAsync(targetEnabled, cancellationToken);
            var health = await preparation.CheckHealthAsync(cancellationToken);
            if (health.Status == PluginHealthStatus.Unhealthy)
            {
                return Conflict(new { code = 409, outcome = "ValidationRejected", message = health.Message });
            }

            var persisted = _manager.SetPluginEnabled(pluginId, request.Enabled);
            if (!persisted.Succeeded)
            {
                return MutationFailure(persisted);
            }

            try
            {
                await preparation.CommitAsync(cancellationToken);
                await _resourceLifecycle.ReconcileAsync(cancellationToken);
                await _dynamicConfig.RefreshConfigAsync(CancellationToken.None);
            }
            catch
            {
                _manager.SetPluginEnabled(pluginId, previousEnabled);
                await _runtimeDomains.TransitionAsync(GetEnabledPluginIds(), CancellationToken.None);
                await _resourceLifecycle.ReconcileAsync(CancellationToken.None);
                throw;
            }

            return Ok(new { code = 200, data = new { pluginId, enabled = request.Enabled, refreshed = true } });
        }
        finally
        {
            TransitionGate.Release();
        }
    }

    /// <summary>Reset all plugins to enabled state.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetPlugins(CancellationToken cancellationToken)
    {
        await TransitionGate.WaitAsync(cancellationToken);
        try
        {
            var manifests = _manager.GetAllManifests();
            var previous = manifests.ToDictionary(item => item.Id, item => _manager.IsPluginEnabled(item.Id), StringComparer.OrdinalIgnoreCase);
            var targetEnabled = manifests.Select(item => item.Id).ToArray();
            await using var preparation = await _runtimeDomains.PrepareAsync(targetEnabled, cancellationToken);
            var health = await preparation.CheckHealthAsync(cancellationToken);
            if (health.Status == PluginHealthStatus.Unhealthy)
            {
                return Conflict(new { code = 409, outcome = "ValidationRejected", message = health.Message });
            }

            foreach (var manifest in manifests.Where(item => !previous[item.Id]))
            {
                var result = _manager.SetPluginEnabled(manifest.Id, true);
                if (result.Succeeded)
                {
                    continue;
                }

                RestorePluginStates(previous);
                return MutationFailure(result);
            }

            try
            {
                await preparation.CommitAsync(cancellationToken);
                await _resourceLifecycle.ReconcileAsync(cancellationToken);
                await _dynamicConfig.RefreshConfigAsync(CancellationToken.None);
            }
            catch
            {
                RestorePluginStates(previous);
                await _runtimeDomains.TransitionAsync(GetEnabledPluginIds(), CancellationToken.None);
                await _resourceLifecycle.ReconcileAsync(CancellationToken.None);
                throw;
            }

            return Ok(new { code = 200, message = "All plugins enabled" });
        }
        finally
        {
            TransitionGate.Release();
        }
    }

    /// <summary>Install an external plugin from a source directory.</summary>
    [HttpPost("install")]
    public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.SourceDirectory))
        {
            return BadRequest(new { code = 400, message = "SourceDirectory is required." });
        }

        var result = _manager.InstallPlugin(request.SourceDirectory);
        if (!result.Succeeded)
        {
            return MutationFailure(result);
        }

        await _dynamicConfig.RefreshConfigAsync(CancellationToken.None);
        return Ok(new { code = 200, message = "Plugin installed successfully" });
    }

    /// <summary>Uninstall an external plugin (must be disabled and unbound).</summary>
    [HttpDelete("{pluginId}")]
    public async Task<IActionResult> UninstallPlugin(string pluginId)
    {
        var result = _manager.UninstallPlugin(pluginId);
        if (!result.Succeeded)
        {
            return MutationFailure(result);
        }

        await _dynamicConfig.RefreshConfigAsync(CancellationToken.None);
        return Ok(new { code = 200, message = "Plugin uninstalled successfully" });
    }

    /// <summary>Upgrade an external plugin from a source directory.</summary>
    [HttpPost("{pluginId}/upgrade")]
    public async Task<IActionResult> UpgradePlugin(string pluginId, [FromBody] InstallPluginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.SourceDirectory))
        {
            return BadRequest(new { code = 400, message = "SourceDirectory is required." });
        }

        var result = _manager.UpgradePlugin(pluginId, request.SourceDirectory);
        if (!result.Succeeded)
        {
            return MutationFailure(result);
        }

        await _dynamicConfig.RefreshConfigAsync(CancellationToken.None);
        return Ok(new { code = 200, message = "Plugin upgraded successfully" });
    }

    private ObjectResult MutationFailure(PluginStateChangeResult result)
    {
        var code = result.Status == PluginStateChangeStatus.StorageFailed ? 503 : 409;
        return StatusCode(code, new { code, outcome = result.Status.ToString(), message = result.Error });
    }

    private string[] GetEnabledPluginIds() => _manager.GetAllManifests()
        .Where(item => _manager.IsPluginEnabled(item.Id))
        .Select(item => item.Id)
        .ToArray();

    private void RestorePluginStates(IReadOnlyDictionary<string, bool> previous)
    {
        foreach (var (pluginId, enabled) in previous)
        {
            if (_manager.IsPluginEnabled(pluginId) != enabled)
            {
                _manager.SetPluginEnabled(pluginId, enabled);
            }
        }
    }
}

public class TogglePluginRequest
{
    [System.Text.Json.Serialization.JsonRequired]
    public bool Enabled { get; set; }
}

public class InstallPluginRequest
{
    public string? SourceDirectory { get; set; }
}
