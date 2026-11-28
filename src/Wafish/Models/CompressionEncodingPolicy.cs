namespace Wafish.Models;

/// <summary>Validates optional encoding restrictions while preserving the shared compression-level default.</summary>
public static class CompressionEncodingPolicy
{
    public static CompressionExecutionConfig Compile(CompressionExecutionConfig config)
    {
        if (config.FlushAfterWrite && config.MinResponseSize != 0)
            throw new ArgumentException("flushAfterWrite requires minResponseSize: 0 to avoid prefix buffering.");
        if (config.AllowedEncodings is { } encodings &&
            (encodings.Length > 2 || encodings.Any(e => e is not ("br" or "gzip")) || encodings.Distinct(StringComparer.Ordinal).Count() != encodings.Length))
            throw new ArgumentException("allowedEncodings must contain unique br or gzip values, or an empty list.");
        if (!ValidLevel(config.BrotliCompressionLevel) || !ValidLevel(config.GzipCompressionLevel))
            throw new ArgumentException("Per-encoding compression levels must be Optimal, Fastest, or NoCompression.");
        return config with { AllowedEncodings = config.AllowedEncodings?.ToArray() };
    }
    private static bool ValidLevel(string? level) => level is null or "Optimal" or "Fastest" or "NoCompression";
}
