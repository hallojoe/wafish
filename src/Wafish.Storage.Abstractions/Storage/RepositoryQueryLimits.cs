namespace Wafish.Storage;

/// <summary>Shared bounds for history and log queries. These bounds do not change retention operations.</summary>
public static class RepositoryQueryLimits
{
    public const int MaximumPageSize = 500;
    public const int MaximumFilterLength = 1024;
    public static int Limit(int requested) => Math.Clamp(requested, 1, MaximumPageSize);
    public static long Offset(int page, int pageSize) => ((long)Math.Max(1, page) - 1) * Limit(pageSize);
    public static void ValidateFilter(string? value, string parameterName)
    {
        if (value?.Length > MaximumFilterLength)
            throw new ArgumentException($"Filter must not exceed {MaximumFilterLength} characters.", parameterName);
    }
}
