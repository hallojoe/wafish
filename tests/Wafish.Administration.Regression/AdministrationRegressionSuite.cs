using Wafish.Runtime;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Plugin.ProxyLog.Models;
using Wafish.Plugin.ProxyLog.Services;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Wafish.Administration;
using Wafish.Dashboard.Infrastructure.I18n;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed partial class AdministrationRegressionSuite : IDisposable
{
    private readonly RegressionReport report = new("administration");
    private readonly List<string> failures = [];
    private readonly string path = Path.Combine(Path.GetTempPath(), "wafish-administration-" + Guid.NewGuid().ToString("N"));
    private readonly string key = Guid.NewGuid().ToString("N");
    public void Dispose() => report.Dispose();

    public async Task<int> RunAsync(string[] args)
    {
        Directory.CreateDirectory(path);
        try
        {
            if (args.Length == 0 || args is ["--need-seven"])
                await NeedSevenEndpointChecks.RunAsync(Check, configure => StartHost("ApiKey", configure: configure, registration: false), key);
            if (args is ["--need-seven"]) return failures.Count == 0 ? 0 : 1;
            await OperationPermissionChecks.RunAsync(Check, configure => StartHost("ApiKey", enableAi: true, configure: configure, registration: false), key);
            if (args is ["--permissions"])
            {
                foreach (var failure in failures) Console.Error.WriteLine(failure);
                return failures.Count == 0 ? 0 : 1;
            }
            await PreviewEndpointChecks.RunAsync(Check, () => StartHost("ApiKey", registration: false), key);
            if (args is ["--previews"])
            {
                foreach (var failure in failures) Console.Error.WriteLine(failure);
                return failures.Count == 0 ? 0 : 1;
            }
            await AuthorizationMatrixChecks.RunAsync(Check, configure => StartHost("ApiKey", enableAi: true, configure: configure, registration: false), key);
            if (args is ["--authorization-matrix"])
            {
                foreach (var failure in failures) Console.Error.WriteLine(failure);
                return failures.Count == 0 ? 0 : 1;
            }
            await MutationContractChecks.RunAsync(Check, () => StartHost("ApiKey", enableAi: true), key);
            await ShutdownChecks.RunAsync(Check, configure => StartHost("ApiKey", configure: configure), key);
            await ResourceCleanupChecks.RunAsync(Check, configure => StartHost("ApiKey", configure: configure), key);
            await CheckStorageBoundsAsync();
            await SessionLifecycleChecks.RunAsync(Check, (mode, configure) => StartHost(mode, configure: configure), key);
            if (args is ["--sessions"])
            {
                foreach (var failure in failures) Console.Error.WriteLine(failure);
                return failures.Count == 0 ? 0 : 1;
            }
            await AuthenticationSecurityChecks.RunAsync(Check, (mode, certificate) => StartHost(mode, certificate: certificate), key);
            await CheckDeploymentAsync();
            await CheckTwoFactorPersistenceAsync();
            await CheckApiKeyHostAsync();
            await CheckJwtHostAsync();
            await CheckOptionalFeaturesAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(path, true);
        }
        
        if (failures.Count == 0)
        {
            Console.WriteLine("All administration checks passed.");
        }
        else
        {
            foreach (var failure in failures)
            {
                Console.Error.WriteLine(failure);
            }
        }
        
        return failures.Count == 0 ? 0 : 1;
    }
}
