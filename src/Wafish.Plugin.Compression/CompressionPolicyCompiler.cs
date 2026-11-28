using System.Text.Json;
using Wafish.Models;
using Wafish.Services;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.Compression;

/// <summary>Validates encoding policy candidates without creating a compressor.</summary>
public sealed class CompressionPolicyCompiler : IRoutePluginCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanCompile(string pluginId) => pluginId.Equals("compression", StringComparison.OrdinalIgnoreCase);
    public CompiledRoutePlugin Compile(PluginBindingSnapshot binding, RouteConfig route)
    {
        var config = CompressionEncodingPolicy.Compile(JsonSerializer.Deserialize<CompressionExecutionConfig>(binding.ConfigJson, Json)
            ?? throw new ArgumentException("Compression configuration is required."));
        if (binding.SchemaVersion != 1 && (config.AllowedEncodings != null || config.BrotliCompressionLevel != null || config.GzipCompressionLevel != null))
            throw new ArgumentException("Encoding restrictions and per-encoding levels require schema 1.");
        return new(binding, PluginExecutionStage.PostProxy, config, route);
    }
}
