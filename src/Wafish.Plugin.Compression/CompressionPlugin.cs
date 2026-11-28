namespace Wafish.Plugin.Compression;

public sealed class CompressionPlugin : IGatewayPlugin
{
    public string PluginId => "compression";
    public string DisplayName => "Response Compression";
    public string Version => "1.0";
    public PluginManifest Manifest => new(PluginId, DisplayName, Version, [PluginScope.Route], [PluginCapability.ProxyPipeline], 350,
        new PluginResourceRequirements(RequestMiddleware: true),
        [new PluginSchemaReference(1, "builtin://plugins/compression/schemas/config-v1.json", """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["enabled"],"properties":{"enabled":{"type":"boolean","default":true},"minResponseSize":{"type":"integer","minimum":0,"maximum":10485760,"default":1024,"description":"Minimum eligible response bytes. Unknown-length responses bypass compression when this exceeds 65536 bytes. Smaller thresholds buffer at most 65536 bytes; an early flush sends uncompressed data. Known lengths use the configured threshold."},"compressionLevel":{"type":"string","enum":["Optimal","Fastest","NoCompression"],"default":"Optimal"},"allowedEncodings":{"type":"array","maxItems":2,"uniqueItems":true,"items":{"type":"string","enum":["br","gzip"]},"description":"Omit to allow both encodings. An empty list disables compression."},"brotliCompressionLevel":{"type":"string","enum":["Optimal","Fastest","NoCompression"],"description":"Overrides compressionLevel for Brotli only."},"gzipCompressionLevel":{"type":"string","enum":["Optimal","Fastest","NoCompression"],"description":"Overrides compressionLevel for Gzip only."},"mimeTypes":{"type":"array","items":{"type":"string","minLength":1},"uniqueItems":true,"default":["text/plain","text/css","text/javascript","application/json","application/xml","text/xml","application/javascript","image/svg+xml"]}}}
        """)], "Route-scoped Gzip/Brotli response compression for compressible media types.");
    public void ConfigureServices(IServiceCollection services, object? pluginOptions = null) { }
    public void ConfigureMiddleware(IApplicationBuilder app) { }
    public void ConfigureProxyPipeline(IReverseProxyApplicationBuilder proxyPipeline) => proxyPipeline.UseMiddleware<CompressionMiddleware>();

    public void ConfigureDashboard(IPluginDashboardBuilder builder) { }
}
