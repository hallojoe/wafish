using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Deployment;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Modules.Dashboard.Controllers;

/// <summary>System information and database downloads shared by administration clients.</summary>
public class DashboardSettingsController : Controller
{
    private readonly IDashboardInfoQueryService _infoQuery;
    private readonly IDbConnectionFactory _connections;
    private readonly StorageOptions _storageOptions;
    private readonly DeploymentOptions _deploymentOptions;
    private readonly string _defaultLocale;
    private readonly string _authMode;
    private readonly string RoutePrefix;

    public DashboardSettingsController(IDashboardInfoQueryService infoQuery,
        IOptions<DashboardOptions> dashboardOptions, IOptions<StorageOptions> storageOptions,
        IOptions<DeploymentOptions> deploymentOptions, IDbConnectionFactory connections)
    {
        _infoQuery = infoQuery;
        _connections = connections;
        _storageOptions = storageOptions.Value;
        _deploymentOptions = deploymentOptions.Value;
        _defaultLocale = dashboardOptions.Value.Locale;
        _authMode = dashboardOptions.Value.Auth.AuthMode.ToString();
        RoutePrefix = dashboardOptions.Value.RoutePrefix.Trim('/');
    }

    #region DB Download

    /// <summary>System information shown on the settings page.</summary>
    [HttpGet("api/settings/system")]
    public IActionResult GetSystemInfo()
    {
        var dbPath = ResolveDatabasePath(_storageOptions);
        long dbSize = 0;
        if (!string.IsNullOrEmpty(dbPath) && System.IO.File.Exists(dbPath))
        {
            try { dbSize = new FileInfo(dbPath).Length; }
            catch { /* ignore */ }
        }

        return Json(new
        {
            code = 200,
            data = new
            {
                version = _infoQuery.GetInfo().Version,
                routePrefix = RoutePrefix,
                authMode = _authMode,
                locale = _defaultLocale,
                databaseFile = dbPath,
                databaseSizeBytes = dbSize,
                deployment = new
                {
                    mode = _deploymentOptions.Mode.ToString(),
                    autoMiddleware = _deploymentOptions.AutoUseMiddleware,
                    requireLoopbackAdmin = _deploymentOptions.RequireLoopbackForAdmin,
                    requireLoopbackDashboard = _deploymentOptions.RequireLoopbackForDashboard,
                    endpoints = _deploymentOptions.ResolvedEndpoints.Select(e => new
                    {
                        name = e.EndpointName,
                        port = e.Port,
                        address = e.IpAddress,
                        role = e.Role,
                        isPublic = e.IsPubliclyBound
                    }).ToList(),
                    healthCheck = new
                    {
                        enabled = _deploymentOptions.HealthCheck.Enabled,
                        path = _deploymentOptions.HealthCheck.Path,
                        readyPath = _deploymentOptions.HealthCheck.ReadyPath,
                        livePath = _deploymentOptions.HealthCheck.LivePath,
                        checkDatabase = _deploymentOptions.HealthCheck.CheckDatabase,
                        checkConfigLoaded = _deploymentOptions.HealthCheck.CheckConfigLoaded
                    }
                }
            }
        });
    }

    /// <summary>Download the SQLite database file for local inspection.</summary>
    [HttpGet("api/settings/database")]
    public IActionResult DownloadDatabase()
    {
        var dbPath = ResolveDatabasePath(_storageOptions);
        if (!System.IO.File.Exists(dbPath))
        {
            return Json(new { code = 404, message = "Database file not found" });
        }

        var fileName = Path.GetFileName(dbPath);
        return PhysicalFile(dbPath, "application/octet-stream", fileName);
    }

    /// <summary>Create a consistent snapshot backup via VACUUM INTO and stream for download.</summary>
    [HttpGet("api/settings/database/backup")]
    public async Task<IActionResult> BackupDatabase()
    {
        var provider = HttpContext.RequestServices.GetService<IStorageBackupProvider>();
        if (provider is null) return StatusCode(501, new { code = 501, message = "Storage backup is unsupported by this provider." });
        try
        {
            var backup = await provider.CreateBackupAsync(HttpContext.RequestAborted);
            Response.RegisterForDisposeAsync(backup);
            return File(backup.Content, "application/octet-stream", backup.FileName);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch { return StatusCode(503, new { code = 503, message = "Database backup failed. No backup was created." }); }
    }

    /// <summary>Resolves the SQLite database file path from the connection string.</summary>
    private static string ResolveDatabasePath(StorageOptions storageOptions)
    {
        var cs = storageOptions.Sqlite.ConnectionString;
        const string prefix = "Data Source=";

        int idx = cs.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return string.Empty;
        }

        var value = cs[(idx + prefix.Length)..].Trim();

        int semi = value.IndexOf(';');
        if (semi >= 0)
        {
            value = value[..semi].Trim();
        }

        if (!Path.IsPathRooted(value))
        {
            var rooted = Path.GetFullPath(value);
            if (System.IO.File.Exists(rooted))
            {
                return rooted;
            }
        }

        return value;
    }

    #endregion
}
