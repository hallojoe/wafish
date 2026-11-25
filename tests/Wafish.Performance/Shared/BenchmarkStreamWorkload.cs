using System.Globalization;
using System.Text;

namespace Wafish.Performance;

internal sealed record BenchmarkStreamWorkload
{
    public string Kind { get; }
    public int Chunks { get; }
    public int ChunkBytes { get; }
    public int IntervalMs { get; }
    public int EncodedChunkBytes => ChunkBytes + (Kind == "events" ? 8 : 0);
    public int TotalBytes => EncodedChunkBytes * Chunks;
    public string Path => $"/api/perf/stream/{Kind}/{Chunks}/{ChunkBytes}/{IntervalMs}";
    public BenchmarkStreamWorkload(string kind, int chunks, int chunkBytes, int intervalMs)
    {
        if (kind is not ("chunks" or "events") || chunks is < 1 or > 256 || chunkBytes is < 1 or > 65536 ||
            intervalMs is < 0 or > 1000 || (long)(chunks - 1) * intervalMs > 4000 ||
            (long)(chunkBytes + (kind == "events" ? 8 : 0)) * chunks > 4 * 1024 * 1024)
            throw new ArgumentException("Stream bounds: chunks 1..256, bytes 1..65536, interval 0..1000 ms, total 4 MiB and 4 seconds.");
        Kind = kind; Chunks = chunks; ChunkBytes = chunkBytes; IntervalMs = intervalMs;
    }
    public byte[] CreateChunk() => Encoding.ASCII.GetBytes(Kind == "events" ? "data: " + new string('x', ChunkBytes) + "\n\n" : new string('x', ChunkBytes));
    public bool Matches(byte[] body)
    {
        if (body.Length != TotalBytes) return false;
        var chunk = CreateChunk();
        for (var offset = 0; offset < body.Length; offset += chunk.Length)
            if (!body.AsSpan(offset, chunk.Length).SequenceEqual(chunk)) return false;
        return true;
    }
    public static BenchmarkStreamWorkload ParsePath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length != 8 || parts[1] != "api" || parts[2] != "perf" || parts[3] != "stream")
            throw new ArgumentException("Invalid stream path.");
        return new(parts[4], int.Parse(parts[5], CultureInfo.InvariantCulture), int.Parse(parts[6], CultureInfo.InvariantCulture), int.Parse(parts[7], CultureInfo.InvariantCulture));
    }
}
