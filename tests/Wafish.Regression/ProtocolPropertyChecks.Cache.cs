using System.Globalization;
using Microsoft.AspNetCore.Http;

internal static partial class ProtocolPropertyChecks
{
    private static async Task CacheAsync()
    {
        var count = 0;
        (string Header, int Freshness, bool Public)[] controls = [("public, max-age=30", 30, true), ("public, max-age=30, s-maxage=5", 5, true), ("public, max-age=0", 0, true), ("private, max-age=30", 30, false), ("public, no-store, max-age=30", 30, false), ("public, no-cache, max-age=30", 30, false), ("public, max-age=600", 600, true), ("broken=\"", 0, false)];
        foreach (var control in controls)
        foreach (var age in new[] { 0, 7, 40 })
        foreach (var elapsed in new[] { 0, 5, 30, 60 })
        foreach (var directive in new[] { "", "max-age=5", "min-fresh=8", "no-cache", "no-store", "only-if-cached", "no-cache, only-if-cached" })
        {
            var fixture = new Fixture { ResponseHeaders = context => { context.Response.Headers.CacheControl = control.Header; context.Response.Headers.Age = age.ToString(CultureInfo.InvariantCulture); } };
            var first = await fixture.Send(); await first.Response.Body.DisposeAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(elapsed));
            var lifetime = Math.Min(control.Freshness - age, 60);
            var bypass = directive.Contains("no-cache") || directive == "no-store";
            var hit = control.Public && lifetime > elapsed && !bypass && (directive != "max-age=5" || age + elapsed <= 5) && (directive != "min-fresh=8" || lifetime - elapsed >= 8);
            var only = directive.Contains("only-if-cached");
            var second = await fixture.Send(context => { if (directive.Length > 0) context.Request.Headers.CacheControl = directive; });
            var expectedCalls = hit || only ? 1 : 2;
            Check(fixture.Calls == expectedCalls && second.Response.StatusCode == (!hit && only ? 504 : 200), $"Cache control={control.Header}; age={age}; elapsed={elapsed}; request={directive}: calls={fixture.Calls}, status={second.Response.StatusCode}, hit={hit}.");
            if (hit) Check(second.Response.Headers.Age == (age + elapsed).ToString(CultureInfo.InvariantCulture), "Cache hit returned an incorrect Age.");
            Check(fixture.Store.AllocatedBytes <= fixture.Store.SizeLimitBytes, "Generated cache case exceeded its budget.");
            await second.Response.Body.DisposeAsync(); count++;
        }
        foreach (var vary in new[] { "Accept-Language", "accept-language", "*", "X-Request-Id", "Cache-Control", "traceparent" })
        foreach (var changed in new[] { false, true })
        {
            var fixture = new Fixture { ResponseHeaders = context => context.Response.Headers.Vary = vary };
            var first = await fixture.Send(context => context.Request.Headers.AcceptLanguage = "en-US"); await first.Response.Body.DisposeAsync();
            var second = await fixture.Send(context => context.Request.Headers["accept-language"] = changed ? "da-DK" : "en-US");
            Check(fixture.Calls == (!changed && vary.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase) ? 1 : 2), "Vary selection crossed a representation boundary: " + vary);
            await second.Response.Body.DisposeAsync(); count++;
        }
        foreach (var invalid in new[] { "-1", "broken", "315360001", "999999999999999999999" })
        {
            var fixture = new Fixture { ResponseHeaders = context => context.Response.Headers.Age = invalid };
            await (await fixture.Send()).Response.Body.DisposeAsync(); await (await fixture.Send()).Response.Body.DisposeAsync();
            Check(fixture.Calls == 2, "Malformed Age was cached: " + invalid); count++;
        }
        foreach (var dateLag in new[] { -10, 0, 12 })
        foreach (var age in new[] { 0, 7 })
        {
            var fixture = new Fixture(); var origin = fixture.Clock.GetUtcNow();
            fixture.ResponseHeaders = context => { context.Response.Headers.CacheControl = "public"; context.Response.Headers.Date = origin.AddSeconds(-dateLag).ToString("R"); context.Response.Headers.Expires = origin.AddSeconds(20).ToString("R"); context.Response.Headers.Age = age.ToString(CultureInfo.InvariantCulture); };
            await (await fixture.Send()).Response.Body.DisposeAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            var response = await fixture.Send();
            Check(fixture.Calls == 1 && response.Response.Headers.Age == (Math.Max(age, dateLag) + 2).ToString(CultureInfo.InvariantCulture), "Date/Expires freshness lost apparent age.");
            await response.Response.Body.DisposeAsync(); count++;
        }
        Console.WriteLine($"Cache properties: {count} generated freshness and variant cases.");
    }
}
