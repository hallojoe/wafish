using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SampleLocalService;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SampleOrderRequest(string ProductId, int Quantity);
public sealed record SampleOrder(Guid Id, string ProductId, int Quantity, DateTimeOffset CreatedAt);
public sealed record SampleOrderResult(SampleOrder Order, bool Created);
public sealed class SampleOrderConflictException : Exception;
public sealed class SampleOrderCapacityException(int retryAfter) : Exception
{
    public int RetryAfter { get; } = retryAfter;
}

/// <summary>One bounded synthetic ledger shared by local sample processes on the same filesystem.</summary>
public sealed class SampleOrderStore(string path, TimeProvider clock)
{
    private const int Capacity = 256;
    private const int MaxBytes = 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path = Path.GetFullPath(path);
    private sealed record Entry(string KeyHash, SampleOrder Order, DateTimeOffset ExpiresAt);

    public static bool ValidKey(string? key) => key is { Length: >= 1 and <= 64 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    public static bool Valid(SampleOrderRequest? request) => request is { ProductId.Length: >= 1 and <= 64, Quantity: >= 1 and <= 1000 } &&
        request.ProductId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public async Task<SampleOrderResult> CreateAsync(string key, SampleOrderRequest request, CancellationToken cancellationToken)
    {
        if (!ValidKey(key) || !Valid(request)) throw new ArgumentException("Use a bounded synthetic product and idempotency key.");
        using var lease = await AcquireAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var entries = Read().Where(entry => entry.ExpiresAt > now).ToList();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var existing = entries.SingleOrDefault(entry => entry.KeyHash == hash);
        if (existing is not null)
        {
            if (existing.Order.ProductId != request.ProductId || existing.Order.Quantity != request.Quantity) throw new SampleOrderConflictException();
            return new(existing.Order, false);
        }
        if (entries.Count >= Capacity)
            throw new SampleOrderCapacityException(Math.Max(1, (int)Math.Ceiling((entries.Min(entry => entry.ExpiresAt) - now).TotalSeconds)));
        var order = new SampleOrder(Guid.NewGuid(), request.ProductId, request.Quantity, now);
        entries.Add(new(hash, order, now + Retention));
        Write(entries, cancellationToken);
        return new(order, true);
    }

    public async Task<SampleOrder?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var lease = await AcquireAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return Read().SingleOrDefault(entry => entry.Order.Id == id && entry.ExpiresAt > now)?.Order;
    }

    private async Task<FileStream> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(_path + ".lock", options);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(10, cancellationToken); }
        }
    }

    private List<Entry> Read()
    {
        if (Directory.Exists(_path)) throw new IOException("Invalid sample order state.");
        if (!File.Exists(_path)) return [];
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxBytes) throw new IOException("Sample order state exceeds its limit.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IOException("Sample order state exceeds its limit.");
        var entries = JsonSerializer.Deserialize<List<Entry>>(bytes, Json) ?? throw new IOException("Invalid sample order state.");
        if (entries.Count > Capacity || entries.Any(entry => entry is null || entry.KeyHash is not { Length: 64 } ||
            entry.KeyHash.Any(c => !char.IsAsciiHexDigit(c)) || entry.Order is null || entry.Order.Id == Guid.Empty ||
            !Valid(new(entry.Order.ProductId, entry.Order.Quantity)) || entry.ExpiresAt - entry.Order.CreatedAt != Retention) ||
            entries.Select(entry => entry.KeyHash).Distinct().Count() != entries.Count || entries.Select(entry => entry.Order.Id).Distinct().Count() != entries.Count)
            throw new IOException("Invalid sample order state.");
        return entries;
    }

    private void Write(List<Entry> entries, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, Json);
        var temporary = _path + ".tmp"; // Exclusive lease also owns any interrupted temporary write.
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
