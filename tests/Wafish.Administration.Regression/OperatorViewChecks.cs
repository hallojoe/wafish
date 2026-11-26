using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;

internal static class OperatorViewChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> run, Func<string, Action<IServiceCollection>, Task<WebApplication>> start, string password)
    {
        await run("Operator views: private accounts retain filters and columns across sign-in and protected storage reload", async () =>
        {
            var aliceToken = Guid.NewGuid().ToString("N"); var bobToken = Guid.NewGuid().ToString("N");
            await using var host = await start("ApiKey", services => services.Configure<DashboardOptions>(options =>
            {
                options.OperatorViewsStatePath = Path.Combine(Path.GetDirectoryName(options.Auth.TwoFactorStatePath)!, "operator-view-state", "views.dat");
                options.Auth.Permissions = [DashboardPermission.Read];
                options.Auth.AuthorizeRequest = context =>
                {
                    var token = context.Request.Headers.Authorization.ToString();
                    var user = token == "Bearer " + aliceToken ? "alice" : token == "Bearer " + bobToken ? "bob" : null;
                    if (user is null) return Task.FromResult(false);
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user, ClaimValueTypes.String, "trusted-test")], "test"));
                    return Task.FromResult(true);
                };
            }));
            using var alice = new HttpClient { BaseAddress = new(host.Urls.Single()) }; alice.DefaultRequestHeaders.Authorization = new("Bearer", aliceToken);
            using var bob = new HttpClient { BaseAddress = alice.BaseAddress }; bob.DefaultRequestHeaders.Authorization = new("Bearer", bobToken);
            using var anonymous = new HttpClient { BaseAddress = alice.BaseAddress };
            Require((await anonymous.GetAsync("/ops/api/operator-views")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous private listing succeeded.");
            var request = new OperatorViewRequest("Investigate errors", "logs", new(true, "synthetic", "orders", 500), ["timestamp", "routeId", "statusCode"]);
            using var created = await alice.PostAsJsonAsync("/ops/api/operator-views", request);
            Require(created.StatusCode == HttpStatusCode.Created && created.Headers.Location is not null, "Saved view lacks a created location.");
            var location = created.Headers.Location!;
            using var descriptor = await alice.GetAsync(location); descriptor.EnsureSuccessStatusCode();
            var initial = await descriptor.Content.ReadFromJsonAsync<JsonElement>();
            Require(initial.GetProperty("data").GetProperty("filters").GetProperty("minimumStatus").GetInt32() == 500, "Filters changed during persistence.");
            Require(descriptor.Headers.CacheControl?.NoStore == true, "Private views can be cached.");
            var b = await bob.GetFromJsonAsync<JsonElement>("/ops/api/operator-views");
            Require(b.GetProperty("data").GetProperty("views").GetArrayLength() == 0, "Another account received private views.");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
            {
                using var message = new HttpRequestMessage(method, location); if (method == HttpMethod.Put) message.Content = JsonContent.Create(request);
                using var denied = await bob.SendAsync(message); Require(denied.StatusCode == HttpStatusCode.NotFound, "Foreign view identifier escaped ownership.");
            }
            using (var duplicate = await alice.PostAsJsonAsync("/ops/api/operator-views", request)) Require(duplicate.StatusCode == HttpStatusCode.Conflict, "Duplicate view name was accepted.");
            using (var invalid = await alice.PutAsJsonAsync(location, request with { Columns = ["authorization"] })) Require(invalid.StatusCode == HttpStatusCode.BadRequest, "Unknown column was accepted.");
            using (var injected = await alice.PostAsJsonAsync("/ops/api/operator-views", new { request.Name, request.Workspace, request.Filters, request.Columns, owner = "bob" })) Require(injected.StatusCode == HttpStatusCode.BadRequest, "Caller ownership field was accepted.");
            var updated = request with { Name = "Errors", Columns = ["statusCode"] };
            using (var saved = await alice.PutAsJsonAsync(location, updated)) saved.EnsureSuccessStatusCode();
            var options = host.Services.GetRequiredService<IOptions<DashboardOptions>>();
            var path = options.Value.OperatorViewsStatePath!;
            var bytes = File.ReadAllBytes(path);
            Require(!Encoding.UTF8.GetString(bytes).Contains("synthetic", StringComparison.Ordinal), "Preferences were stored without protection.");
            if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Preference file permissions are too broad.");
            var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "trusted-test")], "test")) };
            var restored = new OperatorViewStore(options, host.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
            var owner = restored.Owner(context)!;
            Require((await restored.ListAsync(owner, default)).Single().Name == "Errors", "Store reload lost the user's view.");
            var additions = Enumerable.Range(0, 19).Select(index => alice.PostAsJsonAsync("/ops/api/operator-views", request with { Name = "View " + index })).ToArray();
            foreach (var response in await Task.WhenAll(additions)) { using (response) response.EnsureSuccessStatusCode(); }
            using (var full = await alice.PostAsJsonAsync("/ops/api/operator-views", request with { Name = "Overflow" })) Require(full.StatusCode == HttpStatusCode.Conflict, "Account view limit was not enforced.");
            using (var independent = await bob.PostAsJsonAsync("/ops/api/operator-views", request)) independent.EnsureSuccessStatusCode();
            bytes = File.ReadAllBytes(path);
            if (!OperatingSystem.IsWindows())
            {
                var parent = Path.GetDirectoryName(path)!; var permissions = File.GetUnixFileMode(parent);
                try
                {
                    File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    using var failedWrite = await alice.PutAsJsonAsync(location, request);
                    Require(failedWrite.StatusCode == HttpStatusCode.ServiceUnavailable && File.ReadAllBytes(path).SequenceEqual(bytes), "Failed persistence changed the saved view.");
                }
                finally { File.SetUnixFileMode(parent, permissions); }
            }
            File.WriteAllText(path, "corrupt");
            using (var corrupt = await alice.GetAsync("/ops/api/operator-views")) Require(corrupt.StatusCode == HttpStatusCode.ServiceUnavailable, "Corrupt preferences did not fail closed.");
            using (var failedWrite = await alice.PostAsJsonAsync("/ops/api/operator-views", request with { Name = "Uncommitted" })) Require(failedWrite.StatusCode == HttpStatusCode.ServiceUnavailable && File.ReadAllText(path) == "corrupt", "Corrupt state was overwritten.");
            File.WriteAllBytes(path, bytes); File.Move(path, path + ".good"); Directory.CreateDirectory(path);
            using (var unavailable = await alice.DeleteAsync(location)) Require(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Unavailable preference storage was ignored.");
            Directory.Delete(path); File.Move(path + ".good", path);
            using (var deleted = await alice.DeleteAsync(location)) Require(deleted.StatusCode == HttpStatusCode.NoContent, "Owned view deletion failed.");
            using (var missing = await alice.GetAsync(location)) Require(missing.StatusCode == HttpStatusCode.NotFound, "Deleted view remained readable.");
            using (var missing = await alice.PutAsJsonAsync(location, request)) Require(missing.StatusCode == HttpStatusCode.NotFound, "Deleted view was recreated by update.");
            using (var missing = await alice.DeleteAsync(location)) Require(missing.StatusCode == HttpStatusCode.NotFound, "Missing delete succeeded.");
            options.Value.Auth.Permissions = [];
            Require((await alice.GetAsync("/ops/api/operator-views")).StatusCode == HttpStatusCode.Forbidden, "Permission denial was bypassed.");
        });
        await run("Operator views: JWT sign-in restores preferences and anonymous hosts cannot own private state", async () =>
        {
            await using var host = await start("DefaultJwt", _ => { });
            using var client = new HttpClient { BaseAddress = new(host.Urls.Single()) };
            async Task Login()
            {
                using var login = await client.PostAsJsonAsync("/ops/login", new { username = "admin", password }); login.EnsureSuccessStatusCode();
                var json = await login.Content.ReadFromJsonAsync<JsonElement>(); client.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString());
            }
            await Login();
            var request = new OperatorViewRequest("Audit", "audit", new(Search: "Apply"), ["timestamp", "action"]);
            using (var saved = await client.PostAsJsonAsync("/ops/api/operator-views", request)) saved.EnsureSuccessStatusCode();
            using (var logout = await client.PostAsync("/ops/logout", null)) logout.EnsureSuccessStatusCode();
            Require((await client.GetAsync("/ops/api/operator-views")).StatusCode == HttpStatusCode.Unauthorized, "Revoked sign-in retained private access.");
            await Login();
            var list = await client.GetFromJsonAsync<JsonElement>("/ops/api/operator-views"); Require(list.GetProperty("data").GetProperty("views").GetArrayLength() == 1, "New sign-in lost saved views.");
            var options = host.Services.GetRequiredService<IOptions<DashboardOptions>>(); options.Value.Auth.AuthMode = DashboardAuthMode.None;
            var store = host.Services.GetRequiredService<OperatorViewStore>(); Require(store.Owner(new DefaultHttpContext()) is null, "Anonymous mode created an owner.");
            options.Value.Auth.AuthorizeRequest = _ => Task.FromResult(true);
            Require(store.Owner(new DefaultHttpContext()) is null, "Unidentified custom caller received a shared private owner.");
            foreach (var invalid in new[] { request with { Name = " " }, request with { Name = new string('x', 65) }, request with { Workspace = "unknown" },
                request with { Columns = [] }, request with { Columns = ["timestamp", "timestamp"] }, request with { Filters = new(Historical: true) },
                request with { Filters = new(Search: new string('x', 201)) }, request with { Filters = new(MinimumStatus: 99) } })
                Require(!OperatorViewStore.Valid(invalid), "Invalid preference bounds passed.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await store.SaveAsync(new string('A', 64), null, request, cancelled.Token); throw new Exception("Cancelled write succeeded."); } catch (OperationCanceledException) { }
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
