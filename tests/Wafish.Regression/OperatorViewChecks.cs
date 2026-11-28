using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Infrastructure.Auth;
using Wafish.Storage;
using Wafish.Storage.Sqlite;

internal static class OperatorViewChecks
{
    private const string OwnerA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OwnerB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static OperatorView View(string workspace = "routes", string name = "Errors") => new(Guid.NewGuid(), workspace, name,
        new("needle", "failure", "route-one", 400, true, ["routeId", "statusCode"]), true, 0);
    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        await run("Operator views: owner scope, persistence, default selection, revisions, and rollback are atomic", async () =>
        {
            await using var db = new StorageRepositoryChecks.Database(); var repository = db.Get<IOperatorViewRepository>();
            var original = View(); var saved = await repository.SaveAsync(OwnerA, original, 0);
            Check(saved is { Outcome: OperatorViewWriteOutcome.Saved, View.Revision: 1 }, "Initial save failed.");
            Check((await repository.ListAsync(OwnerB, "routes")).Count == 0 && (await repository.ListAsync(OwnerA, "logs")).Count == 0, "Preferences crossed owner or workspace scope.");
            var reopened = new SqliteOperatorViewRepository(db.Get<SqliteConnectionFactory>());
            var loaded = (await reopened.ListAsync(OwnerA, "routes")).Single();
            Check(loaded.Id == original.Id && loaded.Settings.Columns.SequenceEqual(original.Settings.Columns) && loaded.Settings.Filter == original.Settings.Filter, "Stored preferences did not round-trip.");
            Check((await repository.SaveAsync(OwnerA, View(), 0)).Outcome == OperatorViewWriteOutcome.NameConflict, "Duplicate view names are ambiguous.");
            var second = View(name: "Other"); await repository.SaveAsync(OwnerA, second, 0);
            var views = await repository.ListAsync(OwnerA, "routes");
            Check(views.Count == 2 && views.Single(item => item.IsDefault).Id == second.Id && views.Single(item => item.Id == original.Id).Revision == 2, "Default selection was not atomic or versioned.");
            Check((await repository.SaveAsync(OwnerA, original, 1)).Outcome == OperatorViewWriteOutcome.Conflict, "Stale save overwrote a newer revision.");
            Check((await repository.DeleteAsync(OwnerB, "routes", second.Id, 1)).Outcome == OperatorViewWriteOutcome.NotFound, "Foreign delete found private state.");
            Check((await repository.DeleteAsync(OwnerA, "routes", original.Id, 1)).Outcome == OperatorViewWriteOutcome.Conflict, "Stale delete succeeded.");
            await using var connection = await db.Get<SqliteConnectionFactory>().CreateConnectionAsync(); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_view BEFORE INSERT ON operator_views WHEN NEW.name = 'fail' BEGIN SELECT RAISE(ABORT, 'fixture'); END"; await command.ExecuteNonQueryAsync();
            try { await repository.SaveAsync(OwnerA, View(name: "fail"), 0); throw new Exception("Failed persistence succeeded."); } catch (SqliteException) { }
            Check((await repository.ListAsync(OwnerA, "routes")).Single(item => item.IsDefault).Id == second.Id, "Failed save cleared the previous default.");
            var contenders = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => repository.SaveAsync(OwnerA, second with { Name = "Concurrent" }, 1))));
            Check(contenders.Count(item => item.Outcome == OperatorViewWriteOutcome.Saved) == 1 && contenders.Count(item => item.Outcome == OperatorViewWriteOutcome.Conflict) == 7, "Concurrent updates lost revision protection.");
            Check((await repository.DeleteAsync(OwnerA, "routes", original.Id, 2)).Outcome == OperatorViewWriteOutcome.Deleted, "Owned deletion failed.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await repository.SaveAsync(OwnerA, View(), 0, cancellation.Token); throw new Exception("Cancelled save succeeded."); } catch (OperationCanceledException) { }
        });
        await run("Operator views: validation and storage bounds prevent unbounded private state", async () =>
        {
            await using var db = new StorageRepositoryChecks.Database(); var repository = db.Get<IOperatorViewRepository>();
            var view = View();
            foreach (var invalid in new[] { view with { Id = Guid.Empty }, view with { Name = " " }, view with { Name = new string('a', 65) }, view with { Workspace = "../escape" },
                view with { Settings = view.Settings with { Search = new string('s', 257) } }, view with { Settings = view.Settings with { Filter = "\n" } },
                view with { Settings = view.Settings with { Columns = [] } }, view with { Settings = view.Settings with { Columns = ["same", "same"] } },
                view with { Settings = view.Settings with { Columns = ["bad/key"] } }, view with { Settings = view.Settings with { MinimumStatus = 600 } } })
            { Check(!OperatorViewRules.IsValid(invalid), "Invalid preferences passed validation."); try { await repository.SaveAsync(OwnerA, invalid, 0); throw new Exception("Invalid save succeeded."); } catch (ArgumentException) { } }
            try { await repository.ListAsync("credential-value", "routes"); throw new Exception("Raw identity accepted."); } catch (ArgumentException) { }
            for (var i = 0; i < OperatorViewRules.MaximumPerOwner; i++) Check((await repository.SaveAsync(OwnerA, View(name: "View " + i), 0)).Outcome == OperatorViewWriteOutcome.Saved, "Owner capacity reached early.");
            Check((await repository.SaveAsync(OwnerA, View("logs"), 0)).Outcome == OperatorViewWriteOutcome.Capacity, "Owner capacity could be bypassed with another workspace.");
            var current = (await repository.ListAsync(OwnerA, "routes")).First();
            Check((await repository.SaveAsync(OwnerA, current with { Name = "Updated" }, current.Revision)).Outcome == OperatorViewWriteOutcome.Saved, "Capacity prevented an existing update.");
            await using var connection = await db.Get<SqliteConnectionFactory>().CreateConnectionAsync(); await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n < 4046)
                INSERT INTO operator_views(owner,workspace,id,name,settings_json,is_default,revision)
                SELECT printf('%064x',100+n/50),'capacity',printf('%032x',n),'Bounded ' || n,@settings,0,1 FROM numbers
                """;
            command.Parameters.AddWithValue("@settings", JsonSerializer.Serialize(view.Settings)); await command.ExecuteNonQueryAsync();
            Check((await repository.SaveAsync(new string('C', 64), View(), 0)).Outcome == OperatorViewWriteOutcome.Capacity, "Global capacity failed.");
        });
        await run("Operator views: authenticated HTTP writes enforce private ownership, read permission, revisions, and origin checks", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start(services => services.Configure<DashboardOptions>(options =>
            { options.Auth.AuthMode = DashboardAuthMode.BasicAuth; options.Auth.JwtUsername = "alice"; options.Auth.JwtPassword = "view-test-secret"; options.Auth.Permissions = [DashboardPermission.Read]; }));
            var client = fixture.Client; client.DefaultRequestHeaders.Authorization = Basic("alice");
            var view = View();
            using (var missing = await Write(client, view, 0, acknowledge: false)) Check(missing.StatusCode == HttpStatusCode.Forbidden, "Write lacked an explicit request marker.");
            using (var cross = await Write(client, view, 0, origin: "https://other.example")) Check(cross.StatusCode == HttpStatusCode.Forbidden, "Cross-origin write succeeded.");
            using (var saved = await Write(client, view, 0, origin: client.BaseAddress!.GetLeftPart(UriPartial.Authority))) Check(saved.StatusCode == HttpStatusCode.OK, "Read-only account could not save its own preferences.");
            Check((await Read(client)).Single().Id == view.Id, "Owned view was not restored.");
            using (var duplicateName = await Write(client, view with { Id = Guid.NewGuid() }, 0))
            {
                Check(duplicateName.StatusCode == HttpStatusCode.Conflict, "Duplicate name was accepted.");
                using var document = JsonDocument.Parse(await duplicateName.Content.ReadAsStringAsync());
                Check(document.RootElement.GetProperty("outcome").GetString() == "NameConflict", "Duplicate-name outcome is ambiguous.");
            }
            using (var stale = await Write(client, view with { Name = "Stale" }, 0)) Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale HTTP save overwrote preferences.");
            using (var invalid = await Write(client, view with { Settings = view.Settings with { MinimumStatus = 99 } }, 1)) Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid filters accepted.");
            using (var raw = new HttpRequestMessage(HttpMethod.Put, "/ops/api/operator-views/routes/" + view.Id) { Content = JsonContent.Create(new { name = "Secrets", owner = OwnerB, settings = view.Settings, expectedRevision = 1 }) })
            { raw.Headers.Add("X-Wafish-Operator-View", "1"); using var response = await client.SendAsync(raw); Check(response.StatusCode == HttpStatusCode.BadRequest, "Caller could supply an owner."); }
            var options = fixture.App.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
            options.Auth.JwtUsername = "bob"; client.DefaultRequestHeaders.Authorization = Basic("bob");
            Check((await Read(client)).Count == 0, "Different account inherited private views.");
            using (var foreign = await Delete(client, view.Id, 1)) Check(foreign.StatusCode == HttpStatusCode.NotFound, "Foreign deletion crossed ownership.");
            options.Auth.JwtUsername = "alice"; client.DefaultRequestHeaders.Authorization = Basic("alice");
            Check((await Read(client)).Single().Name == view.Name, "Other account changed private preferences.");
            using (var deleted = await Delete(client, view.Id, 1)) Check(deleted.StatusCode == HttpStatusCode.OK, "Owned HTTP deletion failed.");
            client.DefaultRequestHeaders.Authorization = null;
            using (var anonymous = await client.GetAsync("/ops/api/operator-views/routes")) Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Anonymous caller accessed preferences.");
        });
        await run("Operator views: JWT sign-out and renewed sign-in preserve account preferences without session credentials", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start(services => services.Configure<DashboardOptions>(options =>
            { options.Auth.AuthMode = DashboardAuthMode.CustomJwt; options.Auth.JwtUsername = "alice"; options.Auth.JwtPassword = "view-test-secret"; options.Auth.JwtSecret = new string('v', 64); }));
            var client = fixture.Client;
            async Task<string> Login()
            {
                using var response = await client.PostAsJsonAsync("/ops/login", new { username = "alice", password = "view-test-secret" });
                response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.GetProperty("token").GetString()!;
            }
            var token = await Login(); client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var view = View(); using (var saved = await Write(client, view, 0)) Check(saved.StatusCode == HttpStatusCode.OK, "JWT account could not save its preferences.");
            using (var logout = await client.PostAsync("/ops/logout", null)) logout.EnsureSuccessStatusCode();
            using (var revoked = await client.GetAsync("/ops/api/operator-views/routes")) Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "Revoked session read private views.");
            var renewed = await Login(); Check(renewed != token, "Fixture did not create a new session."); client.DefaultRequestHeaders.Authorization = new("Bearer", renewed);
            Check((await Read(client)).Single().Id == view.Id, "Renewed sign-in lost account preferences.");
            await using var connection = await fixture.App.Services.GetRequiredService<SqliteConnectionFactory>().CreateConnectionAsync(); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT owner || name || settings_json FROM operator_views";
            var persisted = (string)(await command.ExecuteScalarAsync())!;
            Check(!persisted.Contains("alice", StringComparison.Ordinal) && !persisted.Contains(token, StringComparison.Ordinal) && !persisted.Contains(renewed, StringComparison.Ordinal) && !persisted.Contains("view-test-secret", StringComparison.Ordinal), "Preference storage retained account or session credentials.");
        });
        await run("Operator views: shared credentials, unsupported providers, and ambiguous custom identities do not share preferences", async () =>
        {
            await using var fixture = await RedirectChecks.Fixture.Start(); var client = fixture.Client;
            Check(!await Available(client), "Shared API key received a private owner.");
            using (var rejected = await Write(client, View(), 0)) Check(rejected.StatusCode == HttpStatusCode.Forbidden, "Shared API key persisted a private view.");
            var options = fixture.App.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
            var subject = "alice"; var issuer = "trusted-one"; var ambiguous = false;
            options.Auth.AuthorizeRequest = context =>
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer), new(ClaimTypes.Name, "same display name") };
                if (ambiguous) claims.Add(new("sub", subject));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "trusted-host"));
                return Task.FromResult(true);
            };
            using (var saved = await Write(client, View(), 0)) Check(saved.StatusCode == HttpStatusCode.OK, "Verified custom subject could not save.");
            subject = "bob"; Check((await Read(client)).Count == 0, "Display name replaced subject ownership.");
            subject = "alice"; issuer = "trusted-two"; Check((await Read(client)).Count == 0, "Different issuer inherited a subject's views.");
            issuer = "trusted-one"; Check((await Read(client)).Count == 1, "Stable custom identity lost its view.");
            ambiguous = true; Check(!await Available(client), "Ambiguous subject claims selected an owner.");
            options.Auth.AuthorizeRequest = _ => Task.FromResult(true); Check(!await Available(client), "A bare authorization delegate created a shared owner.");
            await using var unsupported = await RedirectChecks.Fixture.Start(services =>
            {
                services.RemoveAll<IOperatorViewRepository>();
                services.Configure<DashboardOptions>(value => { value.Auth.AuthMode = DashboardAuthMode.BasicAuth; value.Auth.JwtUsername = "alice"; value.Auth.JwtPassword = "view-test-secret"; });
            });
            unsupported.Client.DefaultRequestHeaders.Authorization = Basic("alice");
            Check(!await Available(unsupported.Client), "Missing optional provider reported support.");
            using var unavailable = await Write(unsupported.Client, View(), 0);
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Missing provider silently persisted views.");
        });
    }
    private static AuthenticationHeaderValue Basic(string user) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":view-test-secret")));
    private static async Task<List<OperatorView>> Read(HttpClient client)
    {
        using var response = await client.GetAsync("/ops/api/operator-views/routes"); response.EnsureSuccessStatusCode();
        Check(response.Headers.CacheControl?.NoStore == true, "Private preferences permit response caching.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("views").Deserialize<List<OperatorView>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private static async Task<bool> Available(HttpClient client)
    { using var response = await client.GetAsync("/ops/api/operator-views/routes"); response.EnsureSuccessStatusCode(); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.GetProperty("data").GetProperty("available").GetBoolean(); }
    private static async Task<HttpResponseMessage> Write(HttpClient client, OperatorView view, long revision, bool acknowledge = true, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/ops/api/operator-views/" + view.Workspace + "/" + view.Id)
        { Content = JsonContent.Create(new { view.Name, view.Settings, view.IsDefault, expectedRevision = revision }) };
        if (acknowledge) request.Headers.Add("X-Wafish-Operator-View", "1"); if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> Delete(HttpClient client, Guid id, long revision)
    { using var request = new HttpRequestMessage(HttpMethod.Delete, "/ops/api/operator-views/routes/" + id + "?expectedRevision=" + revision); request.Headers.Add("X-Wafish-Operator-View", "1"); return await client.SendAsync(request); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
