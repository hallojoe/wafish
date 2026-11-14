using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;

internal static partial class ProtocolPropertyChecks
{
    private static async Task CompressionAsync()
    {
        var count = 0;
        async Task Verify(string header, string? expected, bool multipleLines = false)
        {
            var fixture = new Fixture(compression: true);
            var response = await fixture.Send(context => context.Request.Headers.AcceptEncoding = new Microsoft.Extensions.Primitives.StringValues(multipleLines ? header.Split(", ") : [header]));
            var actual = response.Response.Headers.ContentEncoding.ToString();
            Check(actual == (expected ?? ""), $"Accept-Encoding '{header}' selected '{actual}', expected '{expected}'.");
            Check(response.Response.Headers.Vary.ToString().Split(',').Count(value => value.Trim() == "Accept-Encoding") == 1, "Compression omitted or duplicated Vary.");
            response.Response.Body.Position = 0;
            using var output = new MemoryStream();
            using Stream decoder = actual switch { "br" => new BrotliStream(response.Response.Body, CompressionMode.Decompress, true), "gzip" => new GZipStream(response.Response.Body, CompressionMode.Decompress, true), _ => response.Response.Body };
            await decoder.CopyToAsync(output);
            Check(output.ToArray().SequenceEqual(Fixture.Payload), "Negotiated representation changed response bytes.");
            await response.Response.Body.DisposeAsync(); count++;
        }
        foreach (var br in new[] { 0m, .1m, .5m, 1m })
        foreach (var gzip in new[] { 0m, .1m, .5m, 1m })
        foreach (var wildcard in new[] { 0m, .7m, 1m })
        foreach (var reverse in new[] { false, true })
        {
            var b = "br;q=" + br.ToString(CultureInfo.InvariantCulture); var g = "gzip;q=" + gzip.ToString(CultureInfo.InvariantCulture);
            var value = (reverse ? g + ", " + b : b + ", " + g) + ", *;q=" + wildcard.ToString(CultureInfo.InvariantCulture);
            await Verify(value, br > 0 && br >= gzip ? "br" : gzip > 0 ? "gzip" : null);
        }
        foreach (var (header, selected) in new (string, string?)[]
        {
            ("", null), ("identity", null), ("deflate", null), ("gzip", "gzip"), ("BR", "br"), ("*", "br"),
            ("br;q=0, *;q=1", "gzip"), ("gzip;q=0, *;q=1", "br"), ("br;q=0, br;q=1, gzip;q=.5", null),
            ("br;q=0, br;q=1, gzip;q=0.5", "gzip"), ("gzip;q=0, gzip;q=1", null),
            ("*;q=0, *;q=1", null), ("gzip;q=1.000", "gzip"), ("x-gzip", null),
            ("gzip;q=2", null), ("br;q=-1", null), ("gzip;q=NaN", null), ("br;q=0.1234", null),
            ("gzip;q=bad, br", null), ("gzip;unknown=yes", null), ("br;q=", null), ("gzip;q=1;q=0", null),
            ("gzip;q=1.1", null), ("br;q=0.x", null), ("br;q=00", null), ("br;Q=0.5", "br"),
            ("gzip;q=0.", null), ("br;q=1.", "br"), ("gzip;q=0.001", "gzip"), ("gzip, @", null), ("br;q=\"0.5\"", null)
        }) await Verify(header, selected);
        await Verify("br;q=0.5, gzip;q=1", "gzip", multipleLines: true);
        await Verify("br;q=0, br;q=1, gzip;q=0.5", "gzip", multipleLines: true);
        Console.WriteLine($"Compression properties: {count} generated negotiation and decoding cases.");
    }
}
